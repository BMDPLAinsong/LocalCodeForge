// ============================================================
// CodeAnalyzerTool.cs
// agent_pro 工程理解专项：确定性代码符号索引工具。
// 读代码 → 结构化"地图"（类/方法/属性/字段 + 行号），
// 让 4B 模型不必逐行探索两步链（read 定位不稳），
// 一次 analyze_code 即获得文件结构，再做理解/规划/修改。
// 纯正则+行号扫描（C#/Python），无 LLM 调用，确定性可测。
// ============================================================

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agent.Infrastructure.Tools;

/// <summary>
/// 分析代码文件结构：类型/方法/属性/函数 名称 + 行号索引。
/// </summary>
public sealed class CodeAnalyzerTool : AgentToolBase
{
    private readonly string _workspaceRoot;
    private readonly string? _projectRoot;
    private const int MaxBytes = 200 * 1024;

    public CodeAnalyzerTool(string workspaceRoot, string? projectRoot = null)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _projectRoot = string.IsNullOrWhiteSpace(projectRoot) ? null : Path.GetFullPath(projectRoot);
    }

    public override string Name => "analyze_code";

    public override string Description =>
        "分析代码文件的结构（类/接口/方法/属性/字段/函数 + 行号索引）。" +
        "工程理解用：先 analyze_code 拿到符号地图，再按需 read_file 具体行段，避免逐行探索。支持 C# 与 Python。";

    protected override string SchemaJson => """
    {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "相对于工程根（工程模式）或工作区根目录的代码文件路径" }
      },
      "required": ["path"]
    }
    """;

    public override Task<string> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var relative = args.GetProperty("path").GetString()!;
        var baseRoot = _projectRoot ?? _workspaceRoot;
        var fullPath = Path.GetFullPath(Path.Combine(baseRoot, relative));

        var insideWorkspace = fullPath.StartsWith(_workspaceRoot, StringComparison.OrdinalIgnoreCase);
        var insideProject = _projectRoot is not null && fullPath.StartsWith(_projectRoot, StringComparison.OrdinalIgnoreCase);
        if (!insideWorkspace && !insideProject)
            return Task.FromResult("错误：路径越界。");

        if (!File.Exists(fullPath))
            return Task.FromResult($"错误：文件不存在：{relative}");

        var info = new FileInfo(fullPath);
        if (info.Length > MaxBytes)
            return Task.FromResult($"错误：文件过大（{info.Length} bytes）。");

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        var content = File.ReadAllText(fullPath);
        var lines = content.Split('\n');

        var sb = new StringBuilder();
        var language = ext switch
        {
            ".cs" => "csharp",
            ".py" => "python",
            ".js" or ".ts" => "javascript",
            ".java" => "java",
            ".go" => "go",
            _ => "text"
        };
        sb.Append($"语言: {language} | 总行数: {lines.Length}\n");

        if (language is "csharp" or "java" or "javascript" or "go")
            AnalyzeCSharpLike(lines, sb, language);
        else if (language == "python")
            AnalyzePython(lines, sb);
        else
            sb.Append("（非代码文件，无符号索引）");

        return Task.FromResult(sb.ToString());
    }

    // ── C# / Java / JS / Go 风格：类型 + 方法/属性 ──
    private static readonly Regex TypeRe = new(
        @"^\s*(?:public|private|protected|internal|static|abstract|sealed|partial|readonly|final|export)*\s*(class|interface|enum|record|struct|trait|type)\s+([A-Za-z_]\w*)",
        RegexOptions.Compiled);

    private static readonly Regex MethodRe = new(
        @"^\s*(?:public|private|protected|internal|static|async|virtual|override|abstract|sealed|partial|final|export|func|def)*\s+[A-Za-z_][\w<>\[\],\?\s]*\s+([A-Za-z_]\w*)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex PropertyRe = new(
        @"^\s*(?:public|private|protected|internal|static|virtual|override|abstract|readonly|final)*\s+[A-Za-z_][\w<>\[\]?\s]*\s+([A-Za-z_]\w*)\s*\{\s*(get|set)",
        RegexOptions.Compiled);

    private static void AnalyzeCSharpLike(string[] lines, StringBuilder sb, string language)
    {
        var count = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var m = TypeRe.Match(line);
            if (m.Success)
            {
                sb.AppendLine($"[{i + 1}] {m.Groups[1].Value} {m.Groups[2].Value}");
                count++;
                continue;
            }
            m = PropertyRe.Match(line);
            if (m.Success)
            {
                sb.AppendLine($"[{i + 1}] 属性 {m.Groups[1].Value}");
                count++;
                continue;
            }
            m = MethodRe.Match(line);
            if (m.Success)
            {
                sb.AppendLine($"[{i + 1}] 方法 {m.Groups[1].Value}(...)");
                count++;
            }
        }
        sb.AppendLine($"符号总数: {count}");
    }

    // ── Python：class / def（含缩进方法） ──
    private static readonly Regex PyClassRe = new(@"^\s*class\s+([A-Za-z_]\w*)", RegexOptions.Compiled);
    private static readonly Regex PyDefRe = new(@"^\s*def\s+([A-Za-z_]\w*)\s*\(", RegexOptions.Compiled);

    private static void AnalyzePython(string[] lines, StringBuilder sb)
    {
        var count = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var m = PyClassRe.Match(line);
            if (m.Success)
            {
                sb.AppendLine($"[{i + 1}] class {m.Groups[1].Value}");
                count++;
                continue;
            }
            m = PyDefRe.Match(line);
            if (m.Success)
            {
                var indent = line.Length - line.TrimStart().Length;
                var kind = indent > 0 ? "方法" : "函数";
                sb.AppendLine($"[{i + 1}] {kind} {m.Groups[1].Value}(...)");
                count++;
            }
        }
        sb.AppendLine($"符号总数: {count}");
    }
}
