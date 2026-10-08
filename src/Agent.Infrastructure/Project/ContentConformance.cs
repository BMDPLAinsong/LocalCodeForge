// ============================================================
// ContentConformance.cs
// 增量修改任务的"内容符合性"检查（验证层补洞，2026-10-08）。
// 根因实证：BigService 加注释任务——模型定位/编辑链路全对（read_file
// offset 分页 → edit_file 精确匹配 → dotnet run 编译通过），但 new_string
// 生成错（x + 1500 写成 x + 15000 且漏加 // 已审查），验证层只看"编译+运行
// 成功" → 假成功 ok=1。缺的这道门：修改后的文件必须包含任务要求的内容。
// 设计原则（防打地鼠）：不堆提示词；只用任务输入里"用户显式要求写入的
// 文本"（引号/反引号包裹内容）做确定性包含检查；提取不到就跳过，绝不误伤。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Agent.Infrastructure.Quality;

namespace Agent.Infrastructure.Project;

/// <summary>
/// 内容符合性检查：从任务输入提取"必须出现在修改后文件中的需求 token"，
/// 并对本轮写过的文件做归一化包含检查。数据驱动、无 LLM、无副作用。
/// </summary>
public static class ContentConformance
{
    // 引号/反引号包裹内容：// 已审查、"XXX"、'YYY'、`add_field`。
    // 优先级：反引号 > 中文引号 > 双引号 > 单引号（同一内容避免重复提取）。
    private static readonly (string Pattern, bool SkipIfFile)[] QuotePatterns =
    {
        (@"`([^`\r\n]{1,120})`", false),
        (@"“([^“”\r\n]{1,120})”", false),
        (@"『([^『』\r\n]{1,120})』", false),
        (@"[“”]([^“”\r\n]{1,120})[“”]", false),
        (@"""([^""\r\n]{1,120})""", false),
        (@"'([^'\r\n]{1,120})'", false),
    };

    // 文件名/路径形态（含扩展名或路径分隔符）：是目标不是内容，过滤。
    private static readonly Regex FileLikeRe = new(
        @"[\w.\-\\/]+\.(?:cs|py|js|ts|json|csproj|sln|slnx|md|txt|xml|yaml|yml|java|go)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 注释形态：`// 已审查`、`# TODO`（用户说"加一行注释 // 已审查"时该字面量就是要写入的内容）。
    // `(?<!:)//` 排除 URL（https:// 是引用不是写入内容）；
    // `(?<![A-Za-z])#` 排除 "C# 写一个..."（# 前是字母 = C# 语言名，不是注释）——
    // 否则所有 C# 任务都会被提取出需求 token 全量误伤（自查必除）。
    private static readonly Regex CommentLikeRe = new(
        @"(?<!:)//\s*([^/\r\n]{2,60})|(?<![A-Za-z])#\s*([^#\r\n]{2,60})",
        RegexOptions.Compiled);

    /// <summary>
    /// 从任务输入提取需求内容 token（引号/反引号包裹、非文件名形态、长度 ≥2）。
    /// 提取不到返回空集——调用方应跳过内容检查（不误伤普通生成任务）。
    /// </summary>
    public static IReadOnlyList<string> ExtractRequirementTokens(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Array.Empty<string>();

        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (pattern, _) in QuotePatterns)
        {
            foreach (Match m in Regex.Matches(input, pattern))
            {
                var raw = m.Groups[1].Value.Trim();
                if (raw.Length < 2 || raw.Length > 120)
                    continue;
                // 文件名/路径形态（如 "BigService.cs"）是定位目标不是写入内容
                if (FileLikeRe.IsMatch(raw))
                    continue;
                var norm = OutputVerifier.NormalizeForMatch(raw);
                if (norm.Length < 2)
                    continue;
                if (seen.Add(norm))
                    tokens.Add(norm);
            }
        }

        // 注释形态（`// 已审查` / `# TODO`）：用户说"加一行注释 // 已审查"时
        // 该字面量就是要写入文件的内容（BigService 实证样本，无引号包裹）。
        foreach (Match m in CommentLikeRe.Matches(input))
        {
            var raw = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
            if (raw.Length < 2 || raw.Length > 120)
                continue;
            var norm = OutputVerifier.NormalizeForMatch(raw);
            if (norm.Length < 2)
                continue;
            if (seen.Add(norm))
                tokens.Add(norm);
        }

        return tokens;
    }

    /// <summary>
    /// 检查文件内容（归一化后）是否包含需求 token（归一化后）。
    /// 文件不存在 / 读取失败按"不含"处理（调用方决定语义）。
    /// </summary>
    public static bool FileContainsTokens(string filePath, IReadOnlyList<string> normalizedTokens)
    {
        if (string.IsNullOrWhiteSpace(filePath) || normalizedTokens.Count == 0)
            return false;

        string content;
        try
        {
            content = File.ReadAllText(filePath);
        }
        catch (Exception)
        {
            return false;
        }

        var contentNorm = OutputVerifier.NormalizeForMatch(content);
        foreach (var tok in normalizedTokens)
        {
            // 归一化后的 token 已是空白折叠形态；文件内容同样折叠 → 包含即中
            if (contentNorm.Contains(tok))
                return true;
        }
        return false;
    }
}
