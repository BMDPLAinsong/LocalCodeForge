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

    /// <summary>成员上限（防大工程清单膨胀撑爆上下文）；超出折叠计数。</summary>
    private const int MaxMembersPerType = 20;

    /// <summary>
    /// 类型定义行原文 → 修饰符前缀（如 "internal class TaxService" → "internal "；
    /// "public class Order" → "public "）。找不到类型关键字 → 空串。
    /// 【10-15 清单增强】确定性字符串定位，非解析。
    /// </summary>
    private static string TypeModifiers(string signature)
    {
        if (string.IsNullOrWhiteSpace(signature)) return "";
        // 定位类型关键字（class/interface/enum/record/struct）在行中的位置
        foreach (var kw in TypeKinds)
        {
            var i = signature.IndexOf(kw, StringComparison.Ordinal);
            if (i < 0) continue;
            // 要求关键字前是行首或空格（防 "classify" 之类前缀误配）
            if (i > 0 && signature[i - 1] != ' ' && signature[i - 1] != '\t') continue;
            // 只标注访问性：显式 public/internal 原样；无显式访问修饰符（含 partial/static/sealed 前缀）=
            // C# 顶层类型默认 internal —— 数据语义补全，防模型把无修饰符类当可公开引用（CS0051）
            var prefix = signature[..i].Trim();
            if (prefix.Contains("public")) return "public ";
            if (prefix.Contains("internal")) return "internal ";
            return "internal ";
        }
        return "";
    }

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
        var groups = new List<(string Kind, string Name, string File, string Signature, List<string> Members)>();

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
                    groups.Add((s.Kind, s.Symbol, rel, s.Signature, new List<string>()));
                }
                else if (IsMemberKind(s.Kind) && groups.Count > 0
                         && string.Equals(groups[^1].File, rel, StringComparison.OrdinalIgnoreCase))
                {
                    groups[^1].Members.Add(
                        string.IsNullOrWhiteSpace(s.Signature) ? s.Symbol : s.Signature);
                }
            }
        }

        if (groups.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("【已有类型清单（强约束·数据来源：工程实际代码）】以下类型已在工程中定义，其成员**仅限下列**（行内为定义原文签名）：");
        foreach (var g in groups.Take(maxTypes))
        {
            var mods = TypeModifiers(g.Signature);
            var kindLabel = string.IsNullOrEmpty(mods) ? g.Kind : mods + g.Kind;
            if (g.Members.Count == 0)
            {
                sb.AppendLine($"- {g.Name} ({kindLabel}, {g.File}): （无方法/属性）");
                continue;
            }
            var distinct = g.Members.Distinct(StringComparer.Ordinal).ToList();
            var shown = distinct.Take(MaxMembersPerType).ToList();
            var members = string.Join("; ", shown);
            if (distinct.Count > MaxMembersPerType)
                members += $"…（另有 {distinct.Count - MaxMembersPerType} 个成员）";
            sb.AppendLine($"- {g.Name} ({kindLabel}, {g.File}): {members}");
        }
        if (groups.Count > maxTypes)
            sb.AppendLine($"（另有 {groups.Count - maxTypes} 个类型未列出）");
        sb.AppendLine("使用这些类型时**只允许调用上列成员**（按签名传参/选用，注意访问修饰符：internal 类型不得出现在 public 签名中）；需要新成员/新类型时自行新增，不得臆造。");
        return sb.ToString().TrimEnd();
    }
}
