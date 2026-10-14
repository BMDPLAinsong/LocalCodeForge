// ============================================================
// RoutingTuningReporter.cs
// 调权状态单源（10-14 P2-1，架构审查任务8-A1 + 任务7-B3/B4）：
//   此前"三处调权"各写一份时代过滤查询 + 三判定：
//     · ModelRouter（运行时决策，内联）
//     · CLI stats「路由调权状态」小节
//     · Dashboard /api/tuning（端点内联副本）
//   10-11 修复 P2 层级错配（agent_pro 实为 Rule）只改了 CLI 与 ModelRouter，
//   Dashboard 漏改 → web 端 P2 永远 0 样本（任务8-A1 实锤活 bug）。
//   本类把查询口径 + 三判定 + agent_pro 层级归一：CLI/Dashboard 只做渲染
//   （文本 vs JSON），运行时决策复用查询口径、判定内联（决策不能等批量报告）。
// 无 LLM、纯指标聚合，可测。
// ============================================================

using Agent.Core.Models;
using Agent.Infrastructure.Metrics;

namespace Agent.Infrastructure.Routing;

/// <summary>单项调权判定结果（展示面：是否触发 / 样本数 / 成功率 / 原因）。
/// Reason 可空：RoutingIntelligence 的 out reason 在未触发时可能为 null（原 wire 语义即如此，序列化保持不变）。</summary>
public sealed record TuningVerdict(bool Triggered, int SampleCount, double SuccessRate, string? Reason);

/// <summary>调权状态报告：三处判定的汇总（样本统计 + 判定 + 原因）。</summary>
public sealed record RoutingTuningReport(
    ServiceStats Small,
    ServiceStats Large,
    ServiceStats Pro,
    TuningVerdict WriteDowngrade,
    TuningVerdict ReadUpgrade,
    TuningVerdict ComplexDrop);

/// <summary>
/// 调权判定单一来源：时代过滤的服务统计查询 + 三处调权判定。
/// 消费方：CLI stats 小节 / Dashboard /api/tuning（渲染差异仅文本 vs JSON）。
/// </summary>
public static class RoutingTuningReporter
{
    /// <summary>成功率（0 样本 → 0）。展示面共用口径。</summary>
    public static double RateOf(ServiceStats s) =>
        s.TotalCount > 0 ? (double)s.SuccessCount / s.TotalCount : 0;

    /// <summary>
    /// 时代过滤的服务统计：LawUnifiedEpoch 非空 → 只统计该时代之后样本（30B law-unified）。
    /// 与运行时路由（ModelRouter）同一口径——原三处各自实现同一段 epoch 分支。
    /// </summary>
    public static Task<ServiceStats> GetStatsAsync(
        IMetricsCollector metrics, AgentOptions options, string serviceId, string tier, int take, CancellationToken ct)
        => options.LawUnifiedEpoch is { } e
            ? metrics.GetServiceStatsSinceAsync(serviceId, tier, e, take, ct)
            : metrics.GetServiceStatsAsync(serviceId, tier, take, ct);

    /// <summary>
    /// 构建调权报告。三类样本各查一次（agent_pro 一次，供读升级与 Complex 降级共用——
    /// 原 CLI/Dashboard 各查两次同参数）；层级统一 Rule：agent_pro 的 P2 判定若按
    /// SmallModel 过滤永远 0 样本（10-11 错配），单源处根治，不可再分叉。
    /// </summary>
    public static async Task<RoutingTuningReport> BuildAsync(
        IMetricsCollector metrics, AgentOptions options, CancellationToken ct)
    {
        var rule = RoutingTier.Rule.ToString();
        var small = await GetStatsAsync(metrics, options, RoutingDecision.Small, rule, 10, ct);
        var large = await GetStatsAsync(metrics, options, RoutingDecision.Large, rule, 10, ct);
        var pro = await GetStatsAsync(metrics, options, RoutingDecision.AgentPro, rule, 10, ct);

        var downgrade = RoutingIntelligence.TrySuggestDowngrade(
            small, large, options.RoutingTuningMinSamples, options.RoutingDowngradeMinSuccessRate, out var downReason);
        var readUp = RoutingIntelligence.TrySuggestReadTier(
            pro, small, options.RoutingTuningMinSamples, options.RoutingReadUpgradeBadRate,
            options.RoutingReadUpgradeMinSuccessRate, out var readReason);
        var dropPro = RoutingIntelligence.TrySuggestDropAgentPro(
            pro, options.RoutingDropAgentProMinSamples, options.RoutingDropAgentProMinSuccessRate, out var dropReason);

        return new RoutingTuningReport(
            small, large, pro,
            new TuningVerdict(downgrade, small.TotalCount, RateOf(small), downReason),
            new TuningVerdict(readUp, pro.TotalCount, RateOf(pro), readReason),
            new TuningVerdict(dropPro, pro.TotalCount, RateOf(pro), dropReason));
    }
}