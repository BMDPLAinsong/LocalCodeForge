// ============================================================
// TaskMemoryExtractor.cs
// 任务归档记忆（工程感知 P3 补足）：
// 任务完成/失败后由 CLI 层确定性提炼 (topic, content) 写入 AgentMemoryStore
// （kind=task）——补足"模型不自发 remember 时也有记忆积累"。
// 纯静态、无模型调用：输入截断 + 状态 + 错误信息 → 归档内容。
// ============================================================

namespace Agent.Infrastructure.Memory;

/// <summary>
/// 任务归档提炼（确定性：输入摘要 + 结果状态 + 失败教训）。
/// </summary>
public static class TaskMemoryExtractor
{
    /// <summary>归档 topic 最大长度。</summary>
    public const int TopicMax = 40;

    /// <summary>归档 content 最大长度。</summary>
    public const int ContentMax = 200;

    /// <summary>
    /// 提炼归档条目。
    /// </summary>
    /// <param name="input">任务输入（原文）</param>
    /// <param name="success">是否成功（收到 FinalAnswer）</param>
    /// <param name="errors">任务过程错误消息列表（失败教训来源）</param>
    /// <param name="mode">执行模式（agent / orchestrator）</param>
    /// <param name="finalAnswer">最终交付文本（成功时）</param>
    /// <param name="writtenFileCount">已写文件数（10-13 任务事实：供后续任务知道"上次动过哪些文件"）</param>
    public static (string Topic, string Content) Extract(
        string input, bool success, IReadOnlyList<string> errors, string mode, string? finalAnswer = null, int writtenFileCount = 0)
    {
        // topic：输入单行压缩（去换行）后截断
        var oneLine = (input ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        var topic = oneLine.Length <= TopicMax ? oneLine : oneLine[..TopicMax];

        if (success)
        {
            var answer = string.IsNullOrWhiteSpace(finalAnswer)
                ? "（无最终交付文本）"
                : finalAnswer.Replace("\r", " ").Replace("\n", " ").Trim();
            var content = $"任务完成（{mode}）：{answer}";
            if (writtenFileCount > 0)
                content += $"｜写{writtenFileCount}文件";
            return (topic, Truncate(content, ContentMax));
        }

        var reason = errors is { Count: > 0 }
            ? string.Join("；", errors.Select(e => e.Replace("\r", " ").Replace("\n", " ").Trim()))
            : "未知错误";
        var failContent = $"任务失败（{mode}）：{reason}";
        if (writtenFileCount > 0)
            failContent += $"｜写{writtenFileCount}文件";
        return (topic, Truncate(failContent, ContentMax));
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max];
}
