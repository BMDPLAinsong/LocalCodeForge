// ============================================================
// CodeAnalyzerTool.cs
// agent_pro 工程理解专项：确定性代码符号索引工具。
// 读代码 → 结构化"地图"（类/方法/属性/字段 + 行号），
// 让 4B 模型不必逐行探索两步链（read 定位不稳），
// 一次 analyze_code 即获得文件结构，再做理解/规划/修改。
// 纯正则+行号扫描（C#/Python），无 LLM 调用，确定性可测。
// 【10-08 符号地图治理】全量模式符号上限 300（只显示前 120 + 省略行 + 定向引导）；
// 新增可选 range 参数（"1500-1550" 1-based 行段含边界）按行段精确分析。
// 根因实证：BigService 5003 个方法 → 5003 行地图全量注入上下文 → 模型被淹没，
// analyze_code 后无后续动作（复测 write_not_attempted 卡分析阶段）。
// ============================================================

using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agent.Infrastructure.Tools;

using Agent.Infrastructure.Project;

/// <summary>
/// 分析代码文件结构：类型/方法/属性/函数 名称 + 行号索引。
/// </summary>
public sealed class CodeAnalyzerTool : AgentToolBase
{
    private readonly string _workspaceRoot;
    private readonly string? _projectRoot;
    // 符号地图是行级正则扫描（不进模型上下文），272KB/5000 行毫秒级完成——
    // 去掉原 200KB 限制，上限 5MB 防极端大文件（实测：重定向引导模型走 analyze_code 却撞 200KB 墙 → 死路）
    private const int MaxBytes = 5 * 1024 * 1024;
    private const int MaxSymbolEntries = 300;
    private const int HeadSymbolEntries = 120;

    // range 参数："1500-1550"（1-based 行段，含边界）
    private static readonly Regex RangeRe = new(@"^(\d+)\s*-\s*(\d+)$", RegexOptions.Compiled);

