// ============================================================
// DependencyAnalyzerTool.cs
// 工程图谱增强：analyze_deps —— 文件依赖查询。
// 输入 path → 输出：直接依赖（本文件引用了哪些文件的类型符号）
// + 反向依赖（谁引用了本文件的类型——改动影响面）。
// 与 find_symbol（单符号定位）、analyze_code（单文件结构）互补，
// 供多文件修改规划（改 A 前先看影响面 / 新文件应放哪）。
// 纯确定性：符号索引（SQLite 缓存）+ 词边界引用扫描，无 LLM。
// ============================================================

using System.Text;
using System.Text.Json;

namespace Agent.Infrastructure.Tools;

using Agent.Infrastructure.Project;

/// <summary>
/// 文件依赖查询工具。
/// </summary>
public sealed class DependencyAnalyzerTool : AgentToolBase
{
    private readonly string _workspaceRoot;
    private readonly string? _projectRoot;
    private readonly SymbolIndexStore _store;

    public DependencyAnalyzerTool(string workspaceRoot, string? projectRoot, SymbolIndexStore store)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _projectRoot = string.IsNullOrWhiteSpace(projectRoot) ? null : Path.GetFullPath(projectRoot);
        _store = store;   // 【10-14 P2-3】进程内单例（DI 注入），不再各自 new
    }

    public override string Name => "analyze_deps";

    public override string Description =>
        "分析代码文件的依赖关系：直接依赖（本文件引用了哪些文件的类型符号）+ 反向依赖（谁引用了本文件——改动影响面）。" +
        "工程模式（use/--project）可用，多文件修改前先查影响面。支持 C#/Python/JS/TS/Java/Go。";

    protected override string SchemaJson => """
    {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "相对于工程根（工程模式）或工作区根目录的代码文件路径" }
      },
      "required": ["path"]
    }
    """;

    public override Task<Agent.Core.Models.ToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        if (_projectRoot is null)
            return Task.FromResult(Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.NotProject, "错误：analyze_deps 需要工程模式（先 use <路径> 或 --project）。"));

        var relative = args.GetProperty("path").GetString()!;
        var fullPath = Path.GetFullPath(Path.Combine(_projectRoot, relative));

        // 【读策略 · 双根保护】工程模式放开 + 敏感区源头硬拒（analyze_deps 只服务工程模式）
        if (SensitivePathGuard.IsDenied(fullPath))
            return Task.FromResult(Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.SensitivePath, $"错误：路径涉及敏感区（{fullPath}），禁止读取。"));
        if (!File.Exists(fullPath))
            return Task.FromResult(Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.NotFound, $"错误：文件不存在：{relative}"));

        // 惰性构建符号索引（复用 find_symbol 的持久化索引）
        if (!_store.HasIndex(_projectRoot))
        {
            var built = _store.BuildAndSave(_projectRoot);
            if (built == 0)
                return Task.FromResult(Agent.Core.Models.ToolResult.Ok("未在工程中发现可索引的代码符号（工程过大或无代码定义）。"));
        }
        else
        {
            // 【P1-1】已有索引 → 增量重建：仅重扫 mtime 变更/新增文件，保持索引与磁盘一致
            _store.BuildAndSaveIncremental(_projectRoot);
        }

        var symbols = _store.GetAll(_projectRoot);
        if (symbols.Count == 0)
            return Task.FromResult(Agent.Core.Models.ToolResult.Ok("符号索引为空。"));

        var edges = DependencyAnalyzer.Build(_projectRoot, symbols);
        var norm = relative.Replace('\\', '/');

        var direct = DependencyAnalyzer.GetDirectDependencies(norm, edges);
        var reverse = DependencyAnalyzer.GetReverseDependencies(norm, edges);

        var sb = new StringBuilder();
        sb.AppendLine($"文件 {norm} 依赖分析：");
        if (direct.Count == 0)
            sb.AppendLine("  直接依赖：无（未引用其他文件的类型符号）");
        else
            sb.AppendLine($"  直接依赖（{direct.Count} 个文件）：");
        foreach (var d in direct)
            sb.AppendLine($"    → {d}");

        if (reverse.Count == 0)
            sb.AppendLine("  反向依赖（改动影响面）：无（无文件引用本文件的类型符号）");
        else
            sb.AppendLine($"  反向依赖（{reverse.Count} 个文件引用本文件，改动会影响它们）：");
        foreach (var r in reverse)
            sb.AppendLine($"    ← {r}");

        sb.AppendLine("（依赖基于类型符号引用，粗粒度；含注释/字符串内出现，仅供参考方向）");
        return Task.FromResult(Agent.Core.Models.ToolResult.Ok(sb.ToString()));
    }
}
