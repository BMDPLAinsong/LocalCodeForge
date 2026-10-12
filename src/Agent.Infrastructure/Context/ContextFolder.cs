// ============================================================
// Context / ContextFolder.cs
// 上下文折叠摘要（10-13 分层上下文预算 · 阶段1：任务内防失忆）。
// 确定性折叠（零模型调用，可测）：工具结果超阈值且累计注入超预算时，
// 把大段输出折叠为"结构定义 + 头尾"摘要——语义保留优于纯截断；
// 完整内容已在 TaskEventStore 全量持久化（cold 存储，可回放），
// 折叠只影响 hot 上下文的占用，不丢任何信息。
// ============================================================

using System.Text;
using System.Text.RegularExpressions;

namespace Agent.Infrastructure.Context;

/// <summary>
/// 上下文折叠（确定性、纯函数、可单测）。
/// </summary>
public static class ContextFolder
{
    /// <summary>折叠下限：≤ 该长度不折叠（短内容直接返回，避免无意义开销）。</summary>
    public const int FoldMinBytes = 8000;

    /// <summary>视为代码的文件扩展名（代码折叠提取结构定义行，文本折叠取头尾）。</summary>
    private static readonly HashSet<string> CodeExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".py", ".ts", ".tsx", ".js", ".jsx", ".java", ".go", ".rs",
        ".c", ".cpp", ".h", ".hpp", ".html", ".htm", ".css", ".json",
        ".sql", ".sh", ".ps1", ".xml", ".yaml", ".yml", ".ini", ".config"
    };

    /// <summary>结构定义行（类/接口/记录/结构/枚举/函数/方法签名）。</summary>
    private static readonly Regex DefRe = new(
        @"^\s*(?:public|private|protected|internal|static|sealed|abstract|partial|async|readonly|virtual|override)\s*(?:class|interface|record|struct|enum|def|function)\b|^\s*(?:class|interface|record|struct|enum|def|function)\b",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// 折叠工具结果输出。
    /// </summary>
    /// <param name="output">工具原始输出。</param>
    /// <param name="fileNameHint">文件名提示（决定代码/文本折叠策略；null/未知 → 文本）。</param>
    /// <returns>折叠后文本；短内容原样返回。</returns>
    public static string FoldToolResult(string output, string? fileNameHint)
    {
        if (string.IsNullOrEmpty(output) || output.Length <= FoldMinBytes)
            return output;

        var lines = output.Split('\n');
        var isCode = !string.IsNullOrEmpty(fileNameHint) && CodeExts.Contains(Path.GetExtension(fileNameHint));

        var sb = new StringBuilder();
        sb.Append($"[上下文折叠] {lines.Length} 行 / {output.Length} 字符");
        sb.AppendLine("（完整内容见会话事件存储，可回放）");

        if (isCode)
        {
            var defs = lines.Where(l => DefRe.IsMatch(l)).Take(40).ToList();
            if (defs.Count > 0)
            {
                sb.AppendLine("-- 结构定义 --");
                foreach (var d in defs)
                    sb.AppendLine(d.Trim());
                sb.AppendLine($"-- 共 {defs.Count} 处定义 --");
            }
        }

        sb.AppendLine("-- 开头 --");
        foreach (var l in lines.Take(5))
            sb.AppendLine(l);

        sb.AppendLine($"-- 结尾（省略中间 {Math.Max(0, lines.Length - 10)} 行） --");
        foreach (var l in lines.TakeLast(5))
            sb.AppendLine(l);

        return sb.ToString();
    }
}
