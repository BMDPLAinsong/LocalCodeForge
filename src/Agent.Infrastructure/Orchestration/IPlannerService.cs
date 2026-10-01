// ============================================================
// IPlannerService.cs
// 规划器服务接口。
// ============================================================

using Agent.Core.Models;

namespace Agent.Infrastructure.Orchestration;

/// <summary>
/// 规划器。把用户需求拆解成结构化 Plan。
/// </summary>
public interface IPlannerService
{
    /// <summary>
    /// 根据用户输入生成实现计划。
    /// </summary>
    /// <param name="userInput">用户原始需求</param>
    /// <param name="workspaceRoot">工作区根目录</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>解析成功的 Plan；失败返回 null</returns>
    Task<Plan?> GeneratePlanAsync(
        string userInput,
        string workspaceRoot,
        CancellationToken ct);
}