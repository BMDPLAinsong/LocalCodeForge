// ============================================================
// RoutingIntelligence.cs
// 路由反馈闭环（数据闭环 · 方案 C）：
// 基于历史任务指标（task_metrics）聚合，回流自动调权——
// 写任务 L0.5 直连 large 前，若历史数据表明同场景 small 成功率高
// 且显著省时，则降级 small（失败由升级链兜底，不劣化）。
// 纯静态确定性逻辑，可测；不依赖提示词。
// ============================================================

namespace Agent.Infrastructure.Routing;

/// <summary>服务历史表现统计（按 service_id + routing_tier 聚合）。</summary>
public sealed record ServiceStats(int TotalCount, int SuccessCount, double AvgTotalMs);

/// <summary>
/// 数据闭环调权判定。
/// </summary>
public static class RoutingIntelligence
{
    /// <summary>
    /// 是否建议把候选服务降级为默认（如写任务 large → small）。
    /// 保守三条件，全部满足才降级：
    ///  1. 候选服务历史样本数 ≥ minSamples（数据不足不冒险）；
    ///  2. 候选服务历史成功率 ≥ minSuccessRate；
    ///  3. 候选服务平均耗时 < 当前服务平均耗时（或当前无历史）——省时才有意义。
    /// </summary>
    public static bool TrySuggestDowngrade(
        ServiceStats candidate,
        ServiceStats current,
        int minSamples,
        double minSuccessRate,
        out string? reason)
    {
        reason = null;

        if (candidate.TotalCount < minSamples)
        {
            reason = $"样本不足（{candidate.TotalCount}/{minSamples}），保持默认";
            return false;
        }

        var rate = (double)candidate.SuccessCount / candidate.TotalCount;
        if (rate < minSuccessRate)
        {
            reason = $"成功率 {rate:P0} 低于阈值 {minSuccessRate:P0}，保持默认";
            return false;
        }

        if (current.TotalCount > 0 && candidate.AvgTotalMs >= current.AvgTotalMs)
        {
            reason = $"平均耗时 {candidate.AvgTotalMs / 1000:N0}s 不低于当前 {current.AvgTotalMs / 1000:N0}s，保持默认";
            return false;
        }

        reason = $"历史 {candidate.TotalCount} 条成功率 {rate:P0}，平均 {candidate.AvgTotalMs / 1000:N0}s"
               + (current.TotalCount > 0 ? $" < large {current.AvgTotalMs / 1000:N0}s" : "（large 无历史）");
        return true;
    }
}
