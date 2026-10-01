// ============================================================
// AgentLoopMetricsDecorator.cs
// 用装饰器模式给 AgentLoop 加指标采集。
// ============================================================

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Agent.Core.Abstractions;
using Agent.Core.Models;
using Microsoft.Extensions.Logging;

namespace Agent.Infrastructure.Metrics;

/// <summary>
/// AgentLoop 的指标装饰器。
/// </summary>
public sealed class AgentLoopMetricsDecorator : IAgentLoop
{
    private readonly IAgentLoop _inner;
    private readonly IMetricsCollector _metrics;
    private readonly ILogger<AgentLoopMetricsDecorator> _logger;

    public AgentLoopMetricsDecorator(
        IAgentLoop inner,
        IMetricsCollector metrics,
        ILogger<AgentLoopMetricsDecorator> logger)
    {
        _inner = inner;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>执行一次任务，同时采集指标。</summary>
    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AgentRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var taskId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.Now;
        var sw = Stopwatch.StartNew();

        // 待填充的指标
        var success = false;
        var errorSummary = "";
        var routingTier = "";
        var serviceId = "";
        var routingReason = "";

        // 修复次数统计（serviceId 变化 = 走了一次 recovery 升级）
        var routingChangeCount = 0;
        var lastServiceId = "";

        // 可疑标记
        var isSuspicious = false;

        await foreach (var evt in _inner.RunAsync(request, ct))
        {
            switch (evt)
            {
                case AgentEvent.Routing rt:
                    // 【关键修复】用 serviceId 变化判断，而不是 tier
                    // 因为 tier 可能一直是 Rule，只是 serviceId 从 small 变到 agent_pro
                    if (!string.IsNullOrEmpty(lastServiceId) && lastServiceId != rt.ServiceId)
                    {
                        routingChangeCount++;
                    }
                    lastServiceId = rt.ServiceId;

                    routingTier = rt.Tier;
                    serviceId = rt.ServiceId;
                    routingReason = rt.Reason;
                    break;

                case AgentEvent.ToolResultEvent tr:
                    // 检测可疑信号：ToolResultEvent 里的 Output 含 [检测] 标记
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

        var metric = new TaskMetric
        {
            TaskId = taskId,
            SessionId = request.SessionId,
            UserInput = request.Input,
            Mode = "agent",
            RoutingTier = routingTier,
            ServiceId = serviceId,
            RoutingReason = routingReason,
            Success = success,
            IsSuspicious = isSuspicious,
            RetryCount = routingChangeCount,
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
            _logger.LogWarning(ex, "写入 AgentLoop 指标失败。");
        }
    }
}