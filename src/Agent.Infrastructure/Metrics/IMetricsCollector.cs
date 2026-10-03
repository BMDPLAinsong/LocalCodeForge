// ============================================================
// IMetricsCollector.cs
// 任务指标采集接口。
// ============================================================

using Agent.Core.Models;

namespace Agent.Infrastructure.Metrics;

/// <summary>
/// 任务指标采集器。
/// </summary>
public interface IMetricsCollector
{
    /// <summary>记录一次任务。</summary>
    Task RecordAsync(TaskMetric metric, CancellationToken ct);

    /// <summary>查询最近 N 条记录。</summary>
    Task<IReadOnlyList<TaskMetric>> GetRecentAsync(int take, CancellationToken ct);

    /// <summary>查询最近的失败记录。</summary>
    Task<IReadOnlyList<TaskMetric>> GetRecentFailuresAsync(int take, CancellationToken ct);

    /// <summary>获取总体统计。</summary>
    Task<MetricsSummary> GetSummaryAsync(
        DateTime? since,
        CancellationToken ct);

    /// <summary>数据闭环：按服务+路由层级聚合最近 N 条的表现（成功率/平均耗时）。</summary>
    Task<Agent.Infrastructure.Routing.ServiceStats> GetServiceStatsAsync(
        string serviceId, string routingTier, int take, CancellationToken ct);

    /// <summary>
    /// 【时代隔离】按时代边界拆分某服务的表现（当前时代 = started_at ≥ epoch，历史时代 = 之前）。
    /// 用于如实反映模型切换后的能力（如 agent_pro：30B 时代 vs 4B 历史，避免被旧样本污染）。
    /// </summary>
    Task<(Agent.Infrastructure.Routing.ServiceStats Current, Agent.Infrastructure.Routing.ServiceStats Legacy)> GetEpochBreakdownAsync(
        string serviceId, DateTimeOffset epoch, CancellationToken ct);
}

/// <summary>
/// 指标汇总。
/// </summary>
public sealed record MetricsSummary(
    int TotalCount,
    int SuccessCount,
    int FailureCount,
    int SuspiciousCount,        // ← 新增
    double SuccessRate,
    double AvgRetryCount,
    double AvgTotalMs,
    double AvgPlannerMs,
    double AvgExecutorMs,
    double AvgValidatorMs);