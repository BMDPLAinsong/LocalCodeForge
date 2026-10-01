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
                         tr.Output.Contains("疑似模板") ||
                         tr.Output.Contains("未真正实现")))
                    {
                        isSuspicious = true;
                    }
                    break;

                case AgentEvent.FinalAnswer:
                    success = true;
                    break;

                case AgentEvent.Error e:
                    success = false;
                    errorSummary = e.Message;
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
            RetryCount = retryCount,
            TotalMs = sw.ElapsedMilliseconds,
            StartedAt = startedAt,
            FinishedAt = DateTimeOffset.Now,
            ErrorSummary = errorSummary
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