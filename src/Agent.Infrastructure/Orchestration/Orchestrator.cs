// ============================================================
// Orchestrator.cs
// 三阶段编排：Planner（规划）→ Executor（执行）→ Validator（验证修复）
// ============================================================

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agent.Core.Abstractions;
using Agent.Core.Models;
using Agent.Infrastructure.Quality;
using Agent.Infrastructure.Metrics;
using Agent.Infrastructure.Project;
using Agent.Infrastructure.Routing;
using Microsoft.Extensions.Logging;

namespace Agent.Infrastructure.Orchestration;

/// <summary>
/// 编排器实现。
/// </summary>
public sealed class Orchestrator : IOrchestrator
{
    private readonly IPlannerService _planner;
    private readonly IExecutorService _executor;
    private readonly IToolRegistry _tools;
    private readonly ILintService _lint;
    private readonly AgentOptions _options;
    private readonly IMetricsCollector _metrics;   // ← B+：子调用指标（agent_pro/small 样本自动积累）
    private readonly ILogger<Orchestrator> _logger;
    private readonly Routing.TaskMetricsAggregator? _featureAggregator; // ← D2：特征类历史表现（重试上限校准）

    public Orchestrator(
        IPlannerService planner,
        IExecutorService executor,
        IToolRegistry tools,
        ILintService lint,
        AgentOptions options,
        IMetricsCollector metrics,
        ILogger<Orchestrator> logger,
        Routing.TaskMetricsAggregator? featureAggregator = null)
    {
        _planner = planner;
        _executor = executor;
        _tools = tools;
        _lint = lint;
        _options = options;
        _metrics = metrics;
        _logger = logger;
        _featureAggregator = featureAggregator;
    }

    /// <summary>执行一次编排任务。</summary>
    public async IAsyncEnumerable<AgentEvent> RunAsync(string userInput,string workspaceRoot,[EnumeratorCancellation] CancellationToken ct)
    {
        // ═══════════ 阶段 1：规划 ═══════════
        Plan? plan = null;
        string? planError = null;
        var plannerSw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // 外部数据任务：附加环境约束后交给 Planner（模型在规划阶段即知环境事实）
            var envConstraint = ExternalTaskDetector.Detect(userInput);
            var planInput = envConstraint is null ? userInput : userInput + "\n\n" + envConstraint;

            // 【工程图谱增强】工程模式下附加图谱块：类型分布 + 提及文件的改动影响面（多文件规划）
            var graphBlock = BuildEngineeringGraphBlock(userInput);
            if (graphBlock is not null)
                planInput += "\n\n" + graphBlock;

            plan = await _planner.GeneratePlanAsync(planInput, workspaceRoot, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Planner 调用异常。");
            planError = ex.Message;
        }
        plannerSw.Stop();

        // 【B+】子调用指标：Planner（agent_pro）——orchestrator 内部真实调用可观测、样本自动积累
        await RecordSubMetricAsync(
            serviceId: "agent_pro",
            routingTier: "OrchestratorPlanner",
            userInput: userInput,
            success: planError is null && plan is not null && plan.Files.Count > 0,
            totalMs: plannerSw.ElapsedMilliseconds,
            errorSummary: planError,
            ct: ct);

        if (planError is not null)
        {
            yield return new AgentEvent.Error($"规划异常：{planError}");
            yield break;
        }

        // 把环境约束同步给 Plan，供 Executor 生成/修复文件时附加（单一来源）
        if (plan is not null)
        {
            plan.EnvironmentConstraint = ExternalTaskDetector.Detect(userInput) ?? "";
        }

        if (plan is null || plan.Files.Count == 0)
        {
            yield return new AgentEvent.Error("规划失败，任务终止。");
            yield break;
        }

        // 【任务1 工程增量计划校验】ProjectIncremental 特征任务：Plan 中"已有文件"≥2 判定为
        // 整体重写倾向（实测：模型把 Order.cs/OrderService.cs/Program.cs 全列入计划再整体重写 →
        // 写前才被 P-D 拦截，白白跑一轮 Executor）。此处早拦截，走规划失败升级链重新规划；
        // 合法形态（新增 1 文件 + 至多 1 处接入修改）已有文件数 ≤1，放行。确定性集合运算，非提示词。
        if (plan is not null && _options.ProjectRoot is not null &&
            Directory.Exists(_options.ProjectRoot))
        {
            var feature = Routing.TaskFeatureClassifier.Classify(userInput);
            if (feature == Routing.TaskFeatureClassifier.Feature.ProjectIncremental)
            {
                var existingSet = EnumerateExistingFiles(_options.ProjectRoot);
                var planExisting = CheckIncrementalPlanConflicts(
                    plan.Files.Select(f => f.Path), existingSet);
                if (planExisting.Count >= 2)
                {
                    _logger.LogWarning(
                        "[工程增量计划校验] 计划含已有文件 {Count} 个（{Files}），判定整体重写倾向，拒绝计划。",
                        planExisting.Count, string.Join(", ", planExisting.Take(5)));
                    yield return new AgentEvent.Error(
                        $"规划失败：工程增量任务不应修改多个已有文件。计划包含已有文件：{string.Join(", ", planExisting.Take(6))}。"
                        + "本任务应只新增文件（如 ShippingService.cs），已有文件最多允许 1 处接入修改，禁止整体重写。");
                    yield break;
                }
            }
        }

        // 发出规划事件
        yield return new AgentEvent.PlanGenerated(
            plan.Summary,
            plan.Language,
            plan.Files.Select(f => f.Path).ToList());

        // ═══════════ 阶段 2：逐文件生成 ═══════════
        var writtenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 保存所有已写入文件的内容：
        //   - 供后续 Executor 生成新文件时参考接口
        //   - 供 Validator 修复时提供全项目上下文
        var fileContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var spec in plan.Files)
        {
            ct.ThrowIfCancellationRequested();

            string? content = null;
            string? execError = null;
            var execSw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // 把已生成的文件内容传进去
                // 让 Executor 在生成后续文件时能看到前面文件的真实接口
                content = await _executor.GenerateFileAsync(spec, plan, fileContents, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Executor 生成 {Path} 失败。", spec.Path);
                execError = ex.Message;
            }
            execSw.Stop();

            // 【B+】子调用指标：Executor（small）——生成成功与否即该文件真实样本
            await RecordSubMetricAsync(
                serviceId: "small",
                routingTier: "OrchestratorExecutor",
                userInput: userInput,
                success: execError is null && content is not null,
                totalMs: execSw.ElapsedMilliseconds,
                errorSummary: execError,
                ct: ct);

            // yield 必须出 catch 块
            if (execError is not null)
            {
                yield return new AgentEvent.Error($"生成 {spec.Path} 失败：{execError}");
                yield break;
            }

            if (content is null)
            {
                yield return new AgentEvent.Error($"生成 {spec.Path} 返回空内容。");
                yield break;
            }

            // 写入文件（走 write_file 工具）
            var (writeOk, writeResult) = await WriteFileAsync(spec.Path, content, ct);
            yield return new AgentEvent.ToolCall("write_file", BuildArgsJson(spec.Path, content));
            yield return new AgentEvent.ToolResultEvent("write_file", writeResult, writeOk);

            if (writeOk)
            {
                writtenFiles.Add(spec.Path);
                fileContents[spec.Path] = content;
                yield return new AgentEvent.FileWritten(spec.Path, content.Length);
            }
        }

