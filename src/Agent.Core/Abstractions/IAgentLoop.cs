// 引入模型类型
using Agent.Core.Models;

// 命名空间
namespace Agent.Core.Abstractions;

/// <summary>
/// Agent 主循环。这是整个系统的核心入口。
/// </summary>
public interface IAgentLoop
{
    /// <summary>
    /// 执行一次任务。返回异步事件流。
    /// 用 IAsyncEnumerable 而不是 Task，因为事件是流式产生的，
    /// 前端可以边收边展示，而不必等任务全部完成。
    /// </summary>
    /// <param name="request">任务输入</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>事件流</returns>
    IAsyncEnumerable<AgentEvent> RunAsync(AgentRequest request, CancellationToken ct);
}