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

        var existingByType = GetExistingTypes(projectRoot, targetFile);
        foreach (var t in newTypes)
        {
            if (existingByType.TryGetValue(t, out var file))
                conflicts.Add($"{t}（已存在于 {file}）");
        }
        return conflicts;
    }

    /// <summary>
    /// 工程【其他文件】（排除 targetFile）已有类型名 → 所在相对文件路径。
    /// 供拦截错误信息拼接"已有类型清单"，让模型被拒后立刻知道能/不能定义什么。
    /// </summary>
    public static Dictionary<string, string> GetExistingTypes(string projectRoot, string targetFile)
    {
        var existingByType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(targetFile)) return existingByType;
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
        return existingByType;
    }

    /// <summary>
    /// 【10-08 B 扩展】检测新内容【自身】重复定义类型名（同一内容中 class X 出现 ≥2 次）。
    /// 与 DetectDuplicateTypes（跨文件）互补：模型可能在单份新内容里写两个同名类
    /// （10-08 复测实证：LetterCaseChecker.cs 900 字符内 2×class LetterCaseChecker → CS0101）。
    /// 纯内容检测，不依赖工程扫描——沙箱模式同样生效。
    /// 返回重复类型名列表（"名（定义 N 次）"，按次数降序）；无重复返回空列表。
    /// </summary>
    public static List<string> DetectSelfDuplicateTypes(string content)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(content)) return result;
        var cleaned = BlockCommentRegex.Replace(content, "");
        cleaned = LineCommentRegex.Replace(cleaned, "");
        cleaned = StringLiteralRegex.Replace(cleaned, "");
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in TypeDefRegex.Matches(cleaned))
        {
            var name = m.Groups[2].Value;
            counts[name] = counts.TryGetValue(name, out var c) ? c + 1 : 1;
        }
        foreach (var kv in counts.Where(kv => kv.Value >= 2).OrderByDescending(kv => kv.Value))
            result.Add($"{kv.Key}（定义 {kv.Value} 次）");
        return result;
    }

    /// <summary>
    /// 生成"已有类型清单"文案（数据驱动引导）：
    /// "工程已有类型：Order(Order.cs), OrderService(OrderService.cs), …（共 N 个）。请勿重复定义以上类型；本文件只应定义新类型。"
    /// 超长工程最多列前 30 个 + "等 N 个"。
    /// targetRelative/targetExists：目标文件状态感知的动作指引（10-04 增强，§14-3t）——
    /// 让被 P-D 拦截的模型知道"下一步该做什么"：
    ///   目标已存在 → 指引 read_file 查看 → edit_file 局部修改（不要整体重写）；
    ///   目标是新文件 → 指引只定义新类型、已有类型在其所在文件内 edit 修改。
    /// </summary>
    public static string BuildExistingTypesHint(Dictionary<string, string> existingByType,
        string? targetRelative = null, bool targetExists = false)
    {
        if (existingByType.Count == 0) return "";
        var items = existingByType
            .OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{kv.Key}({kv.Value})")
            .ToList();
        const int maxShown = 30;
        var shown = items.Count > maxShown ? items.Take(maxShown).ToList() : items;
        var suffix = items.Count > maxShown ? $" 等共 {items.Count} 个" : $"（共 {items.Count} 个）";
        var msg = $"工程已有类型：{string.Join(", ", shown)}{suffix}。请勿重复定义以上类型；本文件只应定义清单之外的新类型（不要整体重写已有文件，如需修改请用 edit_file 局部编辑）。";
        if (!string.IsNullOrEmpty(targetRelative))
        {
            if (targetExists)
            {
                msg += $"\n注意：{targetRelative} 已存在于工程。请先 read_file {targetRelative} 查看当前内容，" +
                       "再用 edit_file 局部修改（不要 write_file 整体重写该文件）。";
            }
            else
            {
                msg += $"\n注意：{targetRelative} 是新建文件，本文件只应定义清单之外的新类型；" +
                       "若需修改已有类型，请在其所在文件内用 edit_file 局部编辑。";
            }
        }
        return msg;
    }
}
