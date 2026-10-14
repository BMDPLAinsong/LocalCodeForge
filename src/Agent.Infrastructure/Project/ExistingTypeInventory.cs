// ============================================================
// Project/ExistingTypeInventory.cs
// 【10-14 C3 工程增量类型清单】把"工程已有类型 + 其成员"压成一份紧凑清单，
// 供 Executor 在生成/修复前作为**强约束数据**注入（数据驱动，非提示词规则）。
//
// 背景（A/B 实测根因）：工程增量任务中 30B 模型会凭空引用不存在的成员/类型
// （实测 ShippingService 任务：幻觉 Order.Items / OrderItem）——即使 Orchestrator
// 事后注入了 Order.cs 完整定义（批次3·B）仍幻觉。故改为**事前**把"可用成员全集"
// 以确定性数据形式给出，让"臆造"没有信息基础。
//
// 提取复用 SymbolIndexer.BuildContent（单一提取源，零新增正则）；本类只做
// "按行序把成员归组到所属类型"的确定性后处理。纯函数、可测、无 LLM。
// ============================================================
using System.Text;

namespace Agent.Infrastructure.Project;

/// <summary>已有类型清单构建（确定性；提取源 = SymbolIndexer）。</summary>
public static class ExistingTypeInventory
{
    private static readonly HashSet<string> TypeKinds = new(StringComparer.Ordinal)
    {
        "class", "interface", "enum", "record", "struct"
    };

    private static bool IsMemberKind(string kind) =>
        kind is "method" or "property" or "function";

    /// <summary>
    /// 从 (相对路径, 内容) 集合构建紧凑清单。无类型 → 返回 null（调用方不注入）。
    /// excludeRel：排除目标文件（正在生成/修复的那个文件不算"已有"）。
    /// maxTypes：清单上限（防大工程撑爆上下文）。
    /// </summary>
    public static string? Build(
        IEnumerable<(string Rel, string Content)> files,
        string? excludeRel = null,
        int maxTypes = 30)
    {
        var groups = new List<(string Kind, string Name, string File, List<string> Members)>();

        foreach (var (rel, content) in files)
        {
            if (excludeRel is not null &&
                string.Equals(rel, excludeRel, StringComparison.OrdinalIgnoreCase))
                continue;

            // symbols 按行号升序：类型开启新组，成员归到"同文件最近一个类型"（跨文件不误挂）
            foreach (var s in SymbolIndexer.BuildContent(content, rel))
            {
                if (TypeKinds.Contains(s.Kind))
                {
                    groups.Add((s.Kind, s.Symbol, rel, new List<string>()));
                }
                else if (IsMemberKind(s.Kind) && groups.Count > 0
                         && string.Equals(groups[^1].File, rel, StringComparison.OrdinalIgnoreCase))
                {
                    groups[^1].Members.Add(s.Symbol);
                }
            }
        }

        if (groups.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("【已有类型清单（强约束·数据来源：工程实际代码）】以下类型已在工程中定义，其成员**仅限下列**：");
        foreach (var g in groups.Take(maxTypes))
        {
            var members = g.Members.Count > 0
                ? string.Join(", ", g.Members.Distinct(StringComparer.Ordinal))
                : "（无方法/属性）";
            sb.AppendLine($"- {g.Name} ({g.Kind}, {g.File}): {members}");
        }
        if (groups.Count > maxTypes)
            sb.AppendLine($"（另有 {groups.Count - maxTypes} 个类型未列出）");
        sb.AppendLine("使用这些类型时**只允许调用上列成员**；需要新成员/新类型时自行新增，不得臆造。");
        return sb.ToString().TrimEnd();
    }
}
