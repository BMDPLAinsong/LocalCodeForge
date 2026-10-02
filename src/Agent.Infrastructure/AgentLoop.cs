// ============================================================
// AgentLoop.cs
// Agent 主循环。ReAct + 三层路由 + 首轮决策缓存 + 防死循环 + 失败修复。
// ============================================================

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.Extensions.Logging;
using Agent.Core.Abstractions;
using Agent.Core.Models;
using Agent.Infrastructure.Memory;
using Agent.Infrastructure.Project;
using Agent.Infrastructure.Routing;
using Agent.Infrastructure.Quality;

namespace Agent.Infrastructure;

/// <summary>
/// SK 版 ReAct 循环实现。
/// </summary>
public sealed class AgentLoop : IAgentLoop
{
    private readonly Kernel _kernel;
    private readonly IToolRegistry _tools;
    private readonly ISessionStore _store;
    private readonly AgentOptions _options;
    private readonly IModelRouter _router;
    private readonly ProjectContextStore _projectContextStore;   // ← P2：工程上下文缓存
    private readonly AgentMemoryStore _memoryStore;               // ← P3：模型级记忆
    private readonly ILogger<AgentLoop> _logger;

    // 方案 2 循环熔断：本轮已成功执行的工具签名（工具名+首参+输出指纹）
    private readonly HashSet<string> _toolSignatures = new();

    private string? _lastToolName;
    private bool _lastStepFailed;

    public AgentLoop(
        Kernel kernel,
        IToolRegistry tools,
        ISessionStore store,
        AgentOptions options,
        IModelRouter router,
        ProjectContextStore projectContextStore,   // ← P2：工程上下文缓存
        AgentMemoryStore memoryStore,              // ← P3：模型级记忆
        ILogger<AgentLoop> logger)
    {
        _kernel = kernel;
        _tools = tools;
        _store = store;
        _options = options;
        _router = router;
        _projectContextStore = projectContextStore;
        _memoryStore = memoryStore;
        _logger = logger;
    }

