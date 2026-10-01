// ============================================================
// CreateDirTool.cs
// 创建工作区内的目录（含所有父目录）。
// ============================================================

using System.Text.Json;

namespace Agent.Infrastructure.Tools;

/// <summary>
/// 创建目录的工具。
/// 已存在时不报错，无副作用（幂等）。
/// </summary>
public sealed class CreateDirTool : AgentToolBase
{
    private readonly string _workspaceRoot;

    /// <summary>构造函数。</summary>
    public CreateDirTool(string workspaceRoot)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    /// <summary>工具名。</summary>
    public override string Name => "create_dir";

    /// <summary>工具描述。</summary>
    public override string Description =>
        "在工作区内创建目录（含所有父目录）。已存在则无副作用。" +
        "适合在写文件前先声明目录结构，让项目组织更清晰。";

    /// <summary>参数 Schema。</summary>
    protected override string SchemaJson => """
    {
      "type": "object",
      "properties": {
        "path": {
          "type": "string",
          "description": "相对于工作区根目录的目录路径"
        }
      },
      "required": ["path"]
    }
    """;

    /// <summary>执行工具。</summary>
    public override Task<string> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var relative = args.GetProperty("path").GetString()!;

        var fullPath = Path.GetFullPath(Path.Combine(_workspaceRoot, relative));

        // 越界检查
        if (!fullPath.StartsWith(_workspaceRoot, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult("错误：路径越界。");

        // 创建（幂等）
        Directory.CreateDirectory(fullPath);

        return Task.FromResult($"已创建目录 {relative}。");
    }
}