// 命名空间
namespace Agent.Infrastructure.Routing;

/// <summary>
/// 路由决策结果。
/// </summary>
/// <param name="ServiceId">目标服务：classifier / small / large</param>
/// <param name="Tier">命中的层级</param>
/// <param name="Reason">决策原因</param>
/// <param name="Intent">意图类型（规则命中时为 null）</param>
public sealed record RoutingDecision(
    string ServiceId,
    RoutingTier Tier,
    string Reason,
    IntentType? Intent = null)
{
    /// <summary>意图分类器服务 ID。</summary>
    public const string Classifier = "classifier";

    /// <summary>本地 7B 服务 ID。</summary>
    public const string Small = "small";

    /// <summary>云端大模型服务 ID。</summary>
    public const string Large = "large";

    /// <summary>Agent 专用服务 ID（Qwen3.5:4b）。</summary>
    public const string AgentPro = "agent_pro";
}