// ============================================================
// FindSymbolTool.cs
// 工程图谱查询工具：find_symbol name=<符号> → 定义位置（文件:行号+类型）。
// 首次查询惰性构建索引（SQLite 持久化，后续秒查）。
// 与 analyze_code（单文件结构）互补：跨文件定位。
// ============================================================

using System.Text;
using System.Text.Json;

namespace Agent.Infrastructure.Tools;

using Agent.Infrastructure.Project;

/// <summary>
/// 跨文件符号定位工具。
/// </summary>
public sealed class FindSymbolTool : AgentToolBase
{
    private readonly string? _projectRoot;
    private readonly SymbolIndexStore _store;

    /// <summary>构造函数。</summary>
    public FindSymbolTool(string workspaceRoot, string? projectRoot, SymbolIndexStore store)
    {
        _projectRoot = string.IsNullOrWhiteSpace(projectRoot) ? null : Path.GetFullPath(projectRoot);
        _store = store;   // 【10-14 P2-3】进程内单例（DI 注入），不再各自 new
    }

    public override string Name => "find_symbol";

    public override string Description =>
        "跨文件定位符号（类/方法/属性/函数）的定义位置。name 为符号名（不含括号）。" +
        "返回 [文件:行号] 类型清单；工程模式（use/--project）可用，首次调用自动构建符号索引。";

    protected override string SchemaJson => """
    {
      "type": "object",
      "properties": {
        "name": { "type": "string", "description": "要定位的符号名，如 OrderService、Process、get_weather" }
      },
      "required": ["name"]
    }
    """;

    public override Task<Agent.Core.Models.ToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        if (_projectRoot is null)
            return Task.FromResult(Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.NotProject, "错误：find_symbol 需要工程模式（先 use <路径> 或 --project）。"));

        var name = args.GetProperty("name").GetString()?.Trim() ?? "";
        if (name.Length == 0)
            return Task.FromResult(Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.InvalidArg, "错误：name 不能为空。"));

        // 惰性构建：工程未索引 → 首次调用建索引（SQLite 持久化，后续秒查）
        if (!_store.HasIndex(_projectRoot))
        {
            var built = _store.BuildAndSave(_projectRoot);
            if (built == 0)
                return Task.FromResult(Agent.Core.Models.ToolResult.Ok("未在工程中发现可索引的代码符号（无 .cs/.py/.java/.go/.js/.ts 定义或工程过大被跳过）。"));
        }
        else
        {
            // 【新鲜度】已索引 → 每次 Find 前增量刷新（P1-1 增量：对比 mtime 只重扫变更文件）。
            // 修复实测缺陷：索引惰性建后从不刷新，工程变更（恢复/新增大文件）后 find_symbol 查陈旧索引未命中。
            try
            {
                _store.BuildAndSaveIncremental(_projectRoot);
            }
            catch
            {
                // 增量失败回退旧索引（不致命）
            }
        }

        var hits = _store.Find(_projectRoot, name);
        if (hits.Count == 0)
        {
            // 无精确命中 → 前缀模糊提示（相似符号）
            return Task.FromResult(Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.NotFound, $"未找到符号 {name} 的定义。"));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"符号 {name} 定义位置（{hits.Count} 处）：");
        foreach (var h in hits)
            sb.AppendLine($"  [{h.File}:{h.Line}] {h.Kind} {h.Symbol}");

        // 【定位后执行引导】命中行号是事实（数据驱动）：引导模型用 offset 读目标区间 + edit_file 修改。
        // 实测断点：find_symbol 命中后模型仍 read_file 首页循环（不用 offset / 不 edit_file）。
        var first = hits[0];
        var start = Math.Max(1, first.Line - 10);
        sb.AppendLine(
            $"操作指引：请用 read_file 携带 offset={start}（limit=30）读取 [{first.File}] 第 {first.Line} 行附近内容确认上下文，" +
            $"确认后调用 edit_file 修改该位置。不要整页读取大文件。");
        return Task.FromResult(Agent.Core.Models.ToolResult.Ok(sb.ToString()));
    }
}
