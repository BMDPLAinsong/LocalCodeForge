// 引入模型类型
using Agent.Core.Models;

// 命名空间
namespace Agent.Core.Abstractions;

/// <summary>
/// 会话存储。负责持久化对话历史。
/// </summary>
public interface ISessionStore
{
    /// <summary>
    /// 追加一轮对话到指定会话。
    /// </summary>
    /// <param name="sessionId">会话 ID</param>
    /// <param name="turn">对话轮次</param>
    /// <param name="ct">取消令牌</param>
    Task AppendTurnAsync(string sessionId, ChatTurn turn, CancellationToken ct);

    /// <summary>
    /// 读取指定会话最近 N 轮对话。
    /// </summary>
    /// <param name="sessionId">会话 ID</param>
    /// <param name="take">最多取多少轮</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>按时间正序排列的历史</returns>
    Task<IReadOnlyList<ChatTurn>> GetHistoryAsync(string sessionId, int take, CancellationToken ct);

    /// <summary>
    /// 清空指定会话的全部历史。
    /// </summary>
    /// <param name="sessionId">会话 ID</param>
    /// <param name="ct">取消令牌</param>
    Task ClearAsync(string sessionId, CancellationToken ct);
}