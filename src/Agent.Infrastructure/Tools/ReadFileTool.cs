// 引入 JSON
using System.Text.Json;

// 命名空间
namespace Agent.Infrastructure.Tools;

/// <summary>
/// 读取文本文件内容的工具。
/// </summary>
public sealed class ReadFileTool : AgentToolBase
{
    // 工作区根目录 + 工程根（可选，工程感知 P0）
    private readonly string _workspaceRoot;
    private readonly string? _projectRoot;
    // 单文件读取大小上限，防止一次读入过大文件
    private const int MaxBytes = 200 * 1024;

    /// <summary>构造函数。</summary>
    public ReadFileTool(string workspaceRoot, string? projectRoot = null)
    {
        // 规范化路径
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _projectRoot = string.IsNullOrWhiteSpace(projectRoot) ? null : Path.GetFullPath(projectRoot);
    }

    /// <summary>工具名。</summary>
    public override string Name => "read_file";

    /// <summary>工具描述。</summary>
    public override string Description =>
        "读取文本文件的内容。path 相对于工程根（若已 use/--project 设置工程模式），否则相对于任务工作区根目录。" +
        "大文件（>200KB）默认返回前 500 行并提示总行数，可用 offset/limit 按行分页续读。";

    /// <summary>参数 Schema。</summary>
    protected override string SchemaJson => """
    {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "相对于工作区根目录的文件路径" },
        "offset": { "type": "integer", "description": "起始行号（1 起，默认 1）。配合大文件续读使用" },
        "limit": { "type": "integer", "description": "读取行数（默认 500，上限 1000）" }
      },
      "required": ["path"]
    }
    """;

    /// <summary>执行工具。</summary>
    public override async Task<string> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        // 取出 path 参数
        var relative = args.GetProperty("path").GetString()!;

        // 分页参数（可选，行级；offset 1 起，limit 默认 500 上限 1000）
        var offset = 1;
        var limit = 500;
        var hasPaging = args.TryGetProperty("offset", out var offEl) || args.TryGetProperty("limit", out _);
        if (args.TryGetProperty("offset", out offEl) && offEl.ValueKind == JsonValueKind.Number)
            offset = Math.Max(1, offEl.GetInt32());
        if (args.TryGetProperty("limit", out var limEl) && limEl.ValueKind == JsonValueKind.Number)
            limit = Math.Clamp(limEl.GetInt32(), 1, 1000);

        // 解析基准：工程模式下相对路径以工程根为基准（工程感知 P0）
        var baseRoot = _projectRoot ?? _workspaceRoot;

        // 完整路径
        var fullPath = Path.GetFullPath(Path.Combine(baseRoot, relative));

        // 越界检查：任务工作区 或 工程根 内
        var insideWorkspace = fullPath.StartsWith(_workspaceRoot, StringComparison.OrdinalIgnoreCase);
        var insideProject = _projectRoot is not null && fullPath.StartsWith(_projectRoot, StringComparison.OrdinalIgnoreCase);
        if (!insideWorkspace && !insideProject)
            return "错误：路径越界。";

        // 文件不存在
        if (!File.Exists(fullPath))
            return $"错误：文件不存在：{relative}";

        // 文件过大：不再拒绝，改分页（行级续读）——默认返回前 limit 行 + 总行数提示
        var info = new FileInfo(fullPath);
        var lines = await File.ReadAllLinesAsync(fullPath, ct);
        var total = lines.Length;

        // 切片：offset 1 起
        var slice = lines.Skip(offset - 1).Take(limit).ToArray();

        // 分页头：大文件（>MaxBytes）或显式分页时附提示；小文件默认全文无头（向后兼容）
        var isLarge = info.Length > MaxBytes;
        var header = string.Empty;
        if (isLarge || hasPaging)
        {
            var startLine = offset;
            var endLine = offset + slice.Length - 1;
            header = $"【共 {total} 行，已返回第 {startLine}-{endLine} 行（文件 {info.Length} bytes）】\n" +
                     (endLine < total ? $"如需继续阅读，请用 offset={endLine + 1}（limit={limit}）续读。\n" : string.Empty);
        }

        // 返回
        return header + string.Join("\n", slice);
    }
}