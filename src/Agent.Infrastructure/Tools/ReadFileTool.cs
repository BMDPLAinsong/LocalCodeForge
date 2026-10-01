// 引入 JSON
using System.Text.Json;

// 命名空间
namespace Agent.Infrastructure.Tools;

/// <summary>
/// 读取文本文件内容的工具。
/// </summary>
public sealed class ReadFileTool : AgentToolBase
{
    // 工作区根目录
    private readonly string _workspaceRoot;
    // 单文件读取大小上限，防止一次读入过大文件
    private const int MaxBytes = 200 * 1024;

    /// <summary>构造函数。</summary>
    public ReadFileTool(string workspaceRoot)
    {
        // 规范化路径
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    /// <summary>工具名。</summary>
    public override string Name => "read_file";

    /// <summary>工具描述。</summary>
    public override string Description =>
        "读取工作区内某个文本文件的内容。path 相对于工作区根目录。";

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

        // 完整路径
        var fullPath = Path.GetFullPath(Path.Combine(_workspaceRoot, relative));

        // 越界检查
        if (!fullPath.StartsWith(_workspaceRoot, StringComparison.OrdinalIgnoreCase))
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