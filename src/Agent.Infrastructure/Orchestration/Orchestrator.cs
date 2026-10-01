// ============================================================
// Orchestrator.cs
// 三阶段编排：Planner（规划）→ Executor（执行）→ Validator（验证修复）
// ============================================================

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agent.Core.Abstractions;
using Agent.Core.Models;
using Agent.Infrastructure.Quality;
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
    private readonly ILogger<Orchestrator> _logger;

    public Orchestrator(
        IPlannerService planner,
        IExecutorService executor,
        IToolRegistry tools,
        ILintService lint,
        AgentOptions options,
        ILogger<Orchestrator> logger)
    {
        _planner = planner;
        _executor = executor;
        _tools = tools;
        _lint = lint;
        _options = options;
        _logger = logger;
    }

    /// <summary>执行一次编排任务。</summary>
    public async IAsyncEnumerable<AgentEvent> RunAsync(string userInput,string workspaceRoot,[EnumeratorCancellation] CancellationToken ct)
    {
        // ═══════════ 阶段 1：规划 ═══════════
        Plan? plan = null;
        string? planError = null;

        try
        {
            // 外部数据任务：附加环境约束后交给 Planner（模型在规划阶段即知环境事实）
            var envConstraint = ExternalTaskDetector.Detect(userInput);
            plan = await _planner.GeneratePlanAsync(
                envConstraint is null ? userInput : userInput + "\n\n" + envConstraint,
                workspaceRoot, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Planner 调用异常。");
            planError = ex.Message;
        }

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
        bool success = false;

        while (retries <= _options.ExecutorMaxRetries)
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

                if (retries >= _options.ExecutorMaxRetries)
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

            if (retries >= _options.ExecutorMaxRetries)
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

            try
            {
                // 把全项目文件内容传进去，让模型能看到其它文件的接口
                fixedContent = await _executor.FixFileAsync(
                    spec, currentContent, runOutput, plan, fileContents, ct);

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
            yield return new AgentEvent.Error(
                $"任务未通过验证（重试 {retries} 次）。最后错误：\n{Truncate(lastError, 500)}");
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
            var result = await tool.InvokeAsync(args, ct);
            var ok = !result.StartsWith("错误");
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
            var result = await tool.InvokeAsync(args, ct);

            // ─────────── 检查 0：工具层拒绝（超时/白名单）───────────
            // 【修复】超时、白名单拒绝等输出以"错误"开头且可能不带退出码，
            // 必须判失败，否则交互式死循环程序会被误判为"验证通过"
            if (result.StartsWith("错误", StringComparison.Ordinal))
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
            // → 判失败，触发修复。双容错：预期含失败词（演示）或 stdout 含成功信号（正常报告）→ 放行。
            // 公共判定由 OutputVerifier.HasGenericFailure 提供（双路径统一，单一来源，含查找类信号）。
            if (!string.IsNullOrWhiteSpace(stdout) &&
                OutputVerifier.HasGenericFailure(stdout, expectedOutput, out var genericFailureReason))
            {
                _logger.LogWarning(
                    "输出含失败信号「{Reason}」且预期未包含，判为逻辑失败（假阳性防护）。",
                    genericFailureReason);

                var expectedBlock = string.IsNullOrWhiteSpace(expectedOutput)
                    ? ""
                    : $"\n请修改程序，使实际输出与计划预期一致。\n计划预期输出：\n{expectedOutput}";

                return (false, result + $"\n[检测] 输出含失败信号「{genericFailureReason}」，与预期不符（防假阳性）。{expectedBlock}");
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
            return content.StartsWith("错误") ? null : content;
        }
        catch
        {
            return null;
        }
    }

    // ─────────── 错误定位 ───────────

    /// <summary>
    /// 从编译/运行错误输出里定位出错的文件。
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
        // 【关键修复】允许路径里有空格（原 \s 排除了空格，导致 "X:\.NET AI\..." 匹配失败）
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