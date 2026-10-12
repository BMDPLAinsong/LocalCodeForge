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

    /// <summary>
    /// 【10-13 批次1】生命周期清理：删除早于 N 天前的轮次（启动时调用，防 turns 表无限增长）。
    /// </summary>
    /// <param name="days">保留天数</param>
    /// <returns>删除条数（失败返回 0）</returns>
    int PruneOlderThan(int days);
}