    public CodeAnalyzerTool(string workspaceRoot, string? projectRoot = null)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _projectRoot = string.IsNullOrWhiteSpace(projectRoot) ? null : Path.GetFullPath(projectRoot);
    }

    public override string Name => "analyze_code";

    public override string Description =>
        "分析代码文件的结构（类/接口/方法/属性/字段/函数 + 行号索引）。" +
        "工程理解用：先 analyze_code 拿到符号地图，再按需 read_file 具体行段，避免逐行探索。支持 C# 与 Python。" +
        "大文件（符号超 300）只显示前 120 个，可用 range 参数按行段精确分析（如 range=\"1500-1550\"）。";

    protected override string SchemaJson => """
    {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "相对于工程根（工程模式）或工作区根目录的代码文件路径" },
        "range": { "type": "string", "description": "可选：1-based 行段（含边界），如 1500-1550，只分析该行段符号" }
      },
      "required": ["path"]
    }
    """;

    public override Task<Agent.Core.Models.ToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var relative = args.GetProperty("path").GetString()!;
        var baseRoot = _projectRoot ?? _workspaceRoot;
        var fullPath = Path.GetFullPath(Path.Combine(baseRoot, relative));

        // 【读策略 · 双根保护】沙箱模式仅限工作区根内；工程模式放开。敏感区源头硬拒。
        if (_projectRoot is null && !PathBoundary.IsInside(_workspaceRoot, fullPath))
            return Task.FromResult(Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.OutOfBounds, "错误：路径越界。"));
        if (SensitivePathGuard.IsDenied(fullPath))
            return Task.FromResult(Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.SensitivePath, $"错误：路径涉及敏感区（{fullPath}），禁止读取。"));

        if (!File.Exists(fullPath))
            return Task.FromResult(Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.NotFound, $"错误：文件不存在：{relative}"));

        var info = new FileInfo(fullPath);
        if (info.Length > MaxBytes)
            return Task.FromResult(Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.TooLarge, $"错误：文件过大（{info.Length} bytes）。"));

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        var content = File.ReadAllText(fullPath);
        var lines = content.Split('\n');

        var language = ext switch
        {
            ".cs" => "csharp",
            ".py" => "python",
            ".js" or ".ts" => "javascript",
            ".java" => "java",
            ".go" => "go",
            _ => "text"
        };

        // 解析 range（可选）：合法 → [startLine, endLine) 0-based 半开；非法 → 返回用法提示。
        var startLine = 0;
        var endLine = lines.Length;
        var rangeDesc = "";
        if (args.TryGetProperty("range", out var r) && r.ValueKind == JsonValueKind.String)
        {
            var raw = r.GetString();
            var m = RangeRe.Match(raw ?? "");
            if (!m.Success)
                return Task.FromResult(Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.InvalidArg, "错误：range 格式应为 起-止（1-based 行号，含边界），如 range=\"1500-1550\"。"));
            var s = int.Parse(m.Groups[1].Value);
            var e = int.Parse(m.Groups[2].Value);
            if (s < 1 || e < s)
                return Task.FromResult(Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.InvalidArg, $"错误：range 无效（{raw}）。起止应为 1-based 行号且 起 ≤ 止。"));
            if (s > lines.Length)
                return Task.FromResult(Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.InvalidArg, $"错误：range 起点 {s} 超出文件总行数 {lines.Length}。"));
            startLine = s - 1;
            endLine = Math.Min(e, lines.Length);
            rangeDesc = $" | 分析行段: {s}-{endLine}";
        }

        var sb = new StringBuilder();
        sb.Append($"语言: {language} | 总行数: {lines.Length}{rangeDesc}\n");

        if (language is "csharp" or "java" or "javascript" or "go")
        {
            var entries = AnalyzeCSharpLike(lines, language, startLine, endLine);
            RenderEntries(sb, entries);
        }
        else if (language == "python")
        {
            var entries = AnalyzePython(lines, startLine, endLine);
            RenderEntries(sb, entries);
        }
        else
        {
            sb.Append("（非代码文件，无符号索引）");
        }

        return Task.FromResult(Agent.Core.Models.ToolResult.Ok(sb.ToString()));
    }

    /// <summary>统一渲染：全量超限截断（前 HeadSymbolEntries + 省略行 + 定向引导），否则全量输出。</summary>
    private static void RenderEntries(StringBuilder sb, List<string> entries)
    {
        if (entries.Count > MaxSymbolEntries)
        {
            for (var i = 0; i < HeadSymbolEntries && i < entries.Count; i++)
                sb.AppendLine(entries[i]);
            sb.AppendLine($"... 省略 {entries.Count - HeadSymbolEntries} 个符号 ...");
            sb.AppendLine("请用 find_symbol <名称> 定向定位，或 analyze_code range=<起>-<止> 按行段分析");
        }
        else
        {
            foreach (var e in entries)
                sb.AppendLine(e);
        }
        sb.AppendLine($"符号总数: {entries.Count}");
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

    private static List<string> AnalyzeCSharpLike(string[] lines, string language, int startLine, int endLine)
    {
        var entries = new List<string>();
        for (var i = startLine; i < endLine && i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var m = TypeRe.Match(line);
            if (m.Success)
            {
                entries.Add($"[{i + 1}] {m.Groups[1].Value} {m.Groups[2].Value}");
                continue;
            }
            m = PropertyRe.Match(line);
            if (m.Success)
            {
                entries.Add($"[{i + 1}] 属性 {m.Groups[1].Value}");
                continue;
            }
            m = MethodRe.Match(line);
            if (m.Success)
            {
                entries.Add($"[{i + 1}] 方法 {m.Groups[1].Value}(...)");
            }
        }
        return entries;
    }

    // ── Python：class / def（含缩进方法） ──
    private static readonly Regex PyClassRe = new(@"^\s*class\s+([A-Za-z_]\w*)", RegexOptions.Compiled);
    private static readonly Regex PyDefRe = new(@"^\s*def\s+([A-Za-z_]\w*)\s*\(", RegexOptions.Compiled);

    private static List<string> AnalyzePython(string[] lines, int startLine, int endLine)
    {
        var entries = new List<string>();
        for (var i = startLine; i < endLine && i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var m = PyClassRe.Match(line);
            if (m.Success)
            {
                entries.Add($"[{i + 1}] class {m.Groups[1].Value}");
                continue;
            }
            m = PyDefRe.Match(line);
            if (m.Success)
            {
                var indent = line.Length - line.TrimStart().Length;
                var kind = indent > 0 ? "方法" : "函数";
                entries.Add($"[{i + 1}] {kind} {m.Groups[1].Value}(...)");
            }
        }
        return entries;
    }
}