        // ═══════════ 阶段 3：验证 + 修复 ═══════════
        if (string.IsNullOrWhiteSpace(plan.RunCommand))
        {
            yield return new AgentEvent.FinalAnswer(
                $"已创建 {writtenFiles.Count} 个文件，但 Plan 未提供验证命令。");
            yield break;
        }

        var retries = 0;
        string lastError = "";
        string? lastErrorSig = null;   // 批次4：修复重试错误签名（相同=无进展熔断）
        bool success = false;
        bool typeDefInjected = false;  // 【批次3·B】类型缺失定义注入：每任务只注入一次（避免污染）

        // 【D2】重试上限数据校准：特征类历史成功率低（样本 ≥3 且 <50%）→ 收紧到 1 次修复。
        // 数据依据（D1 归因）：工程增量类任务历史 0 成功（30B 能力边界），3 次重试纯浪费 CPU 推理
        // （约 9 分钟/任务）；收紧后失败即回滚，不再空转。成功率高/样本不足的特征类保持配置值。
        var maxRetries = _options.ExecutorMaxRetries;
        if (_featureAggregator is not null)
        {
            var feature = Routing.TaskFeatureClassifier.Classify(userInput);
            var stats = _featureAggregator.Get(feature);
            if (stats is not null && stats.TotalCount >= 3 &&
                (double)stats.SuccessCount / stats.TotalCount < 0.5)
            {
                maxRetries = 1;
                _logger.LogInformation(
                    "[D2] 特征类 {Feature} 历史成功率 {Rate:P0}（{Ok}/{Total}），重试上限校准 {Default} → {Calibrated}",
                    Routing.TaskFeatureClassifier.FeatureName(feature),
                    (double)stats.SuccessCount / stats.TotalCount,
                    stats.SuccessCount, stats.TotalCount,
                    _options.ExecutorMaxRetries, maxRetries);
            }
        }

        while (retries <= maxRetries)
        {
            ct.ThrowIfCancellationRequested();

            // ═══════════ 阶段 3.1：Lint 检查 ═══════════
            var lintResult = await _lint.LintAsync(
                workspaceRoot,
                plan.Files.Select(f => f.Path).ToList(),
                plan.Language,
                ct);

            // Lint 规则分级：只有严重规则才阻断
            //   F821 未定义名 → 阻断（会运行时失败）
            //   F811 重复定义 → 阻断
            //   F401 未使用 import → 警告，不阻断
            //   F841 未使用变量 → 警告，不阻断
            var blockingRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "F821",
                "F811"
            };

            var blockingErrors = lintResult.Errors
                .Where(e => blockingRules.Contains(e.RuleId))
                .ToList();

            var warningErrors = lintResult.Errors
                .Where(e => !blockingRules.Contains(e.RuleId))
                .ToList();

            // 警告只记日志，不阻断
            if (warningErrors.Count > 0)
            {
                _logger.LogInformation(
                    "Lint 有 {Count} 个警告（不阻断）：",
                    warningErrors.Count);

                foreach (var e in warningErrors)
                {
                    _logger.LogInformation(
                        "  {File}({Line},{Col}) [{Rule}] {Msg}",
                        e.FilePath, e.Line, e.Column, e.RuleId, e.Message);
                }
            }