    /// <summary>
    /// 工程模式加载注入块（工程感知 P2 跨任务记忆）：
    /// 轻量签名校验缓存——工程未变化时复用 ProjectContextStore 的 CompactBlock，
    /// 避免每任务重建上下文块；文件被修改（签名变化）自动重新扫描并更新缓存。
    /// 注入的是瘦身版（概要+目录聚合），模型需要文件详情时 list_dir/read_file 自取。
    /// </summary>
    private string? LoadProjectCompactBlock(string root)
    {
        try
        {
            var (fileCount, dirCount, totalBytes, maxMtimeTicks) =
                Agent.Infrastructure.Project.ProjectScanner.ComputeSignature(root);
            var cached = _projectContextStore.TryGet(root, fileCount, dirCount, totalBytes, maxMtimeTicks);
            if (cached is not null)
            {
                _logger.LogInformation("[工程上下文] 缓存命中（签名一致），复用 CompactBlock。");
                return cached.CompactBlock;
            }

            var scan = Agent.Infrastructure.Project.ProjectScanner.Scan(root);
            _projectContextStore.Put(root, scan);
            _logger.LogInformation("[工程上下文] 缓存未命中，重新扫描并写入缓存。");
            return scan.CompactBlock;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "工程上下文加载失败，降级为无工程块。");
            return null;
        }
    }

    /// <summary>
    /// 模型级记忆检索（工程感知 P3）：按任务输入关键词匹配 AgentMemoryStore，
    /// 命中则注入"历史记忆"块——任务间真正"记住"模型此前发现/决策。
    /// </summary>
    private string? LoadRelevantMemory(string input)
    {
        try
        {
            var hits = _memoryStore.Search(input, limit: 3);
            if (hits.Count == 0)
                return null;

            _logger.LogInformation("[记忆] 历史记忆命中 {Count} 条，已注入上下文。", hits.Count);
            return Agent.Infrastructure.Memory.MemoryPromptBuilder.Build(hits);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "历史记忆检索失败，降级为无记忆块。");
            return null;
        }
    }

    /// <summary>执行一次任务，返回事件流。</summary>
    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AgentRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // 【关键】重置跨任务状态
        _lastStepFailed = false;
        _lastToolName = null;

        // 【新增】模糊输入检测
        var vaguePatterns = new[]
        {
            "写一个函数", "写个函数", "写一个方法", "写个方法",
            "写代码", "帮我写", "帮我做", "帮我搞", "写个程序"
        };

        var isVagueInput = request.Input.Length <= 10 &&
                          vaguePatterns.Any(p => request.Input.Trim() == p ||
                                                 request.Input.Trim() == p + "。" ||
                                                 request.Input.Trim() == p + ".");

        if (isVagueInput)
        {
            var hint = "你的需求不够明确。请具体描述：这个函数要做什么？输入是什么？输出是什么？";
            await _store.AppendTurnAsync(request.SessionId, new ChatTurn("assistant", hint), ct);
            yield return new AgentEvent.FinalAnswer(hint);
            yield break;
        }

        // 载入历史
        var history = await _store.GetHistoryAsync(request.SessionId, _options.MaxHistoryTurns, ct);

        // 检测新任务 → 清空历史上下文
        if (IsNewTask(request.Input))
        {
            _logger.LogInformation("检测到新任务，清空历史上下文。");
            history = Array.Empty<ChatTurn>();
        }

        // 构造 SK 的 ChatHistory
        var chatHistory = new ChatHistory();

        chatHistory.AddSystemMessage(
            $"{_options.SystemPromptText}\n\n## 工作区\n当前工作区根目录：{request.WorkspaceRoot}\n所有文件操作必须在此目录内。");

        foreach (var turn in history)
        {
            if (turn.Role == "user")
                chatHistory.AddUserMessage(turn.Content);
            else
                chatHistory.AddAssistantMessage(turn.Content);
        }

        // 注入外部数据任务的环境约束（若命中）：让模型在生成阶段就知道环境事实
        var envConstraint = ExternalTaskDetector.Detect(request.Input);

        // 工程感知 P2：跨任务记忆——扫描（取文件系统签名）→ 缓存命中复用 CompactBlock
        // （注入瘦身版：概要+目录聚合；模型需要文件详情时 list_dir/read_file 自取。
        //   文件被修改（签名变化）自动重新扫描，保证正确性。）
        var projectBlock = _options.ProjectRoot is null ? null : LoadProjectCompactBlock(_options.ProjectRoot);

        // 工程感知 P3：模型级记忆——按任务输入检索历史记忆（模型 previous 发现/决策），注入供参考
        var memoryBlock = LoadRelevantMemory(request.Input);

        var userMsg = request.Input;
        if (envConstraint is not null) userMsg += "\n\n" + envConstraint;
        if (projectBlock is not null) userMsg += "\n\n" + projectBlock;
        if (memoryBlock is not null) userMsg += "\n\n" + memoryBlock;
        // agent_pro 工程理解专项：读/分析意图（确定性词表）→ 引导优先用符号地图，
        // 避免模型逐行 read_file 探索两步链（4B 生成不稳的根源）。
        if (projectBlock is not null && Agent.Infrastructure.Routing.IntentOverride.IsReadTask(request.Input))
            userMsg += "\n\n（工程提示：理解代码结构请优先调用 analyze_code 获取类/方法/行号索引，再按需 read_file 读具体行段——符号地图比逐行探索高效。）";
        chatHistory.AddUserMessage(userMsg);
        await _store.AppendTurnAsync(request.SessionId, new ChatTurn("user", request.Input), ct);

        // 注册工具 Plugin
        var plugin = BuildPlugin();
        if (!_kernel.Plugins.Contains(plugin.Name))
            _kernel.Plugins.Add(plugin);

        RoutingDecision? firstDecision = null;
        RoutingDecision? lastYieldedDecision = null;
        int forceContinueCount = 0;
        int emptyAnswerCount = 0;
        string? _lastFailureSummary = null; // 上下文治理：最近一次失败的工具输出摘要（注入用截断版）

        var historyStartIndex = chatHistory.Count;

        // ═══════════ ReAct 主循环 ═══════════
        for (var step = 0; step < _options.MaxSteps; step++)
        {
            // ─────── 1. 路由决策 ───────
            string serviceId;
            RoutingDecision decision;

            if (_options.EnableRouting)
            {
                var routingContext = new RoutingContext(
                    UserInput: request.Input,
                    Step: step,
                    LastToolName: _lastToolName,
                    LastStepFailed: _lastStepFailed,
                    HistoryTurns: chatHistory.Count);

                if (step == 0 || firstDecision is null)
                {
                    decision = await _router.RouteAsync(routingContext, ct);
                    firstDecision = decision;
                }
                else if (_lastStepFailed)
                {
                    // 【上下文治理 10-01】recovery 重路由前重建精简上下文：
                    // 删除中间工具往返历史（上下文膨胀源——大段 stdout/stderr 进历史），
                    // 保留需求+环境约束（historyStartIndex 之前），追加失败摘要。
                    // 目的：让 agent_pro 在干净上下文里推理——4B 上下文饱和是空响应根因
                    // （agent_pro 14 条失败中 11 条空响应，全在 recovery 承接后）。
                    // 模型如需当前文件内容会自行 read_file（ReAct 循环内可自取）。
                    if (chatHistory.Count > historyStartIndex)
                    {
                        chatHistory.RemoveRange(historyStartIndex, chatHistory.Count - historyStartIndex);
                    }

                    if (!string.IsNullOrEmpty(_lastFailureSummary))
                    {
                        chatHistory.AddUserMessage(
                            $"上一步失败（工具 {_lastToolName}）：\n{_lastFailureSummary}\n\n请根据失败原因继续完成任务。");
                    }

                    decision = await _router.RouteAsync(routingContext, ct);
                    firstDecision = decision;
                }
                else
                {
                    decision = firstDecision;
                }

                serviceId = decision.ServiceId;
            }
            else
            {
                serviceId = RoutingDecision.Large;
                decision = new RoutingDecision(serviceId, RoutingTier.LargeModel, "路由未启用");
            }

            // ─────── 2. 发出路由事件 ───────
            var decisionChanged = lastYieldedDecision is null
                || lastYieldedDecision.ServiceId != decision.ServiceId
                || lastYieldedDecision.Reason != decision.Reason;

            if (decisionChanged)
            {
                yield return new AgentEvent.Routing(
                    decision.ServiceId,
                    decision.Tier.ToString(),
                    decision.Reason,
                    decision.Intent?.ToString());

                lastYieldedDecision = decision;
            }

            // ─────── 3. 取对应 Chat 服务 ───────
            var chat = _kernel.GetRequiredService<IChatCompletionService>(serviceId);

            // ─────── 4. 构造 settings ───────
            var settings = new OpenAIPromptExecutionSettings
            {
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(autoInvoke: false),
                Temperature = _options.Temperature,
                ExtensionData = new Dictionary<string, object>
                {
                    ["think"] = false
                }
            };

            // ─────── 5. 调用 LLM ───────
            ChatMessageContent? response = null;
            string? errorMessage = null;
            try
            {
                response = await chat.GetChatMessageContentAsync(chatHistory, settings, _kernel, ct);
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
            }

            if (errorMessage is not null)
            {
                yield return new AgentEvent.Error($"LLM 调用失败：{errorMessage}");
                yield break;
            }
            if (response is null)
            {
                yield return new AgentEvent.Error("LLM 返回了空响应。");
                yield break;
            }

            // ─────── 6. 收集结构化函数调用 ───────
            var functionCalls = response.Items
                .OfType<FunctionCallContent>()
                .ToList();

            // ═══════════ 情况 A：结构化函数调用 ═══════════
            if (functionCalls.Count > 0)
            {
                forceContinueCount = 0;
                emptyAnswerCount = 0;
                chatHistory.Add(response);

                foreach (var call in functionCalls)
                {
                    var cleanName = StripPrefix(call.FunctionName);
                    var argsJson = ToJsonElement(call.Arguments);

                    yield return new AgentEvent.ToolCall(cleanName, argsJson);

                    var (output, ok) = await ExecuteToolAsync(cleanName, argsJson, request.Input, ct);
                    yield return new AgentEvent.ToolResultEvent(cleanName, output, ok);

                    _lastToolName = cleanName;
                    _lastStepFailed = !ok && !output.StartsWith("错误：工具");

                    // 上下文治理：仅 run_command 截断（大段 stdout/stderr 膨胀源）；
                    // read_file 保留完整——edit_file 精确匹配依赖完整文件内容，截断会破坏修复
                    var condensed = cleanName == "run_command" ? SummarizeOutput(output) : output;
                    _lastFailureSummary = condensed;

                    // ── 方案 2：循环熔断（工具序列重复且无新进展 → 以工具验证收尾）──
                    // 写任务 large 常"写了又写/验了又验"不交付最终回答（达最大步数失败）。
                    // 同一工具+首参+输出指纹第二次成功执行 = 重复无进展 → 强制收尾。
                    if (ok)
                    {
                        var sig = BuildCycleSignature(cleanName, argsJson, condensed);
                        if (!_toolSignatures.Add(sig))
                        {
                            _logger.LogWarning("[熔断] 工具序列重复无进展（{Sig}），以工具验证结果收尾。", sig);
                            var closeText = $"任务完成：工具已验证（{cleanName}）。最后验证：{TruncateForAnswer(condensed)}";
                            await _store.AppendTurnAsync(request.SessionId, new ChatTurn("assistant", closeText), ct);
                            yield return new AgentEvent.FinalAnswer(closeText);
                            yield break;
                        }
                    }

                    var frc = new FunctionResultContent(
                        functionName: cleanName,
                        pluginName: "_",
                        callId: call.Id,
                        result: condensed);
                    chatHistory.Add(frc.ToChatMessage());
                }
                continue;
            }

            // ═══════════ 情况 B：文本兜底 ═══════════
            var text = response.Content ?? string.Empty;
            var pseudoCalls = TryParseToolCallsFromText(text);

            if (pseudoCalls.Count > 0)
            {
                forceContinueCount = 0;
                emptyAnswerCount = 0;

                var synthetic = new List<(FunctionCallContent fcc, PseudoCall pc)>();

                foreach (var pc in pseudoCalls)
                {
                    var callId = Guid.NewGuid().ToString("N");
                    var cleanName = StripPrefix(pc.Name);
                    var cleanPc = new PseudoCall(cleanName, pc.Args);

                    var argsDict = new Dictionary<string, object?>();
                    if (pc.Args.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in pc.Args.EnumerateObject())
                        {
                            argsDict[prop.Name] = prop.Value.ValueKind switch
                            {
                                JsonValueKind.String => prop.Value.GetString(),
                                JsonValueKind.Number => prop.Value.GetDouble(),
                                JsonValueKind.True => (object?)true,
                                JsonValueKind.False => (object?)false,
                                JsonValueKind.Null => null,
                                _ => prop.Value.GetRawText()
                            };
                        }
                    }

                    var kernelArgs = new KernelArguments(argsDict);

                    var fcc = new FunctionCallContent(
                        functionName: cleanName,
                        pluginName: "_",
                        arguments: kernelArgs,
                        id: callId);

                    synthetic.Add((fcc, cleanPc));
                }

                var items = new ChatMessageContentItemCollection();
                foreach (var (fcc, _) in synthetic)
                    items.Add(fcc);

                chatHistory.Add(new ChatMessageContent(AuthorRole.Assistant, items));

                foreach (var (fcc, pc) in synthetic)
                {
                    yield return new AgentEvent.ToolCall(pc.Name, pc.Args);

                    var (output, ok) = await ExecuteToolAsync(pc.Name, pc.Args, request.Input, ct);
                    yield return new AgentEvent.ToolResultEvent(pc.Name, output, ok);

                    _lastToolName = pc.Name;
                    _lastStepFailed = !ok && !output.StartsWith("错误：工具");
                    var condensed = pc.Name == "run_command" ? SummarizeOutput(output) : output;
                    _lastFailureSummary = condensed;

                    var frc = new FunctionResultContent(
                        functionName: pc.Name,
                        pluginName: "_",
                        callId: fcc.Id,
                        result: condensed);
                    chatHistory.Add(frc.ToChatMessage());
                }

                continue;
            }

            // ═══════════ 情况 C：真正的最终答案 ═══════════

            // 【B】空最终答案兜底：模型返回空内容不得静默交付
            // （会话库曾记录空 assistant 轮 + 静默"[完成]"）
            if (string.IsNullOrWhiteSpace(text))
            {
                emptyAnswerCount++;
                if (emptyAnswerCount >= 2)
                {
                    await _store.AppendTurnAsync(request.SessionId, new ChatTurn("assistant", text), ct);
                    yield return new AgentEvent.Error("模型连续返回空答案，任务终止。");
                    yield break;
                }

                // 【B2】空响应升级链（P2-3）：同模型已空一次，再同模型重试收益有限——
                // 强制升级服务（small → agent_pro → large），每层至多尝试一次；
                // 已到最高层（large）则原层强制重试（此前实测 10-01 多个任务在
                // small/agent_pro 连续空响应后直接终止，升级给了大模型一次机会）。
                var promoted = PromoteService(decision.ServiceId);
                if (promoted is not null)
                {
                    _logger.LogWarning(
                        "模型返回空内容，升级服务 {From} → {To} 重试。",
                        decision.ServiceId, promoted);
                    decision = new RoutingDecision(
                        promoted,
                        RoutingTier.LargeModel,
                        $"空响应升级（{decision.ServiceId} → {promoted}）");
                    firstDecision = decision; // 下一轮循环复用升级后的决策
                    // 【修复 10-01】空响应升级是主动决策：重置 _lastStepFailed，
                    // 避免下一轮被 recovery 规则（recovery 只该响应工具失败）重路由回原层，
                    // 导致升级白升（16:56 天气任务实证：升级 large 后 recovery→agent_pro→空响应→终止）。
                    _lastStepFailed = false;
                }
                else
                {
                    _logger.LogWarning("模型返回空内容（已在最高层），原服务强制重试。");
                }

                chatHistory.AddUserMessage(
                    "你刚才没有输出任何内容。请继续：要么调用工具完成用户需求，要么给出明确的最终回答。不要返回空内容。");
                continue;
            }

            var looksLikeCodeRequest =
                request.Input.Contains("写") ||
                request.Input.Contains("创建") ||
                request.Input.Contains("生成") ||
                request.Input.Contains("实现") ||
                request.Input.Contains("修改") ||
                request.Input.Contains("再加") ||
                request.Input.Contains("添加") ||
                request.Input.Contains("新增") ||
                request.Input.Contains("增加") ||
                request.Input.Contains("扩展") ||
                request.Input.Contains("补充") ||
                request.Input.Contains("加上") ||
                request.Input.Contains("加入") ||
                request.Input.Contains("改成") ||
                request.Input.Contains("改为") ||
                request.Input.Contains("换成");

            var mustRun =
                request.Input.Contains("运行") ||
                request.Input.Contains("执行") ||
                request.Input.Contains("比较") ||
                request.Input.Contains("性能") ||
                request.Input.Contains("输出") ||
                request.Input.Contains("打印") ||
                request.Input.Contains("结果") ||
                request.Input.Contains("演示");

            var looksLikeDesignRequest =
                request.Input.Contains("设计") ||
                request.Input.Contains("规划") ||
                request.Input.Contains("架构") ||
                request.Input.Contains("分析") ||
                request.Input.Contains("方案");

            // 纯设计类任务 → 直接交付
            if (looksLikeDesignRequest && !looksLikeCodeRequest)
            {
                await _store.AppendTurnAsync(request.SessionId, new ChatTurn("assistant", text), ct);
                yield return new AgentEvent.FinalAnswer(text);
                yield break;
            }

            // 收集【本轮内】已调用的工具
            var calledTools = chatHistory
                .Skip(historyStartIndex)
                .SelectMany(m => m.Items.OfType<FunctionCallContent>())
                .Select(fc => StripPrefix(fc.FunctionName))
                .ToList();

            var hasWrittenFile = calledTools.Any(n =>
                n == "write_file" || n == "edit_file");
            var hasRunCommand = calledTools.Any(n => n == "run_command");

            // ═══════════ 业务合理性检查 ═══════════
            var codeRequestKeywords = new[]
            {
                "实现", "编写", "写一个", "写个", "创建", "生成",
                "取整", "计算", "算法", "函数", "方法", "类"
            };

            var isCodeRequest = codeRequestKeywords.Any(k =>
                request.Input.Contains(k, StringComparison.OrdinalIgnoreCase));

            if (isCodeRequest && hasRunCommand)
            {
                var lastRunOutput = chatHistory
                    .Skip(historyStartIndex)
                    .Where(m => m.Role == AuthorRole.Tool && m.Content != null)
                    .Select(m => m.Content!)
                    .LastOrDefault(m => m.Contains("退出码："));

                if (lastRunOutput is not null)
                {
                    var stdoutStart = lastRunOutput.IndexOf("--- stdout ---");
                    var stdout = "";

                    if (stdoutStart >= 0)
                    {
                        var stdoutEnd = lastRunOutput.IndexOf("--- stderr ---", stdoutStart);
                        stdout = stdoutEnd > 0
                            ? lastRunOutput.Substring(stdoutStart, stdoutEnd - stdoutStart)
                            : lastRunOutput.Substring(stdoutStart);

                        stdout = stdout.Replace("--- stdout ---", "").Trim();
                    }

                    var stdoutLines = stdout
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .ToArray();

                    var looksLikeTemplateOutput =
                        stdoutLines.Length <= 1 &&
                        (stdout.Contains("Hello", StringComparison.OrdinalIgnoreCase) ||
                         stdout.Contains("World", StringComparison.OrdinalIgnoreCase));

                    if (looksLikeTemplateOutput && step < _options.MaxSteps - 1)
                    {
                        forceContinueCount++;
                        if (forceContinueCount >= 3)
                        {
                            await _store.AppendTurnAsync(request.SessionId, new ChatTurn("assistant", text), ct);
                            yield return new AgentEvent.Error(
                                "任务未真正完成：输出只是 Hello World 模板，未实现用户需求。");
                            yield break;
                        }

                        _logger.LogWarning("检测到疑似模板输出（Hello World），强制继续。");

                        chatHistory.Add(new ChatMessageContent(AuthorRole.Assistant, text));
                        chatHistory.AddUserMessage(
                            "你当前交付的代码只输出了 Hello World，没有实现用户要求的功能。\n" +
                            "如果任务实际已完成（功能已实现并通过验证），请直接给出最终总结收尾；\n" +
                            "否则请重新实现：用 edit_file 或 write_file 把真正的功能加进去，然后重新 run_command 验证。\n" +
                            "用户需求：" + request.Input);
                        continue;
                    }
                }
            }

            // 检查【本轮内】最近一次 Tool 消息是否 run_command 失败
            var lastRunFailed = false;
            for (int i = chatHistory.Count - 1; i >= historyStartIndex; i--)
            {
                var msg = chatHistory[i];
                if (msg.Role == AuthorRole.Tool && msg.Content != null)
                {
                    if (msg.Content.Contains("退出码：") && !msg.Content.Contains("退出码：0"))
                    {
                        lastRunFailed = true;
                    }
                    break;
                }
                if (msg.Role == AuthorRole.User) break;
            }

            // 规则 1：代码请求但没写文件 → 强制写
            if (looksLikeCodeRequest && !hasWrittenFile && step < _options.MaxSteps - 1)
            {
                forceContinueCount++;
                if (forceContinueCount >= 3)
                {
                    await _store.AppendTurnAsync(request.SessionId, new ChatTurn("assistant", text), ct);
                    yield return new AgentEvent.Error("模型连续 3 次拒绝写文件，任务终止。");
                    yield break;
                }

                chatHistory.Add(new ChatMessageContent(AuthorRole.Assistant, text));
                chatHistory.AddUserMessage(
                    "你没有调用 write_file 或 edit_file。请立即调用工具把代码写入文件。不要直接输出代码。");
                continue;
            }

            // 规则 2.5：只调用了 build，没调 run → 强制 run
            var onlyBuiltNotRun = hasRunCommand &&
                chatHistory.Skip(historyStartIndex)
                    .Where(m => m.Role == AuthorRole.Tool && m.Content != null)
                    .Any(m => m.Content.Contains("dotnet build")) &&
                !chatHistory.Skip(historyStartIndex)
                    .Where(m => m.Role == AuthorRole.Tool && m.Content != null)
                    .Any(m => m.Content.Contains("dotnet run"));

            if (mustRun && onlyBuiltNotRun && step < _options.MaxSteps - 1)
            {
                forceContinueCount++;
                if (forceContinueCount >= 3)
                {
                    await _store.AppendTurnAsync(request.SessionId, new ChatTurn("assistant", text), ct);
                    yield return new AgentEvent.Error("模型只编译未运行，任务终止。");
                    yield break;
                }

                chatHistory.Add(new ChatMessageContent(AuthorRole.Assistant, text));
                chatHistory.AddUserMessage(
                    "用户要求'比较性能 / 看输出 / 演示结果'，你只调用了 `dotnet build`，**没有运行程序**。\n" +
                    "请立即调用 `dotnet run --project xxx.csproj` 运行程序，让用户看到实际输出。");
                continue;
            }

            // 规则 2：写了文件但没运行 → 强制运行
            if (looksLikeCodeRequest && hasWrittenFile && !hasRunCommand && step < _options.MaxSteps - 1)
            {
                forceContinueCount++;
                if (forceContinueCount >= 3)
                {
                    await _store.AppendTurnAsync(request.SessionId, new ChatTurn("assistant", text), ct);
                    yield return new AgentEvent.Error("模型连续 3 次拒绝调用 run_command，任务终止。");
                    yield break;
                }

                chatHistory.Add(new ChatMessageContent(AuthorRole.Assistant, text));
                chatHistory.AddUserMessage(
                    "你已经写了文件但没有调用 run_command。请立即调用 run_command 运行刚写的文件。不要直接交付。");
                continue;
            }

            // 规则 3：运行失败了 → 强制修复
            if (looksLikeCodeRequest && lastRunFailed && step < _options.MaxSteps - 1)
            {
                forceContinueCount++;
                if (forceContinueCount >= 3)
                {
                    await _store.AppendTurnAsync(request.SessionId, new ChatTurn("assistant", text), ct);
                    yield return new AgentEvent.Error("模型连续 3 次未能修复运行错误，任务终止。");
                    yield break;
                }

                chatHistory.Add(new ChatMessageContent(AuthorRole.Assistant, text));
                chatHistory.AddUserMessage(
                    "上一次 run_command 执行失败（退出码非 0）。请仔细阅读错误信息，修改代码后重新运行。**不要谎报成功。**");
                continue;
            }

            // 真正交付
            await _store.AppendTurnAsync(request.SessionId, new ChatTurn("assistant", text), ct);
            yield return new AgentEvent.FinalAnswer(text);
            yield break;
        }

        yield return new AgentEvent.Error($"达到最大步数 {_options.MaxSteps}，任务终止。");
    }

    /// <summary>剥离工具名前缀。</summary>
    private static string StripPrefix(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        var dashIndex = name.IndexOf('-');
        if (dashIndex > 0 && dashIndex < name.Length - 1)
        {
            return name[(dashIndex + 1)..];
        }

        return name;
    }

    /// <summary>执行一个工具，返回（输出, 是否成功）。</summary>
    private async Task<(string output, bool ok)> ExecuteToolAsync(string name, JsonElement args, string taskInput, CancellationToken ct)
    {
        try
        {
            var originalName = name;
            IAgentTool? tool = null;

            // 循环剥前缀直到找到工具
            while (tool is null)
            {
                if (_tools.TryGet(name, out var found))
                {
                    tool = found;
                    break;
                }

                var dashIndex = name.IndexOf('-');
                if (dashIndex <= 0 || dashIndex >= name.Length - 1)
                {
                    return ($"错误：工具 {originalName} 不存在。", false);
                }

                name = name[(dashIndex + 1)..];
            }

            var output = await tool.InvokeAsync(args, ct);

            // ─────────── 特殊处理 run_command ───────────
            if (name == "run_command")
            {
                // 【修复】工具层拒绝（超时/白名单）直接判失败
                // 超时的输出以"错误"开头且不带退出码，原逻辑会误判为成功
                if (output.StartsWith("错误", StringComparison.Ordinal))
                {
                    return (output, false);
                }

                // ─────────── 检查 0.5：外部凭据失败（401/403/API key） ───────────
                // 公共判定由 OutputVerifier.HasCredentialFailure 提供（双路径统一，单一来源）。
                if (OutputVerifier.HasCredentialFailure(output, out var credentialPattern))
                {
                    _logger.LogWarning(
                        "运行输出含外部凭据失败信号「{Pattern}」，归因为需要 API Key。",
                        credentialPattern);

                    return (output + "\n[检测] 错误原因是外部服务需要 API Key/凭据，但当前环境没有可用凭据。" +
                            "请改用免 Key 实现，可直接参考以下可运行代码（无需注册任何密钥）：\n" +
                            "import requests\n" +
                            "def get_weather_by_ip(ip):\n" +
                            "    loc = requests.get(f'http://ip-api.com/json/{ip}').json()\n" +
                            "    w = requests.get(f'https://wttr.in/{loc[\"city\"]}?format=j1').json()\n" +
                            "    return w['current_condition'][0]", false);
                }

                if (output.Contains("退出码："))
            {
                // 检查 1：退出码
                var match = Regex.Match(output, @"退出码：(\d+)");
                if (match.Success
                    && int.TryParse(match.Groups[1].Value, out var code)
                    && code != 0)
                {
                    return (output, false);
                }

                // 提取 stdout 和 stderr
                var stdoutStart = output.IndexOf("--- stdout ---");
                var stderrStart = output.IndexOf("--- stderr ---");

                var stdout = "";
                var stderr = "";

                if (stdoutStart >= 0)
                {
                    var stdoutEnd = stderrStart > stdoutStart ? stderrStart : output.Length;
                    stdout = output.Substring(stdoutStart, stdoutEnd - stdoutStart)
                        .Replace("--- stdout ---", "").Trim();
                }

                if (stderrStart >= 0)
                {
                    stderr = output.Substring(stderrStart)
                        .Replace("--- stderr ---", "").Trim();
                }

                // ─────────── 检查 2：stderr 里的错误（强失败） ───────────
                // stderr 非空即失败（程序正常不该往 stderr 写东西）；
                // 无害警告剥离由 OutputVerifier.StripHarmlessWarnings 提供（双路径统一，单一来源）。
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    var stderrCheck = OutputVerifier.StripHarmlessWarnings(stderr);

                    if (!string.IsNullOrWhiteSpace(stderrCheck))
                    {
                        return (output, false);
                    }
                    // 剥离后 stderr 只剩无害警告 → 不判失败，继续后续检查
                }

                // ─────────── 检查 3：stdout 里的"结构性错误" ───────────
                // 只匹配"程序崩溃"类的信号，不匹配"程序自己打印的 Error:"
                // 公共判定由 OutputVerifier.HasStructuralError 提供（双路径统一，信号集取并集）。
                if (OutputVerifier.HasStructuralError(stdout, out _))
                {
                    return (output, false);
                }

                // ─────────── 检查 4：输出有效性（占位符 / 模板） ───────────
                // 公共判定由 OutputVerifier.IsPlaceholderOutput / IsTemplateOutput 提供（双路径统一）。
                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    var stdoutLines = stdout
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .ToArray();

                    // 4.1 占位符检测
                    if (stdoutLines.Length > 0)
                    {
                        // 占位 API Key 强信号：模型常写 OpenWeatherMap 等需注册服务的占位密钥
                        if (OutputVerifier.HasPlaceholderKey(stdoutLines, out var placeholderKeyPattern))
                        {
                            _logger.LogWarning("输出含占位 API Key 信号（{Pattern}），判为未完成实现。", placeholderKeyPattern);
                            return (output + "\n[检测] 输出含占位 API Key（如 YOUR_API_KEY），程序未真正实现。" +
                                    "请改用免 Key 方案（wttr.in / 本地模拟），不要使用需要注册密钥的 API。", false);
                        }

                        if (OutputVerifier.IsPlaceholderOutput(stdoutLines, out var placeholderCount, out var placeholderTotal))
                        {
                            _logger.LogWarning(
                                "输出疑似占位符（{Count}/{Total} 行）",
                                placeholderCount, placeholderTotal);

                            return (output + "\n[检测] 输出疑似占位符。", false);
                        }
                    }

                    // 4.2 模板输出检测（方案 1 降级：任务要求的内容不判模板）
                    if (OutputVerifier.IsTemplateOutput(stdoutLines) &&
                        !OutputVerifier.IsTaskExpectedOutput(taskInput, stdoutLines))
                    {
                        return (output + "\n[检测] 输出疑似模板内容。", false);
                    }

                    // ─────────── 检查 3.5：stdout 通用失败判定 ───────────
                    // 通用失败词（失败/failed/error/JSON 字段）+ 成功信号容错；
                    // 公共判定由 OutputVerifier.HasGenericFailure 提供（双路径统一，单一来源）。
                    if (OutputVerifier.HasGenericFailure(stdout, null, out var genericFailureReason))
                    {
                        return (output + "\n[检测] 程序输出了失败结果（" + genericFailureReason + "），但没有成功输出。请检查逻辑并修复。", false);
                    }
                }
            }

            return (output, true);
            }

            return (output, true);
        }
        catch (Exception ex)
        {
            return ($"工具执行异常：{ex.Message}", false);
        }
    }

    /// <summary>从 LLM 文本里解析伪工具调用。</summary>
    private static List<PseudoCall> TryParseToolCallsFromText(string text)
    {
        var result = new List<PseudoCall>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var cleaned = Regex.Replace(
            text,
            @"```(?:json)?\s*([\s\S]*?)```",
            "$1",
            RegexOptions.IgnoreCase);

        var i = 0;
        while (i < cleaned.Length)
        {
            if (cleaned[i] == '{')
            {
                var end = FindMatchingBrace(cleaned, i);
                if (end > i)
                {
                    var candidate = cleaned.Substring(i, end - i + 1);
                    if (TryParseSingleCall(candidate, out var call))
                        result.Add(call);
                    i = end + 1;
                    continue;
                }
            }
            i++;
        }

        return result;
    }

    /// <summary>找到与 start 位置 { 匹配的 } 位置。</summary>
    private static int FindMatchingBrace(string s, int start)
    {
        var depth = 0;
        var inString = false;
        var escape = false;

        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (escape) { escape = false; continue; }
            if (c == '\\') { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;

            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    /// <summary>尝试把一段 JSON 解析成工具调用。</summary>
    private static bool TryParseSingleCall(string json, out PseudoCall call)
    {
        call = null!;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("name", out var nameProp)) return false;

            var name = nameProp.GetString();
            if (string.IsNullOrEmpty(name)) return false;

            JsonElement args;
            if (root.TryGetProperty("arguments", out var argsProp))
                args = argsProp.Clone();
            else
                args = JsonDocument.Parse("{}").RootElement.Clone();

            call = new PseudoCall(name, args);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把 IAgentTool 集合包装成 SK 的 KernelPlugin。</summary>
    private KernelPlugin BuildPlugin()
    {
        var functions = _tools.All.Select(tool =>
        {
            return KernelFunctionFactory.CreateFromMethod(
                async (KernelArguments args, CancellationToken ct) =>
                {
                    var dict = args.ToDictionary(kv => kv.Key, kv => kv.Value);
                    var json = JsonSerializer.SerializeToElement(dict);
                    return await tool.InvokeAsync(json, ct);
                },
                functionName: tool.Name,
                description: tool.Description);
        }).ToList();

        return KernelPluginFactory.CreateFromFunctions(
            pluginName: "_",
            description: "工具集合",
            functions: functions);
    }

    /// <summary>把参数字典转 JsonElement。</summary>
    private static JsonElement ToJsonElement(IDictionary<string, object?>? args)
    {
        if (args is null || args.Count == 0) return default;
        return JsonSerializer.SerializeToElement(args);
    }

    /// <summary>伪工具调用的数据结构。</summary>
    private sealed record PseudoCall(string Name, JsonElement Args);

    /// <summary>判断用户输入是否是"新任务"。</summary>
    private static bool IsNewTask(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;

        var newTaskSignals = new[]
        {
            "写一个", "写个", "创建一个", "创建个",
            "生成一个", "生成个", "实现一个", "实现个"
        };

        var hasNewTaskSignal = newTaskSignals.Any(k =>
            input.Contains(k, StringComparison.OrdinalIgnoreCase));

        var followUpSignals = new[]
        {
            "继续", "接着", "然后", "再改", "改成", "改为", "换成",
            "再加", "加个", "补充", "还有", "另外",
            "刚才", "上面", "那个文件", "这个文件", "这段代码",
            "优化", "修复", "改进", "重构"
        };

        var hasFollowUpSignal = followUpSignals.Any(k =>
            input.Contains(k, StringComparison.OrdinalIgnoreCase));

        if (hasNewTaskSignal && !hasFollowUpSignal)
            return true;

        return false;
    }

    /// <summary>
    /// 工具输出注入 chatHistory 前的截断（上下文治理）：
    /// 保留头部关键信息 + 尾部错误行（Traceback/退出码通常在尾部），
    /// 防大段 stdout/stderr 撑爆上下文导致 4B 模型空响应。
    /// 仅影响注入，运行输出判定（OutputVerifier）仍用完整输出。
    /// </summary>
    private static string SummarizeOutput(string output, int head = 400, int tail = 300)
    {
        if (output.Length <= head + tail) return output;
        return output[..head] + "\n...(中间省略，共 " + output.Length + " 字符)...\n" + output[^tail..];
    }

    /// <summary>空响应升级链：small → agent_pro → large；已在最高层返回 null。</summary>
    /// <summary>
    /// 循环熔断签名：工具名 + 首参 + 输出指纹（前 60 字符）。
    /// 同签名二次成功执行 = 重复无进展。
    /// </summary>
    public static string BuildCycleSignature(string toolName, JsonElement args, string output)
    {
        var firstArg = "";
        try
        {
            if (args.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in args.EnumerateObject())
                {
                    firstArg = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString() ?? ""
                        : prop.Value.ToString();
                    break;
                }
            }
        }
        catch
        {
        }
        var fp = output is null ? "" : (output.Length > 60 ? output[..60] : output);
        return $"{toolName}:{firstArg}:{fp}";
    }

    /// <summary>最终回答截断。</summary>
    private static string TruncateForAnswer(string s)
        => s is null ? "" : (s.Length > 200 ? s[..200] : s);

    /// <summary>
    /// 空响应升级（路由层修复 B）：4B（small/agent_pro）空响应高发且升级无意义——
    /// 空响应是生成能力问题，直接跳到 large；large 层空响应返回 null 走原层重试。
    /// </summary>
    public static string? PromoteService(string serviceId) =>
        serviceId switch
        {
            RoutingDecision.Small => RoutingDecision.Large,
            RoutingDecision.AgentPro => RoutingDecision.Large,
            _ => null
        };
}