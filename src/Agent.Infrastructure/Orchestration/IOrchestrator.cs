// ============================================================
// IOrchestrator.cs
// 编排器接口。串联 Planner → Executor → Validator。
// ============================================================

using Agent.Core.Models;

namespace Agent.Infrastructure.Orchestration;

/// <summary>
/// 编排器。三阶段执行复杂任务。
/// </summary>
public interface IOrchestrator
{
    /// <summary>
    /// 执行一次编排任务。返回事件流。
    /// </summary>
    /// <param name="userInput">用户需求</param>
    /// <param name="workspaceRoot">工作区根目录</param>
    /// <param name="ct">取消令牌</param>
    IAsyncEnumerable<AgentEvent> RunAsync(
        string userInput,
        string workspaceRoot,
        CancellationToken ct);
}