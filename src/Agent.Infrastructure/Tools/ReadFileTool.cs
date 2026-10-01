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
        "读取文本文件的内容。path 相对于工程根（若已 use/--project 设置工程模式），否则相对于任务工作区根目录。";

    /// <summary>参数 Schema。</summary>
    protected override string SchemaJson => """
    {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "相对于工作区根目录的文件路径" }
      },
      "required": ["path"]
    }
    """;

    /// <summary>执行工具。</summary>
    public override async Task<string> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        // 取出 path 参数
        var relative = args.GetProperty("path").GetString()!;

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

        // 文件过大
        var info = new FileInfo(fullPath);
        if (info.Length > MaxBytes)
            return $"错误：文件过大（{info.Length} bytes），超过 {MaxBytes} bytes 上限。";

        // 读取内容
        var content = await File.ReadAllTextAsync(fullPath, ct);

        // 返回
        return content;
    }
}