// ============================================================
// OrchestratorMetricsDecorator.cs
// 用装饰器模式给 Orchestrator 加指标采集。
// ============================================================

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Agent.Core.Models;
using Agent.Infrastructure.Orchestration;
using Microsoft.Extensions.Logging;

namespace Agent.Infrastructure.Metrics;

/// <summary>
/// Orchestrator 的指标装饰器。
/// </summary>
public sealed class OrchestratorMetricsDecorator : IOrchestrator
{
    private readonly IOrchestrator _inner;
    private readonly IMetricsCollector _metrics;
    private readonly ILogger<OrchestratorMetricsDecorator> _logger;

    public OrchestratorMetricsDecorator(
        IOrchestrator inner,
        IMetricsCollector metrics,
        ILogger<OrchestratorMetricsDecorator> logger)
    {
        _inner = inner;
        _metrics = metrics;
        _logger = logger;
    }

    public async IAsyncEnumerable<AgentEvent> RunAsync(
        string userInput,
        string workspaceRoot,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var taskId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.Now;
        var sw = Stopwatch.StartNew();

        var success = false;
        var errorSummary = "";
        var fileCount = 0;
        var writeCount = 0;
        var runCount = 0;
        var isSuspicious = false;
        // 【10-10 可疑成功治理·归因落库】记录首个可疑信号类别（与 AgentLoop 装饰器同构）
        var suspiciousReason = "";

        await foreach (var evt in _inner.RunAsync(userInput, workspaceRoot, ct))
        {
            switch (evt)
            {
                case AgentEvent.PlanGenerated pg:
                    fileCount = pg.FilePaths.Count;
                    break;

                case AgentEvent.ToolCall tc:
                    if (tc.Name.Contains("write_file")) writeCount++;
                    else if (tc.Name.Contains("run_command")) runCount++;
                    break;

                case AgentEvent.ToolResultEvent tr:
                    // 检测可疑信号
                    if (!string.IsNullOrEmpty(tr.Output) &&
                        (tr.Output.Contains("[检测]") ||
                         tr.Output.Contains("边缘成功") ||
                         tr.Output.Contains("疑似模板") ||
                         tr.Output.Contains("未真正实现")))
                    {
                        isSuspicious = true;
                        if (string.IsNullOrEmpty(suspiciousReason))
                        {
                            if (tr.Output.Contains("边缘成功")) suspiciousReason = "边缘成功（失败信号+成功证据）";
                            else if (tr.Output.Contains("疑似模板")) suspiciousReason = "输出疑似模板";
                            else if (tr.Output.Contains("未真正实现")) suspiciousReason = "未真正实现";
                            else suspiciousReason = "[检测] 标记";
                        }
                    }
                    break;

                case AgentEvent.FinalAnswer:
                    success = true;
                    break;

                case AgentEvent.Error e:
                    success = false;
                    errorSummary = e.Message;

                    // 【批次3·C 父失败回填】顶层 orchestrator 失败 → 回填本任务窗口内的
                    // Executor 子记录为失败（成功不反映真实达成——阶段 3 验证未通过）。
                    try
                    {
                        var n = await _metrics.BackfillFailedSubMetricsAsync(
                            userInput, startedAt,
                            "父任务未通过验证（回填：顶层 orchestrator 失败）", ct);
                        if (n > 0)
                            _logger.LogInformation("父失败回填 {Count} 条 Executor 子记录。", n);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("父失败回填失败（容忍）: {Msg}", ex.Message);
                    }
                    break;
            }

            yield return evt;
        }

        sw.Stop();

        var expectedWrites = fileCount;
        var retryCount = Math.Max(0, writeCount - expectedWrites);

        var metric = new TaskMetric
        {
            TaskId = taskId,
            SessionId = "",
            UserInput = userInput,
            Mode = "orchestrator",
            RoutingTier = "Orchestrated",
            ServiceId = "orchestrator",
            RoutingReason = "自动编排",
            Success = success,
            IsSuspicious = isSuspicious,
            // 【10-08 metrics 数据治理】E2E 冒烟用例由环境变量 LAW_E2E=1 标记（stats 剔除）
            // 【10-14 P3-6 归位】判定收走 ModelRouter.IsE2ERun 单一来源（原先三处同义判定之一）。
            IsE2E = Agent.Infrastructure.Routing.ModelRouter.IsE2ERun,
            RetryCount = retryCount,
            TotalMs = sw.ElapsedMilliseconds,
            StartedAt = startedAt,
            FinishedAt = DateTimeOffset.Now,
            // 【10-10 可疑成功治理·归因落库】suspicious 任务写 reason（与 AgentLoop 装饰器同构）
            ErrorSummary = isSuspicious && string.IsNullOrEmpty(errorSummary)
                ? suspiciousReason
                : errorSummary
        };

        try
        {
            await _metrics.RecordAsync(metric, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入 Orchestrator 指标失败。");
        }
    }
}