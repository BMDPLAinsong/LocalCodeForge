// ============================================================
// MemoryPromptBuilder.cs
// 历史记忆注入格式化（工程感知 P3 补足）：
// 把检索命中的记忆分组成"历史失败教训（⚠ 警示）"与"历史参考"两块注入，
// 失败教训单列且带警示标记——让模型在同类任务开始就看到此前失败原因，
// 避免重蹈覆辙（少走弯路）。确定性静态方法，无模型调用。
// ============================================================

using System.Text;

namespace Agent.Infrastructure.Memory;

/// <summary>
/// 历史记忆注入块构建。
/// </summary>
public static class MemoryPromptBuilder
{
    /// <summary>
    /// 构建注入块。无命中返回 null。
    /// 失败记忆（kind=task 且 content 以"任务失败"开头）单列警示组，
    /// 同一失败主题命中 ≥2 次时附失败计数强化信号。
    /// </summary>
    public static string? Build(IReadOnlyList<AgentMemoryStore.MemoryHit> hits)
    {
        if (hits is null || hits.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("## 历史记忆（跨任务，来自此前任务）");

        var failures = hits
            .Where(h => h.Kind == "task" && h.Content.StartsWith("任务失败", StringComparison.Ordinal))
            .ToList();
        var references = hits
            .Where(h => !(h.Kind == "task" && h.Content.StartsWith("任务失败", StringComparison.Ordinal)))
            .ToList();

        if (failures.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("⚠ 历史失败教训（请参考并避免重蹈覆辙）:");
            foreach (var f in failures)
                sb.AppendLine($"- [task] {f.Topic}：{f.Content}");
            if (failures.Count >= 2)
                sb.AppendLine($"（同类失败已记录 {failures.Count} 次）");
        }

        if (references.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("历史参考:");
            foreach (var r in references)
                sb.AppendLine($"- [{r.Kind}] {r.Topic}：{r.Content}");
        }

        return sb.ToString();
    }
}