            // 只有严重错误才进入 lint 修复流程
            if (blockingErrors.Count > 0)
            {
                _logger.LogInformation(
                    "Lint 失败（严重）：{Count} 个错误",
                    blockingErrors.Count);

                foreach (var e in blockingErrors)
                {
                    _logger.LogInformation(
                        "  {File}({Line},{Col}) [{Rule}] {Msg}",
                        e.FilePath, e.Line, e.Column, e.RuleId, e.Message);
                }

                if (retries >= maxRetries)
                    break;

                // ═══════════ 按文件分组，逐个修复 ═══════════
                var errorsByFile = blockingErrors
                    .Select(e => new
                    {
                        Error = e,
                        PlanPath = MatchPlanFile(e.FilePath, plan)
                    })
                    .Where(x => x.PlanPath is not null)
                    .GroupBy(x => x.PlanPath!)
                    .ToList();

                if (errorsByFile.Count == 0)
                {
                    _logger.LogWarning("无法匹配任何 lint 错误到 Plan 文件。");
                    yield return new AgentEvent.Error(
                        $"Lint 错误无法定位到文件：\n{Truncate(string.Join("\n", blockingErrors.Select(e => e.ToDiagnosticString())), 500)}");
                    yield break;
                }

                _logger.LogInformation(
                    "Lint 错误涉及 {Count} 个文件，逐个修复。",
                    errorsByFile.Count);

                var anyFixed = false;

                foreach (var group in errorsByFile)
                {
                    ct.ThrowIfCancellationRequested();

                    var lintFailedPath = group.Key;
                    var lintErrorMsg = string.Join("\n",
                        group.Select(x => x.Error.ToDiagnosticString()));

                    _logger.LogInformation(
                        "修复 {Path}（含 {Count} 个错误）",
                        lintFailedPath, group.Count());

                    // 读取当前内容
                    var lintCurrentContent = await ReadFileAsync(lintFailedPath, ct);
                    if (lintCurrentContent is null)
                    {
                        _logger.LogWarning("无法读取 {Path}，跳过。", lintFailedPath);
                        continue;
                    }

                    // 找 FileSpec
                    var lintSpec = plan.Files.FirstOrDefault(f =>
                        f.Path.Equals(lintFailedPath, StringComparison.OrdinalIgnoreCase));
                    if (lintSpec is null)
                    {
                        _logger.LogWarning("找不到 {Path} 的 FileSpec。", lintFailedPath);
                        continue;
                    }

                    // 调用修复器
                    string? lintFixedContent = null;
                    string? lintFixError = null;

                    try
                    {
                        lintFixedContent = await _executor.FixFileAsync(
                            lintSpec, lintCurrentContent, lintErrorMsg, plan, fileContents, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Executor 修复 {Path} 失败。", lintFailedPath);
                        lintFixError = ex.Message;
                    }

                    if (lintFixError is not null || lintFixedContent is null)
                    {
                        _logger.LogWarning(
                            "修复 {Path} 失败：{Msg}",
                            lintFailedPath, lintFixError ?? "返回空内容");
                        continue;
                    }

                    // 检测是否有实质变化
                    var lintDiffRatio = ComputeDiffRatio(lintCurrentContent, lintFixedContent);
                    if (lintDiffRatio < 0.02)
                    {
                        _logger.LogWarning(
                            "修复 {Path} 变化过小（{Ratio:P1}），跳过。",
                            lintFailedPath, lintDiffRatio);
                        continue;
                    }

                    // 写回
                    var (lintWOk, lintWMsg) = await WriteFileAsync(lintFailedPath, lintFixedContent, ct);
                    yield return new AgentEvent.ToolCall("write_file", BuildArgsJson(lintFailedPath, lintFixedContent));
                    yield return new AgentEvent.ToolResultEvent("write_file", lintWMsg, lintWOk);

                    if (lintWOk)
                    {
                        fileContents[lintFailedPath] = lintFixedContent;
                        anyFixed = true;
                    }
                }

                if (!anyFixed)
                {
                    _logger.LogWarning("所有 Lint 错误修复失败，终止。");
                    yield return new AgentEvent.Error(
                        $"Lint 修复失败：涉及 {errorsByFile.Count} 个文件，全部未成功修改。");
                    yield break;
                }

                retries++;

                // 回循环顶部重新 lint
                continue;
            }

            // ═══════════ 阶段 3.2：Lint 通过 → 跑 run_command ═══════════
            // 【关键】如果是 C# 项目，run_command 自动加 --project，避免多 csproj 冲突
            var effectiveRunCommand = plan.RunCommand;

            if (plan.Language == "csharp" && !string.IsNullOrWhiteSpace(plan.RunCommand))
            {
                var csprojFile = plan.Files.FirstOrDefault(f =>
                    f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));

                // 【批次3 续·防 Planner 幻觉 csproj 名】工程模式下，csproj 一律用工程根
                // 实际存在的文件（E2E 实证：Planner 幻觉 OrderSystem.csproj → run_command
                // 卡死重试 3 次，B 注入等修复路径根本没机会跑到编译阶段）。
                if (_options.ProjectRoot is not null && Directory.Exists(_options.ProjectRoot))
                {
                    var realCsproj = Directory.GetFiles(_options.ProjectRoot, "*.csproj",
                        SearchOption.TopDirectoryOnly)
                        .FirstOrDefault(p =>
                            !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                            !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar));
                    if (realCsproj is not null)
                    {
                        csprojFile = new Agent.Core.Models.FileSpec { Path = Path.GetFileName(realCsproj) };
                        if (plan.RunCommand.Contains("--project", StringComparison.OrdinalIgnoreCase))
                        {
                            // Planner 已写死幻觉项目名 → 替换为实际名
                            effectiveRunCommand = System.Text.RegularExpressions.Regex.Replace(
                                effectiveRunCommand,
                                @"--project\s+[\w\.\\/]+",
                                $"--project {Path.GetFileName(realCsproj)}",
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        }
                    }
                }

                if (csprojFile is not null)
                {
                    if (!effectiveRunCommand.Contains("--project") &&
                        !effectiveRunCommand.Contains(" -p "))
                    {
                        effectiveRunCommand = System.Text.RegularExpressions.Regex.Replace(
                            effectiveRunCommand,
                            @"\bdotnet\s+run\b",
                            $"dotnet run --project {csprojFile.Path}",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    }
                }
            }

            var (runOk, runOutput) = await RunCommandAsync(effectiveRunCommand, plan.ExpectedOutput, ct);
            yield return new AgentEvent.ToolCall("run_command", BuildCommandJson(effectiveRunCommand));
            yield return new AgentEvent.ToolResultEvent("run_command", runOutput, runOk);

            if (runOk)
            {
                success = true;
                break;
            }

            lastError = runOutput;

            // 【批次4 发现】修复重试无进展熔断：错误签名未变化 → 提前终止（不等满 3 次）。
            // 数据实证：3 条"任务未通过验证（重试 3 次）"同错误反复修复无效；
            // 错误签名 = 编译错误码 + 文件行号 指纹，签名相同 = 修复未推进。
            var newSig = ExtractErrorSignature(runOutput);
            if (newSig is not null && newSig == lastErrorSig && retries > 0)
            {
                _logger.LogWarning("修复重试无进展：错误签名未变化（{Sig}），提前终止。", newSig);
                yield return new AgentEvent.Error(
                    $"修复重试无进展：错误签名未变化（{Truncate(runOutput, 120)}），判失败。");
                yield break;
            }
            lastErrorSig = newSig;

            if (retries >= maxRetries)
                break;

            // 定位出错的文件
            var failedPath = LocateErrorFile(runOutput, plan);
            if (failedPath is null)
            {
                _logger.LogWarning("无法从错误信息定位文件：{Output}", runOutput);
                break;
            }

            _logger.LogInformation("定位到出错文件：{Path}，第 {Retry} 次修复。",
                failedPath, retries + 1);

            // 读取当前内容
            var currentContent = await ReadFileAsync(failedPath, ct);
            if (currentContent is null)
            {
                _logger.LogWarning("无法读取 {Path}，跳过修复。", failedPath);
                break;
            }

            // 找对应的 FileSpec
            var spec = plan.Files.FirstOrDefault(f =>
                f.Path.Equals(failedPath, StringComparison.OrdinalIgnoreCase));
            if (spec is null)
            {
                _logger.LogWarning("找不到 {Path} 的 FileSpec。", failedPath);
                break;
            }

            // 让 Executor 修复
            string? fixedContent = null;
            string? fixError = null;

            // 【批次3·B 类型缺失定义注入】错误含"类型未包含成员/找不到类型" → 找该类型定义
            // 文件，把【完整定义内容】注入修复上下文（不是位置指引——E2E 已实证 30B 不会跟着
            // 位置去 read_file，必须让定义直接出现在修复依据里）。
            // 注入源：① 计划文件 fileContents；② 工程根扫描（工程已有类型如 Order.cs 不在
            // fileContents，E2E 实证——必须回退读工程根文件，否则 B 永不触发）。
            // 约束：只在修复阶段触发；每任务只注入一次；只注入第一个命中的类型。
            string? typeDefContext = null;
            if (!typeDefInjected)
            {
                var missingType = ExtractMissingType(runOutput);
                if (missingType is not null)
                {
                    string? defFilePath = null;
                    string? defContent = null;
                    var defKv = fileContents.FirstOrDefault(kv =>
                        kv.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                        kv.Value.Contains($"class {missingType}", StringComparison.OrdinalIgnoreCase));
                    if (defKv.Key is not null)
                    {
                        defFilePath = defKv.Key;
                        defContent = defKv.Value;
                    }
                    else if (_options.ProjectRoot is not null && Directory.Exists(_options.ProjectRoot))
                    {
                        // 工程已有类型：扫工程根 .cs 文件（排除 bin/obj）找 class 定义
                        foreach (var cs in Directory.GetFiles(_options.ProjectRoot, "*.cs",
                                     SearchOption.TopDirectoryOnly))
                        {
                            if (cs.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) ||
                                cs.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                                continue;
                            var text = await ReadFileAsync(cs, ct);
                            if (text is not null &&
                                text.Contains($"class {missingType}", StringComparison.OrdinalIgnoreCase))
                            {
                                defFilePath = Path.GetFileName(cs);
                                defContent = text;
                                break;
                            }
                        }
                    }

                    if (defFilePath is not null && defContent is not null)
                    {
                        typeDefContext =
                            $"\n\n目标类型 {missingType} 的当前定义如下（文件 {defFilePath} 完整内容）：\n```\n{defContent}\n```";
                        typeDefInjected = true;
                        _logger.LogInformation(
                            "类型缺失 {Type}：注入完整定义（{File}，{Len} 字符）。",
                            missingType, defFilePath, defContent.Length);
                    }
                }
            }

            try
            {
                // 把全项目文件内容传进去，让模型能看到其它文件的接口
                var fixInput = typeDefContext is null ? runOutput : runOutput + typeDefContext;
                fixedContent = await _executor.FixFileAsync(
                    spec, currentContent, fixInput, plan, fileContents, ct);

                // 修复成功后，同步更新 fileContents
                if (fixedContent is not null)
                {
                    fileContents[failedPath] = fixedContent;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Executor 修复 {Path} 失败。", failedPath);
                fixError = ex.Message;
            }

            // yield 必须出 catch 块
            if (fixError is not null)
            {
                yield return new AgentEvent.Error($"修复 {failedPath} 失败：{fixError}");
                yield break;
            }

            if (fixedContent is null)
            {
                _logger.LogWarning("修复 {Path} 返回空内容。", failedPath);
                break;
            }

            // 检测修复内容是否真的变化了
            if (fixedContent == currentContent)
            {
                _logger.LogWarning(
                    "修复 {Path} 返回的内容和原文完全相同，说明模型没理解错误。终止重试。",
                    failedPath);

                yield return new AgentEvent.Error(
                    $"修复失败：模型输出与原文相同。错误信息：\n{Truncate(runOutput, 500)}");
                yield break;
            }

            // 写回
            var (wOk, wMsg) = await WriteFileAsync(failedPath, fixedContent, ct);
            yield return new AgentEvent.ToolCall("write_file", BuildArgsJson(failedPath, fixedContent));
            yield return new AgentEvent.ToolResultEvent("write_file", wMsg, wOk);

            if (!wOk)
            {
                _logger.LogWarning("修复后写回 {Path} 失败。", failedPath);
                break;
            }

            retries++;
        }

        // ═══════════ 交付 ═══════════
        if (success)
        {
            yield return new AgentEvent.FinalAnswer(
                $"{plan.Summary}\n" +
                $"已创建 {writtenFiles.Count} 个文件：{string.Join("、", writtenFiles)}。\n" +
                $"验证通过：{plan.RunCommand}");
        }
        else
        {
            // 【批次1·B】顶层失败增强：用户显式写目标不存在 → 失败归因可读（"目标未落地"而非泛化验证失败）
            var targetNote = "";
            if (Project.ProjectPathDetector.TryGetWriteTarget(userInput, out var wt, out var wf))
            {
                var exists = wf ? File.Exists(wt) : Directory.Exists(wt);
                if (!exists)
                    targetNote = $"\n[目标未落地] 用户显式写目标 {wt} 不存在（写入位置与用户指定不符）。";
            }
            yield return new AgentEvent.Error(
                $"任务未通过验证（重试 {retries} 次）。最后错误：\n{Truncate(lastError, 500)}{targetNote}");
        }
    }

