// 命名空间
namespace Agent.Infrastructure.Routing;

/// <summary>
/// 模型路由器接口。决定每次 LLM 调用走哪个模型。
/// </summary>
public interface IModelRouter
{
    /// <summary>
    /// 根据上下文做出路由决策。
    /// </summary>
    /// <param name="context">路由上下文</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>路由决策</returns>
    Task<RoutingDecision> RouteAsync(RoutingContext context, CancellationToken ct);
}