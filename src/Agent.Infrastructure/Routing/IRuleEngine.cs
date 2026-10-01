// 命名空间
namespace Agent.Infrastructure.Routing;

/// <summary>
/// 规则引擎接口。Layer 1 的实现契约。
/// </summary>
public interface IRuleEngine
{
    /// <summary>
    /// 评估当前上下文是否命中某条规则。
    /// </summary>
    /// <param name="context">路由上下文</param>
    /// <returns>命中返回决策；未命中返回 null</returns>
    RoutingDecision? Evaluate(RoutingContext context);
}