    // ─────────── 工具封装 ───────────

    /// <summary>调用 write_file 工具。</summary>
    private async Task<(bool ok, string result)> WriteFileAsync(
        string path, string content, CancellationToken ct)
    {
        if (!_tools.TryGet("write_file", out var tool))
            return (false, "错误：write_file 工具未注册。");

        var args = JsonSerializer.SerializeToElement(new
        {
            path = path,
            content = content
        });

        try
        {
            // 【10-13 批次4】读结构化 Success 字段（不再匹配"错误："前缀）
            var result = await tool.InvokeAsync(args, ct);
            var ok = result.Success;
            return (ok, result);
        }
        catch (Exception ex)
        {
            return (false, $"工具异常：{ex.Message}");
        }
    }

    /// <summary>调用 run_command 工具。</summary>
    /// <summary>调用 run_command 工具。</summary>
    private async Task<(bool ok, string output)> RunCommandAsync(
        string command, string? expectedOutput, CancellationToken ct)
    {
        if (!_tools.TryGet("run_command", out var tool))
            return (false, "错误：run_command 工具未注册。");

        var args = JsonSerializer.SerializeToElement(new
        {
            command = command
        });

        try
        {
            // 【10-13 批次4】ToolResult 双轨：Success 供判定，Message 文本供后续解析（stdout/stderr 提取）
            var toolResult = await tool.InvokeAsync(args, ct);
            var result = toolResult.Message;

            // ─────────── 检查 0：工具层拒绝（超时/白名单/路径边界）───────────
            // 【修复】读结构化 Success 字段（超时/白名单拒绝不再靠"错误"前缀文本判定）
            if (!toolResult.Success)
            {
                return (false, result);
            }

            // ─────────── 检查 0.5：外部凭据失败（与 AgentLoop 检查 0.5 双路径统一） ───────────
            // 模型常用需要注册 API Key 的外部服务（如 OpenWeatherMap），环境没有凭据必然失败。
            // 凭据失败是硬失败，不做 expected 容错（与 AgentLoop 一致）。
            // 公共判定由 OutputVerifier.HasCredentialFailure 提供（双路径统一，单一来源）。
            if (OutputVerifier.HasCredentialFailure(result, out var credentialPattern))
            {
                _logger.LogWarning(
                    "运行输出含外部凭据失败信号「{Pattern}」，归因为需要 API Key。",
                    credentialPattern);

                return (false, result + "\n[检测] 错误原因是外部服务需要 API Key/凭据，但当前环境没有可用凭据。" +
                        "请改用免 Key 实现，可直接参考以下可运行代码（无需注册任何密钥）：\n" +
                        "import requests\n" +
                        "def get_weather_by_ip(ip):\n" +
                        "    loc = requests.get(f'http://ip-api.com/json/{ip}').json()\n" +
                        "    w = requests.get(f'https://wttr.in/{loc[\"city\"]}?format=j1').json()\n" +
                        "    return w['current_condition'][0]");
            }

            // ─────────── 检查 1：退出码 ───────────
            var match = Regex.Match(result, @"退出码：(\d+)");
            if (match.Success
                && int.TryParse(match.Groups[1].Value, out var code)
                && code != 0)
            {
                return (false, result);
            }

            // ─────────── 提取 stdout 和 stderr ───────────
            var stdoutStart = result.IndexOf("--- stdout ---");
            var stderrStart = result.IndexOf("--- stderr ---");

            var stdout = "";
            var stderr = "";

            if (stdoutStart >= 0)
            {
                var stdoutEnd = stderrStart > stdoutStart ? stderrStart : result.Length;
                stdout = result.Substring(stdoutStart, stdoutEnd - stdoutStart)
                    .Replace("--- stdout ---", "").Trim();
            }

            if (stderrStart >= 0)
            {
                stderr = result.Substring(stderrStart)
                    .Replace("--- stderr ---", "").Trim();
            }

            // ─────────── 检查 2：stderr 里的真实错误（强失败） ───────────
            // 程序正常不该往 stderr 写东西，但要排除纯警告。
            // 无害警告剥离由 OutputVerifier.StripHarmlessWarnings 提供（双路径统一，单一来源）；
            // 剥离后非空即失败（阈值与 AgentLoop 统一，不再按 >10 字符）。
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                var stderrMeaningful = OutputVerifier.StripHarmlessWarnings(stderr);

                if (!string.IsNullOrWhiteSpace(stderrMeaningful))
                {
                    _logger.LogDebug("stderr 含错误：{Stderr}", Truncate(stderrMeaningful, 200));
                    return (false, result);
                }
            }

            // ─────────── 检查 3：stdout 里的"结构性错误" ───────────
            // 只匹配"程序崩溃 / 编译失败"类信号
            // 不匹配"程序自己打印的 Error:"（如演示错误处理）
            // 公共判定由 OutputVerifier.HasStructuralError 提供（双路径统一，信号集取并集）。
            if (OutputVerifier.HasStructuralError(stdout, out var structuralSignal))
            {
                _logger.LogDebug("stdout 含结构性错误：{Signal}", structuralSignal);
                return (false, result);
            }

            // ─────────── 检查 3.5：输出语义有效性（防"假阳性成功"） ───────────
            // 退出码 0 不代表逻辑正确：程序可能走了"失败路径"（查询未找到、操作失败）却正常退出。
            // 若 stdout 出现强失败信号（通用失败词 / JSON 错误字段），而 expected_output 并未预期该信号
            // → 判失败，触发修复。成功证据判定（【10-08 A 证据链先行】）：
            // 命中软信号但存在成功证据（演示分支/成功报告）→ 不判失败，打 warning 可观测。
            // 公共判定由 OutputVerifier.HasGenericFailure 提供（双路径统一，单一来源，含查找类信号）。
            string genericFailureReason = "";
            bool hadEvidence = false;
            if (!string.IsNullOrWhiteSpace(stdout) &&
                OutputVerifier.HasGenericFailure(stdout, expectedOutput, out genericFailureReason, out hadEvidence))
            {
                _logger.LogWarning(
                    "输出含失败信号「{Reason}」且预期未包含，判为逻辑失败（假阳性防护）。",
                    genericFailureReason);

                var expectedBlock = string.IsNullOrWhiteSpace(expectedOutput)
                    ? ""
                    : $"\n请修改程序，使实际输出与计划预期一致。\n计划预期输出：\n{expectedOutput}";

                return (false, result + $"\n[检测] 输出含失败信号「{genericFailureReason}」，与预期不符（防假阳性）。{expectedBlock}");
            }
            if (hadEvidence)
            {
                // 【B 阶段】边缘成功：命中软信号但存在成功证据——按成功处理，
                // 注入 [边缘成功] 标记，metrics 装饰器据此标 is_suspicious=1（数据回流）。
                return (true, result + $"\n[边缘成功] 输出含失败信号「{genericFailureReason}」但存在成功证据（演示分支/成功报告），按成功处理。");
            }

            // （检查 3.5b 已并入检查 3.5：JSON 错误字段由 OutputVerifier.HasGenericFailure 统一判定）

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
                if (stdoutLines.Length > 0 &&
                    OutputVerifier.IsPlaceholderOutput(stdoutLines, out var placeholderCount, out var placeholderTotal))
                {
                    _logger.LogWarning(
                        "输出疑似占位符（{Count}/{Total} 行）",
                        placeholderCount, placeholderTotal);

                    return (false, result + "\n[检测] 输出疑似占位符。");
                }

                // 4.2 模板输出检测（方案 1 降级：计划预期内容不判模板）
                if (OutputVerifier.IsTemplateOutput(stdoutLines) &&
                    !OutputVerifier.IsTaskExpectedOutput(expectedOutput, stdoutLines))
                {
                    return (false, result + "\n[检测] 输出疑似模板内容。");
                }

                // 【P0-2】严格校验开关：expected 每一非空行须在 stdout 精确出现
                // 【H3】工程模式（ProjectRoot 非空）默认开严格——工程改动验证要求逐项兑现；
                // 普通生成任务保持宽松（StrictOutputVerification 默认 false）。
                var strictActive = _options.StrictOutputVerification || _options.ProjectRoot is not null;
                if (strictActive &&
                    !string.IsNullOrWhiteSpace(expectedOutput) &&
                    !OutputVerifier.IsTaskExpectedOutputStrict(expectedOutput, stdoutLines))
                {
                    return (false, result + "\n[检测] 输出与计划预期不完全一致（严格模式：预期每一行须在输出中精确出现）。");
                }
            }

