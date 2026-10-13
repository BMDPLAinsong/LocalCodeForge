// ============================================================
// AgentLoop.cs
// Agent 主循环。ReAct + 三层路由 + 首轮决策缓存 + 防死循环 + 失败修复。
// ============================================================

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.IO;
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

    // 【H2】弱验证防护：任务是否写过文件 + 最近一次 run_command 是否成功。
    // 熔断收尾时若"写过文件但最近验证未通过"→ 判失败而非假成功（read_file 等只读工具不算验证证据）。
    private bool _everWroteFile;
    private bool _lastRunCommandOk;
    /// <summary>最后一次 run_command 的工具结果摘要（熔断归因用，让 write_unverified 可区分 超时/编译错/没跑）。</summary>
    private string _lastRunCommandSummary = "";

    // 【批次1·验证层总盘点】写被拒记录 + 显式写目标（完成门 B/C 判定输入）。
    // _writeTarget：CLI 已授权或输入解析出的用户显式写目标（"X盘根目录 test.txt" → X:\test.txt）。
    private bool _writeRejected;
    private string? _writeTarget;
    /// <summary>【10-13 上下文预算】X1 已全文注入的目标文件绝对路径（read_file 短路判定用；每任务重置）。</summary>
    private string? _injectedTargetFullPath;
    /// <summary>【10-13 阶段1】工具结果累计注入字节（折叠摘要触发用；每任务重置）。</summary>
    private long _ctxToolBytes;
    private bool _writeTargetIsFile;

    // 【10-08 内容符合性】本轮成功写过的文件路径列表（完成门 D 判定输入：修改内容须含需求 token）。
    private readonly List<string> _writtenFiles = new();

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
            // 【任务5】按输入特征类过滤记忆注入：任务归档记忆只注入同类任务，决策记忆降权保留
            var feature = Agent.Infrastructure.Routing.TaskFeatureClassifier
                .Classify(input).ToString();
            var hits = _memoryStore.Search(input, limit: 3, feature: feature);
            if (hits.Count == 0)
                return null;

            _logger.LogInformation("[记忆] 历史记忆命中 {Count} 条（特征类 {Feature}），已注入上下文。", hits.Count, feature);
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
        _everWroteFile = false;
        _lastRunCommandOk = false;
        _lastRunCommandSummary = "";
        _writeRejected = false;

        // 【批次1·A】显式写目标提取（完成门 B 判定输入；CLI 已授权路径经 AgentRequest 传入）
        _writeTarget = request.ExplicitWriteTarget;
        _writeTargetIsFile = request.ExplicitWriteTargetIsFile;
        if (_writeTarget is null)
        {
            _writeTarget = Project.ProjectPathDetector.TryGetWriteTarget(request.Input, out var t, out var f)
                ? t
                : null;
            _writeTargetIsFile = _writeTarget is not null && f;
        }

        // 【记忆治理收尾】任务启动时自动清理过期记忆（30 天未用且 0 命中自动删除）
        try
        {
            var pruned = _memoryStore.PruneExpired(_options.MemoryPruneDays, _options.MemoryPruneMinHits);
            if (pruned > 0)
                _logger.LogInformation("[记忆治理] 过期清理 {Count} 条（{Days} 天未用且命中 < {MinHits}）。",
                    pruned, _options.MemoryPruneDays, _options.MemoryPruneMinHits);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("记忆治理失败（容忍）: {Msg}", ex.Message);
        }

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

        // 【批次6 ⑧ 能力分层】预路由：循环前先定目标服务——
        // 工具集按服务注入、工程引导按服务裁剪都依赖它（原循环内 step0 才路由，插件注册被挤到路由前）。
        // 与循环内 step0 路由同输入同调用，总调用次数不变（循环内 firstDecision 复用，不重复路由）。
        RoutingDecision? firstDecision;
        if (_options.EnableRouting)
        {
            var rc0 = new RoutingContext(
                UserInput: request.Input,
                Step: 0,
                LastToolName: null,
                LastStepFailed: false,
                HistoryTurns: chatHistory.Count);
            firstDecision = await _router.RouteAsync(rc0, ct);
        }
        else
        {
            firstDecision = new RoutingDecision(RoutingDecision.Large, RoutingTier.LargeModel, "路由未启用");
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
        // 【批次6 ⑧ 能力分层】仅当目标服务可见 analyze_code（analysis 层）时注入——
        // small 分层后无此工具，注入引导会让模型调用不存在的工具。
        if (projectBlock is not null
            && Agent.Infrastructure.Routing.IntentOverride.IsReadTask(request.Input)
            && Agent.Infrastructure.Routing.ToolLayering.ServiceHasTier(
                firstDecision!.ServiceId, Agent.Infrastructure.Routing.ToolLayering.Analysis, _options.EnableToolLayering))
            userMsg += "\n\n（工程提示：理解代码结构请优先调用 analyze_code 获取类/方法/行号索引，再按需 read_file 读具体行段——符号地图比逐行探索高效。）";

        // 【10-09 方案2】分析/总结类任务注入核心文件清单（数据驱动，非提示词）：
        // 命中 ReadQuery 特征类（确定性词表，含"分析/解释/总结"前缀）时，注入入口/依赖/大源码文件
        // 的真实相对路径——模型 5-8 步内读完全部核心文件即可总结，不再逐文件发散探索
        // （2026-10-09 实测：20 步读 16 个文件未收敛、3 次猜错路径）。
        if (projectBlock is not null
            && Agent.Infrastructure.Routing.TaskFeatureClassifier.Classify(request.Input)
                == Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.ReadQuery)
        {
            var coreLines = Agent.Infrastructure.Project.ProjectScanner.CoreFileLines(_options.ProjectRoot!);
            if (!string.IsNullOrWhiteSpace(coreLines))
                userMsg += "\n\n" + coreLines;
        }

        // 【2026-10-12 方案B】存在性命中 → 升级为【内容注入】：
        //   位置指引（"目标文件：index.html"）在大工程 CompactBlock 截断时无效——X:\ 根
        //   index.html 未入上下文 → 模型不知道文件存在 → 自创无关任务（实测跑偏）。
        //   内容注入（先例：B 符号定义注入"内容优于位置指引"）让模型拿到真实内容再修复。
        //   文件不存在（新建任务）→ 保持原位置提示；超大/敏感区 → 位置提示（防上下文爆炸/源头拒）。
        //   【10-12 修正】以 FindExistingProjectFileRef（更宽正则 + 存在性校验）为权威驱动，
        //   不依赖 ExtractTargetFile 先命中——TargetFileRe 曾缺 html 扩展名导致注入整体跳过
        //   （双正则漂移实证：模型大海捞针翻无关目录）。
        //   【10-12 X1】输入已含盘符绝对路径（"读取x:\盘下index.html文件…"）→ 绝对路径优先：
        //   提取+清洗+File.Exists 命中 → 注入绝对路径全文（模型直接锚定，不再 list_dir 盘根摸底）。
        //   【10-12 B2+】X1 内容注入【不依赖 projectBlock】——B2 轻量通道（文件目标不挂工程，
        //   projectBlock=null）时若被守护跳过 → 模型失去"目标文件已定位+内容"引导 → 行为随机
        //   （实测第一次碰巧 read_file 命中、第二次 list_dir 探路→自创→完成门拦截）。绝对路径
        //   存在性命中即注入，工程内文件检索/位置提示分支仍限工程模式。
        //   【10-13 上下文预算】注入阈值化：≤ ContextTargetInjectBytes 全文注入 + 记录注入路径
        //   （read_file 短路用，消双份）；> 阈值 骨架注入（路径+行数+前 50 行），不短路，分页定向。
        _injectedTargetFullPath = null;
        _ctxToolBytes = 0;
        if (IntentOverride.IsWriteTask(request.Input))
        {
            if (ProjectPathDetector.TryResolveAbsolutePath(request.Input, out var absTarget)
                && TryLoadFileContentByPath(absTarget, out var absContent))
            {
                if (absContent.Length <= _options.ContextTargetInjectBytes)
                {
                    userMsg += $"\n\n（任务引用文件 {absTarget} 已存在，当前内容如下——该文件即任务目标，请基于真实内容读取/修复，无需探索定位：\n```\n{absContent}\n```）";
                    _injectedTargetFullPath = absTarget;
                    _logger.LogWarning("[上下文预算] 目标文件全文注入（{Path}，{N} 字符）。", absTarget, absContent.Length);
                }
                else
                {
                    var lines = absContent.Split('\n');
                    var head = string.Join("\n", lines.Take(50));
                    userMsg += $"\n\n（任务目标文件 {absTarget}，共 {lines.Length} 行 / {absContent.Length} 字符。文件较大已注入前 50 行供定位：\n```\n{head}\n```\n后续内容请用 read_file 分页读取，不要一次性读全文。）";
                    _logger.LogWarning("[上下文预算] 目标文件骨架注入（{Path}，{N} 字符 > 阈值 {T}）。", absTarget, absContent.Length, _options.ContextTargetInjectBytes);
                }
            }
            else if (projectBlock is not null)
            {
                var existingRef = ReadOnlyGuard.FindExistingProjectFileRef(request.Input, _options.ProjectRoot!);
                if (existingRef is not null
                    && TryLoadFileContent(_options.ProjectRoot!, existingRef, out var fileContent))
                {
                    userMsg += $"\n\n（任务引用文件 {existingRef} 已存在于工程根，当前内容如下——请基于真实内容读取/修复，无需再探索定位：\n```\n{fileContent}\n```）";
                }
                else
                {
                    var target = ExtractTargetFile(request.Input);
                    if (target is not null)
                        userMsg += $"\n\n（任务目标文件：{target}——本次任务只需读取/修改此文件，其余文件与本任务无关，请勿阅读。）";
                }
            }
        }
        // 【10-08 会话写目标延续】CLI 已授权的显式写目标（含"这个文件"指代继承的上轮目标）注入：
        // 模型拿到真实路径事实，知道"这个文件"指什么、往哪写——达成门同一 _writeTarget 做落地校验。
        if (request.ExplicitWriteTarget is not null)
            userMsg += $"\n\n（本次任务显式写目标：{request.ExplicitWriteTarget}" +
                $"{(request.ExplicitWriteTargetIsFile ? "（文件）" : "（目录）")}——请直接对该路径执行写操作。）";
        // 【10-12 B】沙箱读例外：输入显式指向的绝对路径文件 → 注入 ReadFileTool（任务级，串行安全）。
        // 用户主动在输入里写出路径（"读取x:\index.html…"）= 明确授权读该文件；黑名单仍源头拒。
        if (ProjectPathDetector.TryResolveAbsolutePath(request.Input, out var explicitReadTarget)
            && _tools.TryGet("read_file", out var readTool)
            && readTool is Agent.Infrastructure.Tools.ReadFileTool readFileTool)
            readFileTool.SetExplicitReadTarget(explicitReadTarget);

        // 【批次6 ⑧ 能力分层】按目标服务过滤工具集 + 注入可用工具清单：
        // 过滤后若有缩减（small 隐藏分析/记忆工具）→ 注入"可用工具"行，让模型知道边界，
        // 不会尝试调用被隐藏的工具；agent_pro/large 全量 → 不注入（无冗余）。
        IReadOnlyCollection<Agent.Core.Abstractions.IAgentTool> toolSet;
        if (_options.EnableToolLayering)
        {
            toolSet = Agent.Infrastructure.Routing.ToolLayering.Filter(
                _tools.All, firstDecision!.ServiceId, enabled: true);
            if (toolSet.Count < _tools.All.Count)
                userMsg += $"\n\n（本次任务可用工具：{string.Join(", ", toolSet.Select(t => t.Name))}。其余工具本次任务不可用。）";
        }
        else
        {
            toolSet = _tools.All.ToList();
        }

        chatHistory.AddUserMessage(userMsg);
        await _store.AppendTurnAsync(request.SessionId, new ChatTurn("user", request.Input), ct);
        // 【10-13 上下文预算】首轮注入量日志（可观测：X1/记忆/工程块合计，调参有数据）
        _logger.LogInformation("[上下文预算] 任务注入 {Bytes} 字符（≈{KB} KB）。",
            userMsg.Length, userMsg.Length / 1024);

        // 注册工具 Plugin（【批次6 ⑧ 能力分层】按目标服务过滤后的工具集；kernel 跨任务复用，
        // 先移除旧插件再添加，防上个任务的工具集残留到本任务）
        var plugin = BuildPlugin(toolSet);
        var existingPlugin = _kernel.Plugins.FirstOrDefault(p => p.Name == plugin.Name);
        if (existingPlugin is not null)
            _kernel.Plugins.Remove(existingPlugin);
        _kernel.Plugins.Add(plugin);

        RoutingDecision? lastYieldedDecision = null;
        int forceContinueCount = 0;
        int emptyAnswerCount = 0;
        bool autoTestRun = false; // 工程完成门：每任务最多自动跑一次测试
        string? _lastFailureSummary = null; // 上下文治理：最近一次失败的工具输出摘要（注入用截断版）

        var historyStartIndex = chatHistory.Count;

        // ═══════════ ReAct 主循环 ═══════════
        // 【10-09 方案3】读任务步数分级：ReadQuery 特征类（读/分析）需"探索+总结"更多步，
        // 写任务保持 MaxSteps 防空转。确定性特征类判定，非提示词。
        var isReadQuery = Agent.Infrastructure.Routing.TaskFeatureClassifier.Classify(request.Input)
            == Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.ReadQuery;
        var effectiveMaxSteps = _options.MaxSteps;
        if (isReadQuery)
            effectiveMaxSteps = Math.Max(effectiveMaxSteps, _options.ReadQueryMaxSteps);
        // 【10-09 方案4】读任务收敛判定：记录最后一步模型输出与"是否调用了工具"——
        // 步数耗尽时若最后一步是纯文本总结（无工具调用），判成功而非失败。
        string? lastModelText = null;
        var lastStepHadToolCall = false;
        for (var step = 0; step < effectiveMaxSteps; step++)
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

                if (firstDecision is null)
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
            // 【单模型三角色】温度按服务角色运行时注入（端点 Temperature 优先，回退全局）
            var settings = new OpenAIPromptExecutionSettings
            {
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(autoInvoke: false),
                Temperature = _options.GetEffectiveTemperature(serviceId),
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
                // 【10-08 方案 B】LLM 调用异常升级兜底：空响应升级链只覆盖"空内容"，
                // 不覆盖调用异常（TaskCanceledException 偶发中断/HttpClient 超时/连接失败）——
                // 此前异常直接判失败（68s canceled 与 890s 600s 超时实证）。
                // 与空响应链同构：异常时按 PromoteService 升级一次（small/agent_pro → large，
                // large 已在最高层 → 判失败；每任务天然至多升级一次）。
                var promoted = PromoteService(decision.ServiceId);
                if (promoted is not null)
                {
                    _logger.LogWarning(
                        "LLM 调用失败（{Msg}），升级服务 {From} → {To} 重试。",
                        errorMessage, decision.ServiceId, promoted);
                    decision = new RoutingDecision(
                        promoted,
                        RoutingTier.LargeModel,
                        $"LLM 异常升级（{decision.ServiceId} → {promoted}）");
                    firstDecision = decision; // 下一轮循环复用升级后的决策
                    // 与空响应升级同：主动决策，重置 _lastStepFailed，
                    // 避免下一轮被 recovery 规则（只该响应工具失败）重路由回原层导致白升。
                    _lastStepFailed = false;
                    chatHistory.AddUserMessage(
                        $"上一步模型调用失败（{errorMessage}）。请重新开始：要么调用工具完成用户需求，要么给出明确的最终回答。");
                    continue;
                }

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
                lastStepHadToolCall = true; // 【方案4】最后一步是工具调用 → 不算收敛总结
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

                    // 【H2】写/改文件成功 → 记录；run_command 结果 → 记录最近验证状态
                    if (ok && (cleanName is "write_file" or "edit_file" or "append_file"))
                    {
                        _everWroteFile = true;
                        // 【10-08 内容符合性】记录写过的文件路径（完成门 D 检查对象）。
                        // 归一化为绝对路径（与 WriteFileTool baseRoot 逻辑对称：工程可写以工程根
                        // 为基准，否则任务目录）——完成门 B 按"本轮写入目标"判定需要绝对路径。
                        if (argsJson.TryGetProperty("path", out var p) &&
                            p.ValueKind == System.Text.Json.JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(p.GetString()))
                        {
                            try
                            {
                                var baseRoot = _options.ProjectWriteEnabled && _options.ProjectRoot is not null
                                    ? _options.ProjectRoot
                                    : request.WorkspaceRoot;
                                _writtenFiles.Add(Path.GetFullPath(Path.Combine(baseRoot, p.GetString()!)));
                            }
                            catch (Exception)
                            {
                                _writtenFiles.Add(p.GetString()!);
                            }
                        }
                    }
                    // 【批次1·C】写工具被拒 → 记录（完成门判"被拒未重试 → 失败"）
                    if (!ok && (cleanName is "write_file" or "edit_file" or "append_file" or "create_dir"))
                        _writeRejected = true;
                    if (cleanName == "run_command")
                    {
                        _lastRunCommandOk = ok;
                        _lastRunCommandSummary = Agent.Infrastructure.Context.ToolResultNormalizer.Truncate(output, head: 160, tail: 80);
                    }

                    // 【10-13 上下文预算】工具结果注入压缩（数据结构性）：
                    //   · run_command：截断（既有）
                    //   · read_file：① 短路——读取目标 == X1 已全文注入的文件 → 短提示（消双份冗余：
                    //     X1 注入 40KB + read_file 40KB = 90KB，CPU 30B prefill >20min 超时实证）；
                    //     ② 超阈值截断——> ContextToolResultBytes 头尾截断 + 行数提示（完整靠 offset 分页）。
                    //   · edit_file 精度：短路仅发生在"注入=File.ReadAllText(同文件)"时，内容同一来源，
                    //     old_string 匹配不受影响；超阈值截断文件无短路注入，模型拿到头尾+分页提示。
                    //   【10-13 B】压缩逻辑抽公共方法 CondenseToolResult：情况 A（结构化调用）与
                    //   情况 B（文本兜底 TryParseToolCallsFromText）共用——30B 走文本路径，此前该路径
                    //   read_file 无短路/截断/折叠，39KB 结果原样进上下文（38KB 注入 + 39KB 结果 = 77KB，
                    //   CPU prefill >20min 实证），消双路径漂移。
                    var condensed = CondenseToolResult(cleanName, argsJson, output, request);
                    _lastFailureSummary = condensed;

                    // ── 方案 2：循环熔断（工具序列重复且无新进展 → 以工具验证收尾）──
                    // 写任务 large 常"写了又写/验了又验"不交付最终回答（达最大步数失败）。
                    // 同一工具+首参+输出指纹第二次成功执行 = 重复无进展 → 强制收尾。
                    if (ok)
                    {
                        var sig = BuildCycleSignature(cleanName, argsJson, condensed);
                        if (!_toolSignatures.Add(sig))
                        {
                            // 【批次1·B】完成门优先（H2/H4 之前）：
                            // 显式目标已落地 → 直接放行收尾（验证命令失败不误伤——t2 实证：
                            // write_file X:/notes.txt 成功但 type X:/notes.txt cmd 正斜杠失败，
                            // 目标已达成，H2 却判失败）；
                            // 目标缺失 / 写被拒未重试 → 判失败（诚实失败）。
                            var fuseGate = Project.CompletionGate.Evaluate(
                                isWriteTask: IntentOverride.IsWriteTask(request.Input),
                                wroteAny: _everWroteFile,
                                writeRejected: _writeRejected,
                                target: _writeTarget,
                                targetIsFile: _writeTargetIsFile,
                                requirementTokens: (_writeTarget is not null
                    ? Project.ContentConformance.ExtractRequirementTokensExtended(request.Input)
                    : Project.ContentConformance.ExtractRequirementTokens(request.Input)),
                                writtenFiles: _writtenFiles);
                            if (fuseGate is not null)
                            {
                                _logger.LogWarning("[完成门·熔断] {Message}", fuseGate.message);
                                yield return new AgentEvent.Error(fuseGate.message);
                                yield break;
                            }
                            if (_writeTarget is not null)
                            {
                                _logger.LogWarning("[熔断] 显式写目标已落地（{Target}），以工具验证结果收尾。", _writeTarget);
                                var targetText = $"任务完成：显式写目标 {_writeTarget} 已落地。最后验证：{TruncateForAnswer(condensed)}";
                                await _store.AppendTurnAsync(request.SessionId, new ChatTurn("assistant", targetText), ct);
                                yield return new AgentEvent.FinalAnswer(targetText);
                                yield break;
                            }

                            // 【H2】熔断收尾的证据门槛：写过文件但最近一次 run_command 未通过
                            // （如 read_file 重复导致的熔断）→ 验证不足，判失败而非假成功。
                            if (_everWroteFile && !_lastRunCommandOk)
                            {
                                _logger.LogWarning("[熔断] 已写文件但验证未通过（最近 run_command 失败/缺失），判失败防假成功。");
                                yield return new AgentEvent.Error(BuildWriteUnverifiedReason(_lastRunCommandSummary));
                                yield break;
                            }

                            // 【H4】熔断收尾的完成门槛（要求写却没写 = 未完成任务）：
                            // 任务输入含写动词（IsWriteTask）但本轮从未写过文件（只读/循环 read 卡死）
                            // → read_file 等只读工具证明不了改动，判失败而非"工具已验证"假完成。
                            if (!_everWroteFile && IntentOverride.IsWriteTask(request.Input))
                            {
                                _logger.LogWarning("[熔断] 写任务但未产生任何写入（只读循环），判失败防假完成。");
                                yield return new AgentEvent.Error("任务要求修改文件但未产生任何写入（仅循环读取），熔断终止。可重试或改用 find_symbol/analyze_code 定位后编辑。");
                                yield break;
                            }

                            _logger.LogWarning("[熔断] 工具序列重复无进展（{Sig}）。", sig);
                            // 【10-10 问题1】最近工具输出以"错误："开头（业务级失败，如大文件 read 被拒）
                            // → 不得包装"工具已验证"完成（完成文本即错误原文 = 可疑成功）。
                            // 读任务已收敛（最后一步纯文本总结）→ 放行总结；否则判失败（诚实失败）。
                            if (IsToolSemanticFailure(condensed))
                            {
                                if (isReadQuery && IsReadQueryConverged(lastStepHadToolCall, lastModelText))
                                {
                                    await _store.AppendTurnAsync(request.SessionId, new ChatTurn("assistant", lastModelText!), ct);
                                    _logger.LogWarning("[熔断·收敛] 读任务已产出总结，放行收尾。");
                                    yield return new AgentEvent.FinalAnswer(lastModelText);
                                    yield break;
                                }
                                yield return new AgentEvent.Error($"工具序列重复无进展且最近验证失败（{TruncateForAnswer(condensed)}），任务终止。");
                                yield break;
                            }
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
            lastModelText = text; // 【方案4】记录最后一步模型文本（收敛判定用）
            lastStepHadToolCall = false;
            var pseudoCalls = TryParseToolCallsFromText(text);

            if (pseudoCalls.Count > 0)
            {
                lastStepHadToolCall = true; // 【方案4】伪调用也是工具调用 → 不算收敛总结
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
                    // 【10-13 B】与情况 A 共用同一压缩逻辑（短路/截断/折叠 + _ctxToolBytes 累计）
                    var condensed = CondenseToolResult(pc.Name, pc.Args, output, request);
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
                IntentOverride.IsWriteTask(request.Input) ||
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
                request.Input.Contains("换成") ||
                request.Input.Contains("加"); // 与 IntentOverride.OperationVerbs 对齐（防双路径漂移：路由词表已含"加"）

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

                    if (looksLikeTemplateOutput && step < effectiveMaxSteps - 1)
                    {
                        forceContinueCount++;
                        if (forceContinueCount >= 3)
                        {
                            await _store.AppendTurnAsync(request.SessionId, new ChatTurn("assistant", text), ct);
                            // 【批次1·D】消息含"未真正实现"→ AgentLoopMetricsDecorator 标 is_suspicious=1
                            yield return new AgentEvent.Error(
                                "任务未真正实现：输出只是 Hello World 模板，未实现用户需求。");
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
            if (looksLikeCodeRequest && !hasWrittenFile && step < effectiveMaxSteps - 1)
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

            if (mustRun && onlyBuiltNotRun && step < effectiveMaxSteps - 1)
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
            if (looksLikeCodeRequest && hasWrittenFile && !hasRunCommand && step < effectiveMaxSteps - 1)
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
            if (looksLikeCodeRequest && lastRunFailed && step < effectiveMaxSteps - 1)
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

            // ═══════════ 工程完成门：改了代码 → 自动运行测试并回填 ═══════════
            // 工程模式 + 本轮写过文件 + 工程有测试入口 + 尚未自动跑过 → TestRunnerService 确定性执行，
            // 结果以 Tool 消息注入，模型基于真实结果收尾/修复（3 轮防死循环、每任务最多自动跑一次）。
            if (_options.ProjectRoot is not null
                && hasWrittenFile
                && !autoTestRun
                && step < effectiveMaxSteps - 1)
            {
                var testCmd = Agent.Infrastructure.Project.TestRunnerService.DetectTest(_options.ProjectRoot);
                if (testCmd is not null)
                {
                    autoTestRun = true;
                    _logger.LogWarning("工程完成门：已修改代码，自动运行测试（{Cmd}）。", testCmd.Value.Display);

                    var testResult = await Agent.Infrastructure.Project.TestRunnerService.RunAsync(
                        _options.ProjectRoot!, timeoutMs: 150_000, ct);

                    chatHistory.Add(new ChatMessageContent(
                        AuthorRole.Tool,
                        new ChatMessageContentItemCollection
                        {
                            new FunctionResultContent(
                                new FunctionCallContent("run_test", "{}"),
                                testResult.Summary + (testResult.TimedOut ? " [超时]" : ""))
                        }));

                    chatHistory.Add(new ChatMessageContent(AuthorRole.Assistant, text));
                    chatHistory.AddUserMessage(
                        "检测到本次任务修改了工程代码，已完成测试自动运行，结果：" + testResult.Summary + "。\n" +
                        (testResult.ExitCode == 0
                            ? "测试通过，请直接给出最终总结收尾。"
                            : "测试未通过，请根据结果修复代码后重新验证；若确实无法通过请在最终回答中说明。"));
                    continue;
                }
            }

            // 【批次1·验证层总盘点】完成门：写任务收尾前查"写被拒未重试"+"目标达成"。
            // 让"写了文件"升级为"写到了用户要求的目标"（X盘根目录 test.txt 实证：写了别处 ≠ 达成）。
            var gateVerdict = Project.CompletionGate.Evaluate(
                isWriteTask: IntentOverride.IsWriteTask(request.Input),
                wroteAny: _everWroteFile,
                writeRejected: _writeRejected,
                target: _writeTarget,
                targetIsFile: _writeTargetIsFile,
                requirementTokens: (_writeTarget is not null
                    ? Project.ContentConformance.ExtractRequirementTokensExtended(request.Input)
                    : Project.ContentConformance.ExtractRequirementTokens(request.Input)),
                writtenFiles: _writtenFiles);
            if (gateVerdict is not null)
            {
                await _store.AppendTurnAsync(request.SessionId, new ChatTurn("assistant", text), ct);
                _logger.LogWarning("[完成门] {Message}", gateVerdict.message);
                yield return new AgentEvent.Error(gateVerdict.message);
                yield break;
            }

            // 真正交付
            await _store.AppendTurnAsync(request.SessionId, new ChatTurn("assistant", text), ct);
            yield return new AgentEvent.FinalAnswer(text);
            yield break;
        }

        // 【批次1·验证层总盘点】最大步数收尾也过完成门：
        // 显式写目标已落地 → 放行收尾（模型反复验证命令失败循环达上限，但目标已达成——t2 复测实证）；
        // 目标缺失 / 写被拒未重试 → 判失败（诚实失败，与收尾/熔断三处一致）。
        var maxGate = Project.CompletionGate.Evaluate(
            isWriteTask: IntentOverride.IsWriteTask(request.Input),
            wroteAny: _everWroteFile,
            writeRejected: _writeRejected,
            target: _writeTarget,
            targetIsFile: _writeTargetIsFile,
            requirementTokens: (_writeTarget is not null
                    ? Project.ContentConformance.ExtractRequirementTokensExtended(request.Input)
                    : Project.ContentConformance.ExtractRequirementTokens(request.Input)),
            writtenFiles: _writtenFiles);
        if (maxGate is not null)
        {
            _logger.LogWarning("[完成门·最大步数] {Message}", maxGate.message);
            yield return new AgentEvent.Error(maxGate.message);
            yield break;
        }
        if (_writeTarget is not null)
        {
            var landed = $"任务完成：显式写目标 {_writeTarget} 已落地（达到最大步数 {effectiveMaxSteps}，但目标达成）。";
            await _store.AppendTurnAsync(request.SessionId, new ChatTurn("assistant", landed), ct);
            _logger.LogWarning("[完成门·最大步数] 目标已落地，放行收尾。");
            yield return new AgentEvent.FinalAnswer(landed);
            yield break;
        }

        // 【10-09 方案4】读任务收敛判定：ReadQuery 达到步数上限，但最后一步是纯文本回答
        // （无工具调用）且已产出实质总结 → 判成功而非失败。读任务的交付物即总结文本本身；
        // 三件套后 read 任务不再发散，耗尽场景多为"探索充分、最后一步恰好是总结但未走交付分支"。
        if (isReadQuery && IsReadQueryConverged(lastStepHadToolCall, lastModelText))
        {
            await _store.AppendTurnAsync(request.SessionId, new ChatTurn("assistant", lastModelText!), ct);
            _logger.LogWarning("[收敛·最大步数] 读任务已产出总结（{Len} 字符），放行收尾。", lastModelText!.Length);
            yield return new AgentEvent.FinalAnswer(lastModelText);
            yield break;
        }

        yield return new AgentEvent.Error($"达到最大步数 {effectiveMaxSteps}，任务终止。");
    }

    /// <summary>
    /// 【10-10 问题1】工具业务级失败判定（确定性纯函数，可测）：工具输出以"错误："统一前缀
    /// 开头即为语义失败（如大文件 read 拒绝、路径越界）——熔断收尾时不得把失败输出包装成
    /// "工具已验证"完成（那会产生"完成文本即错误原文"的可疑成功）。
    /// </summary>
    public static bool IsToolSemanticFailure(string output)
        => output.StartsWith("错误：", StringComparison.Ordinal);

    /// <summary>
    /// 【10-09 方案4】读任务收敛判定（确定性纯函数，可测）：
    /// 最后一步为纯文本回答（无工具调用）且已产出实质总结（非空白、长度 ≥ 30 字符）
    /// → 视为收敛，步数耗尽时放行收尾（读任务的交付物即总结文本本身）。
    /// </summary>
    public static bool IsReadQueryConverged(bool lastStepHadToolCall, string? lastModelText, int minLen = 30)
        => !lastStepHadToolCall
           && !string.IsNullOrWhiteSpace(lastModelText)
           && lastModelText.Length >= minLen;

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

    /// <summary>目标文件正则：匹配输入中的文件名（含常见代码/文本扩展名）。数据驱动，供目标文件注入。</summary>
    private static readonly System.Text.RegularExpressions.Regex TargetFileRe = new(
        @"\b([\w.-]+\.(?:cs|py|js|ts|json|csproj|sln|slnx|md|txt|xml|yaml|yml|java|go|html|htm))\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>从任务输入提取目标文件名（首个命中；排除工作区路径前缀干扰）。未命中返回 null。</summary>
    public static string? ExtractTargetFile(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var m = TargetFileRe.Match(input);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>内容注入上限（防上下文爆炸；超大文件降级为位置指引）。</summary>
    private const long TargetContentMaxBytes = 200 * 1024;

    /// <summary>
    /// 加载工程内目标文件内容用于注入（2026-10-12 方案B）。
    /// 命中工程内已存在文件 → 读全文；文件不存在 / 超 200KB / 敏感区 → 返回 false（位置提示兜底）。
    /// 纯确定性文件操作，无模型调用；异常一律返回 false（不阻断任务，降级为位置指引）。
    /// </summary>
    public static bool TryLoadFileContent(string projectRoot, string fileName, out string content)
    {
        content = "";
        try
        {
            var full = Directory.EnumerateFiles(projectRoot, fileName, SearchOption.AllDirectories).FirstOrDefault();
            if (full is null) return false;
            return TryLoadFileContentByPath(full, out content);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>【10-12 X1】按已解析的绝对路径加载文件内容（注入用）。校验同 TryLoadFileContent。</summary>
    public static bool TryLoadFileContentByPath(string fullPath, out string content)
    {
        content = "";
        try
        {
            var fi = new FileInfo(fullPath);
            if (!fi.Exists) return false;
            if (fi.Length > TargetContentMaxBytes) return false;              // 超大 → 位置指引
            if (SensitivePathGuard.IsDenied(fullPath)) return false;          // 敏感区源头拒
            content = File.ReadAllText(fullPath);
            return true;
        }
        catch
        {
            return false;
        }
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

            // 【10-13 批次4】工具契约结构化：InvokeAsync 返回 ToolResult（Success/Code/Message）。
            // 双轨消费：模型/渲染仍吃 Message 文本；机器判定读 Success 字段（不再匹配"错误："前缀）。
            var toolResult = await tool.InvokeAsync(args, ct);
            var output = toolResult.Message;

            // ─────────── 特殊处理 run_command ───────────
            if (name == "run_command")
            {
                // 【修复】工具层拒绝（超时/白名单/路径边界）直接判失败——读结构化 Success 字段
                if (!toolResult.Success)
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
                    // 通用失败词（失败/failed/error/JSON 字段）+ 成功证据判定；
                    // 公共判定由 OutputVerifier.HasGenericFailure 提供（双路径统一，单一来源）。
                    // 【10-08 A 证据链先行】4 参版本：hasEvidence=true 表示"命中软信号但存在
                    // 成功证据"（边缘成功）——不判失败，打 warning 可观测（任务级可疑落库在 B 阶段）。
                    if (OutputVerifier.HasGenericFailure(stdout, null, out var genericFailureReason, out var hadEvidence))
                    {
                        return (output + "\n[检测] 程序输出了失败结果（" + genericFailureReason + "），但没有成功输出。请检查逻辑并修复。", false);
                    }
                    if (hadEvidence)
                    {
                        // 【B 阶段】边缘成功：命中软信号但存在成功证据——按成功处理，
                        // 但注入 [边缘成功] 标记（与 [检测] 同构），metrics 装饰器据此标 is_suspicious=1
                        // （数据回流：可疑成功从日志升级为可统计、可审计的数据）。
                        return (output + "\n[边缘成功] 输出含失败信号（" + genericFailureReason +
                                "）但存在成功证据（演示分支/成功报告），按成功处理。", true);
                    }
                }
            }

            return (output, toolResult.Success);
            }

            return (output, toolResult.Success);
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

    /// <summary>把 IAgentTool 集合包装成 SK 的 KernelPlugin（【批次6 ⑧】接收按服务过滤后的子集）。</summary>
    private KernelPlugin BuildPlugin(IReadOnlyCollection<Agent.Core.Abstractions.IAgentTool> tools)
    {
        var functions = tools.Select(tool =>
        {
            return KernelFunctionFactory.CreateFromMethod(
                async (KernelArguments args, CancellationToken ct) =>
                {
                    var dict = args.ToDictionary(kv => kv.Key, kv => kv.Value);
                    var json = JsonSerializer.SerializeToElement(dict);
                    // 【10-13 批次4】SK 函数结果保持字符串契约（ToolResult 双轨：字段仅供机器判定）
                    var r = await tool.InvokeAsync(json, ct);
                    return r.Message;
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
    /// <summary>
    /// 【10-11 write_unverified 归因】熔断失败消息带上 run_command 真实失败原因，
    /// 让 metrics 的 error_summary 可区分"超时/编译错误/退出码/未跑"，不再吞掉细节。
    /// 纯函数：summary 为空（未记录到 run_command）→ 兼容旧消息。
    /// </summary>
    public static string BuildWriteUnverifiedReason(string lastRunSummary)
    {
        const string baseMsg = "任务写入文件但验证未通过（run_command 未成功），熔断终止，防假成功。";
        if (string.IsNullOrWhiteSpace(lastRunSummary)) return baseMsg;
        return baseMsg + " 最近 run_command：" + lastRunSummary;
    }

    /// <summary>【10-13 上下文预算】从 read_file 参数提取 path 字符串。非对象/无 path/空 → false。</summary>
    /// <summary>
    /// 【10-13 上下文预算】工具结果注入压缩（情况 A 结构化调用 / 情况 B 文本兜底共用，消双路径漂移）：
    /// run_command 截断；read_file ① 短路（读取目标 == 已全文注入文件 → 短提示，消双份冗余）
    /// ② 超阈值截断 ③ 累计超预算折叠。同时累计 _ctxToolBytes。纯确定性逻辑。
    /// </summary>
    private string CondenseToolResult(string cleanName, System.Text.Json.JsonElement args, string output, Agent.Core.Models.AgentRequest request)
    {
        var condensed = cleanName == "run_command"
            ? Agent.Infrastructure.Context.ToolResultNormalizer.Truncate(output)
            : output;
        if (cleanName == "read_file")
        {
            if (_injectedTargetFullPath is not null
                && TryGetReadPath(args, out var readPath)
                && ReadPathMatches(readPath, request, _injectedTargetFullPath))
            {
                condensed = "（文件全文已在上文任务目标文件注入块中，内容一致，无需重复读取——直接基于注入内容修复。）";
                _logger.LogWarning("[上下文预算] read_file 短路命中（{Path} 已全文注入，消双份冗余）。", readPath);
            }
            else if (output.Length > _options.ContextToolResultBytes)
            {
                condensed = Agent.Infrastructure.Context.ToolResultNormalizer.Truncate(output, head: 6000, tail: 2000);
                _logger.LogWarning("[上下文预算] read_file 结果超 {Limit} 截断（头 6000 + 尾 2000），完整内容可 offset 分页续读。", _options.ContextToolResultBytes);
            }
            else if (_ctxToolBytes + output.Length > _options.ContextFoldThreshold)
            {
                TryGetReadPath(args, out var foldPath);
                condensed = Agent.Infrastructure.Context.ContextFolder.FoldToolResult(
                    output, string.IsNullOrEmpty(foldPath) ? null : Path.GetFileName(foldPath));
                _logger.LogWarning("[上下文预算] 累计注入超 {Limit}，read_file 结果折叠摘要（{Path}）。", _options.ContextFoldThreshold, foldPath);
            }
        }
        _ctxToolBytes += condensed.Length;
        return condensed;
    }

    public static bool TryGetReadPath(System.Text.Json.JsonElement args, out string path)
    {
        path = "";
        if (args.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
        if (!args.TryGetProperty("path", out var p) || p.ValueKind != System.Text.Json.JsonValueKind.String) return false;
        path = p.GetString() ?? "";
        return path.Length > 0;
    }

    /// <summary>
    /// 【10-13 上下文预算】read_file path 是否指向"X1 已全文注入的目标文件"（短路判定）。
    /// 归一规则：含盘符 → GetFullPath；相对 → 以工具基准（工程根可写时取工程根，否则工作区根）
    /// Combine 后 GetFullPath——与写工具/完成门的基准一致，杜绝双基准漂移。
    /// 纯函数（静态），可单测。
    /// </summary>
    public static bool ReadPathMatches(string readPath, Agent.Core.Models.AgentRequest request, string injectedFullPath)
    {
        try
        {
            var full = Path.IsPathRooted(readPath)
                ? Path.GetFullPath(readPath)
                : Path.GetFullPath(Path.Combine(request.WorkspaceRoot, readPath));
            return full.Equals(injectedFullPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
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