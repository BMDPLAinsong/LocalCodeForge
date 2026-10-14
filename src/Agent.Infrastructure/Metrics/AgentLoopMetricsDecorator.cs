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
        // 【10-10 可疑成功治理·归因落库】记录首个可疑信号类别，供 stats/SQL 归因
        // （此前 suspicious 成功任务 error_summary 恒空，无法区分 [边缘成功]/[检测]模板/未真正实现）。
        var suspiciousReason = "";

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
                    // 检测可疑信号：ToolResultEvent 里的 Output 含 [检测] / 边缘成功 标记
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
                    // 【批次1·D】可疑信号扩展：模板未实现 / 完成门目标不符 → 标可疑（需求未达成）
                    if (e.Message.Contains("未真正实现") ||
                        e.Message.Contains("与用户指定不符"))
                    {
                        isSuspicious = true;
                        if (string.IsNullOrEmpty(suspiciousReason))
                        {
                            suspiciousReason = e.Message.Contains("未真正实现") ? "未真正实现" : "完成门目标不符";
                        }
                    }
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
            // 【10-08 metrics 数据治理】E2E 冒烟用例由环境变量 LAW_E2E=1 标记（stats 剔除）
            // 【10-14 P3-6 归位】判定收走 ModelRouter.IsE2ERun 单一来源（原在此处复刻env 读取，
            // 与 ModelRouter/OrchestratorMetricsDecorator 三处同义判定→改一处漏两处的漂移向量）。
            IsE2E = Agent.Infrastructure.Routing.ModelRouter.IsE2ERun,
            RetryCount = routingChangeCount,
            TotalMs = sw.ElapsedMilliseconds,
            StartedAt = startedAt,
            FinishedAt = DateTimeOffset.Now,
            // 【10-10 可疑成功治理·归因落库】suspicious 任务写 reason 到 ErrorSummary
            // （成功任务也有 summary，供 stats/SQL 按类别归因——此前恒空无法区分来源）。
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
            _logger.LogWarning(ex, "写入 AgentLoop 指标失败。");
        }
    }
}