            // 所有检查通过
            return (true, result);
        }
        catch (Exception ex)
        {
            return (false, $"工具异常：{ex.Message}");
        }
    }

    /// <summary>调用 read_file 工具。</summary>
    private async Task<string?> ReadFileAsync(string path, CancellationToken ct)
    {
        if (!_tools.TryGet("read_file", out var tool))
            return null;

        var args = JsonSerializer.SerializeToElement(new { path = path });

        try
        {
            var content = await tool.InvokeAsync(args, ct);
            return content.Success ? content.Message : null;
        }
        catch
        {
            return null;
        }
    }

    // ─────────── 错误定位 ───────────

    /// <summary>
    /// 从编译/运行错误输出里定位出错的文件。
    /// <summary>提取编译/运行错误的指纹（错误码 + 文件行号），用于修复重试无进展判定。
    /// C#：`File.cs(10,13): error CS1002` → CS1002 + 10；Python：`line 5` + 错误名。</summary>
    public static string? ExtractErrorSignature(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var sb = new System.Text.StringBuilder();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(output, @"\bCS\d+\b"))
            sb.Append(m.Value).Append(';');
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(output, @"\b[A-Z][A-Za-z]+Error\b"))
            sb.Append(m.Value).Append(';');
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(output, @"\.(?:cs|py|js|ts|go|java)\((\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            sb.Append(m.Groups[1].Value).Append(';');
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(output, @"\bline\s+(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            sb.Append(m.Groups[1].Value).Append(';');
        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>
    /// 【批次3·B】从编译错误输出里提取"类型缺失"的目标类型名（确定性匹配，不靠模型）。
    /// 命中模式（支持弯/直引号）：
    ///   “X”未包含“Y”的定义   → 查 X（CS0117/CS1061 成员缺失）
    ///   未能找到类型或命名空间名“X” → 查 X（CS0246）
    ///   找不到类型“X”         → 查 X（兜底）
    /// 只返回第一个命中的类型（约束：只注入 1 个符号）。
    /// </summary>
    public static string? ExtractMissingType(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(output,
            @"[“""]([A-Za-z_][A-Za-z0-9_]*)[”""]未包含[“""]([A-Za-z_][A-Za-z0-9_]*)[”""]的定义");
        if (m.Success) return m.Groups[1].Value;
        m = System.Text.RegularExpressions.Regex.Match(output,
            @"未能找到类型或命名空间名[“""]([A-Za-z_][A-Za-z0-9_]*)[”""]");
        if (m.Success) return m.Groups[1].Value;
        m = System.Text.RegularExpressions.Regex.Match(output,
            @"找不到类型[“""]([A-Za-z_][A-Za-z0-9_]*)[”""]");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// 支持 C# 和 Python 两种错误格式。
    /// </summary>
    private static string? LocateErrorFile(string output, Plan plan)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        // NETSDK 错误 → 通常是 csproj 问题（只对真正的 error 生效）
        if (output.Contains("error NETSDK", StringComparison.OrdinalIgnoreCase))
        {
            var csprojMatch = Regex.Match(output, @"([^\s\[\]]+\.csproj)");
            if (csprojMatch.Success)
            {
                var file = csprojMatch.Groups[1].Value;
                var matched = MatchPlanFile(file, plan);
                if (matched is not null) return matched;
            }

            return plan.Files.FirstOrDefault(f =>
                f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))?.Path;
        }

        // C# 编译错误：File.cs(10,15): error CS1234
        // 【关键修复】允许路径里有空格（原 \s 排除了空格，导致 "D:\My Projects\..." 匹配失败）
        var csharpMatch = Regex.Match(output, @"([^\r\n(]+\.cs)\(\d+,\d+\):\s*error");
        if (csharpMatch.Success)
        {
            var file = csharpMatch.Groups[1].Value;
            return MatchPlanFile(file, plan);
        }

        // Python 错误格式：File "path/to/file.py", line N
        var pythonMatch = Regex.Match(output, @"File ""([^""]+\.py)""");
       
        if (pythonMatch.Success)
        {
            var file = pythonMatch.Groups[1].Value;
            return MatchPlanFile(file, plan);
        }

        // argparse 错误兜底：从 "usage: xxx.py" 提取文件名
        var usageMatch = Regex.Match(output, @"usage:\s*([^\r\n]+?\.py)");
        if (usageMatch.Success)
        {
            var file = usageMatch.Groups[1].Value;
            var matched = MatchPlanFile(file, plan);
            if (matched is not null) return matched;
        }

        // 通用兜底：找入口文件
        var entryCandidates = new[]
        {
            "main.py", "Main.py", "app.py", "App.py", "run.py",
            "Program.cs", "Main.cs",
            "index.js", "main.js", "app.js"
        };

        foreach (var candidate in entryCandidates)
        {
            var spec = plan.Files.FirstOrDefault(f =>
                Path.GetFileName(f.Path).Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (spec is not null)
            {
                return spec.Path;
            }
        }

        // 最后兜底：返回第一个文件
        return plan.Files.FirstOrDefault()?.Path;
    }

    /// <summary>
    /// 把错误信息里的文件路径匹配到 Plan 里的相对路径。
    /// </summary>
    private static string? MatchPlanFile(string errorFilePath, Plan plan)
    {
        if (string.IsNullOrEmpty(errorFilePath)) return null;

        var normalized = errorFilePath.Replace('\\', '/');

        foreach (var spec in plan.Files)
        {
            var specNorm = spec.Path.Replace('\\', '/');

            if (normalized.EndsWith(specNorm, StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains(specNorm, StringComparison.OrdinalIgnoreCase))
            {
                return spec.Path;
            }
        }

        return null;
    }

    // ─────────── 辅助 ───────────

    /// <summary>
    /// 【工程图谱增强】构建规划提示附加块：类型分布摘要 + 用户输入提及文件的改动影响面（反向依赖）。
    /// 让 Planner 在多文件修改时知道工程全貌（符号在哪、改谁波及谁），产出跨文件一致的规划。
    /// 纯确定性（符号索引 + 依赖分析），失败降级为 null 不影响主流程。
    /// </summary>
    private string? BuildEngineeringGraphBlock(string userInput)
    {
        if (_options.ProjectRoot is null || !Directory.Exists(_options.ProjectRoot))
            return null;
        try
        {
            var symbols = SymbolIndexer.Build(_options.ProjectRoot);
            if (symbols.Count == 0)
                return null;

            var edges = DependencyAnalyzer.Build(_options.ProjectRoot, symbols);
            var sb = new StringBuilder();
            sb.AppendLine("【工程图谱（规划参考）】");

            // 已有文件清单（前 40 个）：工程模式下的真实文件（含 csproj/sln），
            // 提示 Planner：这些文件已存在，涉及它们时优先增量修改（edit_file），不要整体重写。
            var existingFiles = EnumerateExistingFiles(_options.ProjectRoot).Take(40).ToList();
            if (existingFiles.Count > 0)
            {
                sb.AppendLine("已有文件清单（已存在，勿整体重写；修改用 edit_file 增量）：");
                sb.AppendLine("  " + string.Join(", ", existingFiles));
            }

            // 类型分布摘要：文件 → 类型符号（前 30 文件，每文件前 6 个）
            var typesByFile = symbols
                .Where(s => s.Kind is "class" or "interface" or "enum" or "record" or "struct")
                .GroupBy(s => s.File)
                .OrderByDescending(g => g.Count())
                .Take(30);
            foreach (var g in typesByFile)
            {
                var names = string.Join(", ", g.Select(s => s.Symbol).Distinct().Take(6));
                sb.AppendLine($"  {g.Key}：{names}");
            }

            // 用户输入提及的文件 → 改动影响面（反向依赖）
            var mentioned = Regex.Matches(userInput, @"[\w./\\\-]+\.(?:cs|py|js|ts|java|go)", RegexOptions.IgnoreCase)
                .Select(m => m.Value.Replace('\\', '/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (mentioned.Count > 0)
            {
                sb.AppendLine("改动影响面（用户提及文件的反向依赖）：");
                foreach (var f in mentioned)
                {
                    var reverse = DependencyAnalyzer.GetReverseDependencies(f, edges);
                    if (reverse.Count > 0)
                        sb.AppendLine($"  {f} 被引用 ← {string.Join(", ", reverse.Take(8))}");
                }
            }

            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 【任务1 工程增量计划校验】判定 Plan 与工程已有文件的冲突（确定性集合运算，非提示词）。
    /// 返回 Plan 中引用已有文件的路径列表（归一化 / 分隔、大小写不敏感）。
    /// </summary>
    public static List<string> CheckIncrementalPlanConflicts(
        IEnumerable<string> planPaths, IEnumerable<string> existingFiles)
    {
        var existingSet = existingFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return planPaths
            .Select(p => p.Replace('\\', '/'))
            .Where(p => existingSet.Contains(p))
            .ToList();
    }

    /// <summary>工程模式下列举工程代码/配置文件（供规划提示的"已有文件清单"）。</summary>
    private static IEnumerable<string> EnumerateExistingFiles(string projectRoot)    {
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".py", ".js", ".ts", ".java", ".go", ".csproj", ".sln", ".slnx", ".fsproj", ".vbproj"
        };
        var list = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(projectRoot, "*.*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(projectRoot, f).Replace('\\', '/');
                if (rel.Contains(".git", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("bin", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("obj", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("node_modules", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("workspace", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith(".vs", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (exts.Contains(Path.GetExtension(f)))
                    list.Add(rel);
            }
        }
        catch { }
        return list;
    }

    /// <summary>
    /// 【B+】记录 orchestrator 内部子调用指标（Planner=agent_pro / Executor=small）。
    /// 目的：复杂任务的内部真实调用可观测、样本自动积累（此前只记顶层 orchestrator，
    /// agent_pro 样本仅靠顶层路由产生，导致 30B Planner 能力不可见）。
    /// 纯记录，失败不中断主流程。
    /// </summary>
    private async Task RecordSubMetricAsync(
        string serviceId, string routingTier, string userInput,
        bool success, long totalMs, string? errorSummary, CancellationToken ct)
    {
        try
        {
            var now = DateTimeOffset.Now;
            await _metrics.RecordAsync(new TaskMetric
            {
                TaskId = Guid.NewGuid().ToString("N"),
                SessionId = "",
                UserInput = Truncate(userInput, 500),
                Mode = "orchestrator_sub",
                RoutingTier = routingTier,
                ServiceId = serviceId,
                RoutingReason = "orchestrator 内部调用（子指标）",
                Success = success,
                TotalMs = totalMs,
                ErrorSummary = errorSummary is null ? "" : Truncate(errorSummary, 500),
                StartedAt = now,
                FinishedAt = now
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("子指标记录失败（容忍）: {Msg}", ex.Message);
        }
    }

    /// <summary>构造 write_file 事件的参数 JSON。</summary>
    private static JsonElement BuildArgsJson(string path, string content)
    {
        return JsonSerializer.SerializeToElement(new
        {
            path = path,
            content = content
        });
    }

    /// <summary>构造 run_command 事件的参数 JSON。</summary>
    private static JsonElement BuildCommandJson(string command)
    {
        return JsonSerializer.SerializeToElement(new { command = command });
    }

    /// <summary>字符串截断。</summary>
    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...";

    /// <summary>
    /// 计算两个字符串的差异比例（0.0 = 完全相同，1.0 = 完全不同）。
    /// </summary>
    private static double ComputeDiffRatio(string a, string b)
    {
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 0.0;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 1.0;

        var maxLen = Math.Max(a.Length, b.Length);
        var diff = 0;

        for (var i = 0; i < maxLen; i++)
        {
            var ca = i < a.Length ? a[i] : '\0';
            var cb = i < b.Length ? b[i] : '\0';
            if (ca != cb) diff++;
        }

        return (double)diff / maxLen;
    }
}