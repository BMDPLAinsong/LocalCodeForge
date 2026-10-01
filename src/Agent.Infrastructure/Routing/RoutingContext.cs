// 命名空间
namespace Agent.Infrastructure.Routing;

/// <summary>
/// 路由的输入上下文。包含判断"该用哪个模型"需要的全部信息。
/// </summary>
/// <param name="UserInput">用户本轮的原始输入</param>
/// <param name="Step">当前是 ReAct 循环的第几步（从 0 开始）</param>
/// <param name="LastToolName">上一步调用的工具名，可为空</param>
/// <param name="LastStepFailed">上一步是否失败（工具执行错误）</param>
/// <param name="HistoryTurns">已有的对话轮数</param>
public sealed record RoutingContext(
    string UserInput,
    int Step,
    string? LastToolName,
    bool LastStepFailed,
    int HistoryTurns);