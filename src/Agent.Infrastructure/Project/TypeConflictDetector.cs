using System.Text.RegularExpressions;

namespace Agent.Infrastructure.Project;

/// <summary>
/// 【P-D】写前类型冲突检测（工程化、非提示词）：
/// 30B 模型对"增量修改已有文件"纪律弱——ShippingService 任务 3/3 稳定失败，
/// 每次把已有 `class Order` 重复定义进 OrderService.cs（全量重写恶习）→ CS0101。
/// 修复手段：写工具在写工程内文件（新建或已存在）前，确定性检测新内容
/// 是否定义了工程其他文件已有的类型名（class/interface/enum/struct/record）；
/// 命中 → 拒绝写入并返回明确错误，让模型第 1 次修复就知道不该重复定义。
/// 纯正则 + 文件扫描，毫秒级，无模型调用。
/// </summary>
public static class TypeConflictDetector
{
    private static readonly Regex TypeDefRegex = new(
        @"\b(class|interface|enum|struct|record)\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    // 剥离注释与字符串字面量，避免注释/字符串里的 "class X" 误报为类型定义。
    // 轻量词法（非完整 C# 解析）：块注释（跨行）→ 行注释 → 双引号字符串（含转义）。
    private static readonly Regex BlockCommentRegex = new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex LineCommentRegex = new(@"//[^\r\n]*", RegexOptions.Compiled);
    private static readonly Regex StringLiteralRegex = new(@"""(?:[^""\\]|\\.)*""", RegexOptions.Compiled);

    /// <summary>提取文本中的类型定义名（去重；注释/字符串中的类名不计）。</summary>
    public static List<string> ExtractTypeNames(string content)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(content)) return result;
        var cleaned = BlockCommentRegex.Replace(content, "");
        cleaned = LineCommentRegex.Replace(cleaned, "");
        cleaned = StringLiteralRegex.Replace(cleaned, "");
        foreach (Match m in TypeDefRegex.Matches(cleaned))
        {
            var name = m.Groups[2].Value;
            if (!result.Contains(name, StringComparer.OrdinalIgnoreCase))
                result.Add(name);
        }
        return result;
    }

    /// <summary>
    /// 检测写 targetFile 的 newContent 是否定义了工程【其他文件】已有的类型名。
    /// 目标文件自身已有的类型（partial 场景）不算冲突（扫描时排除目标文件）。
    /// 返回冲突描述列表（"类型名（已存在于 文件）"）；无冲突返回空列表。
    /// </summary>
    public static List<string> DetectDuplicateTypes(string projectRoot, string targetFile, string newContent)
    {
        var conflicts = new List<string>();
        if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(targetFile)) return conflicts;
        var newTypes = ExtractTypeNames(newContent);
        if (newTypes.Count == 0) return conflicts;

        // 工程其他 .cs 文件的类型名 → 所在文件（首个命中即可）
        var existingByType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var targetFull = Path.GetFullPath(targetFile);
            foreach (var f in Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories))
            {
                string fFull;
                try { fFull = Path.GetFullPath(f); } catch { continue; }
                if (string.Equals(fFull, targetFull, StringComparison.OrdinalIgnoreCase)) continue;
                string content;
                try { content = File.ReadAllText(f); } catch { continue; }
                foreach (var tn in ExtractTypeNames(content))
                {
                    if (!existingByType.ContainsKey(tn))
                        existingByType[tn] = Path.GetRelativePath(projectRoot, f).Replace('\\', '/');
                }
            }
        }
        catch { }

        foreach (var t in newTypes)
        {
            if (existingByType.TryGetValue(t, out var file))
                conflicts.Add($"{t}（已存在于 {file}）");
        }
        return conflicts;
    }
}
