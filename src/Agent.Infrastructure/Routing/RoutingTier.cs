// 命名空间
namespace Agent.Infrastructure.Routing;

/// <summary>
/// 路由层级。指示本次决策由三层漏斗中的哪一层做出。
/// 用于日志分析和成本统计。
/// </summary>
public enum RoutingTier
{
    /// <summary>Layer 1：规则拦截命中。零 LLM 调用，最快最省。</summary>
    Rule,

    /// <summary>Layer 2：本地小模型意图分类命中。</summary>
    SmallModel,

    /// <summary>Layer 3：大模型兜底。规则和分类都没定下来时的保底选择。</summary>
    LargeModel
}