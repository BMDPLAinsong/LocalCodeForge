// 引入 JSON
using System.Text.Json;

// 命名空间
namespace Agent.Infrastructure.Tools;

/// <summary>
/// 列出目录内容的工具。让 Agent 能探查项目结构。
/// </summary>
public sealed class ListDirTool : AgentToolBase
{
    // 工作区根目录，构造时注入
    private readonly string _workspaceRoot;

    /// <summary>构造函数，注入工作区根目录。</summary>
    public ListDirTool(string workspaceRoot)
    {
        // 规范化路径，去掉末尾的斜杠
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    /// <summary>工具名。</summary>
    public override string Name => "list_dir";

    /// <summary>工具描述。</summary>
    public override string Description =>
        "列出指定目录下的文件和子目录。path 相对于工作区根目录，传 \".\" 表示根目录。";

    /// <summary>参数 Schema。</summary>
    protected override string SchemaJson => """
    {
      "type": "object",
      "properties": {
        "path": {
          "type": "string",
          "description": "相对于工作区根目录的路径，例如 \".\" 或 \"src\""
        }
      },
      "required": ["path"]
    }
    """;

    /// <summary>执行工具。</summary>
    public override Task<string> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        // 取出 path 参数
        var relative = args.TryGetProperty("path", out var p) ? p.GetString() ?? "." : ".";

        // 拼接并规范化完整路径
        var fullPath = Path.GetFullPath(Path.Combine(_workspaceRoot, relative));

        // 安全检查：路径必须位于工作区内
        if (!fullPath.StartsWith(_workspaceRoot, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult("错误：路径越界。");
        }

        // 目录不存在
        if (!Directory.Exists(fullPath))
        {
            return Task.FromResult($"错误：目录不存在：{relative}");
        }

        // 用 StringBuilder 拼接输出
        var sb = new System.Text.StringBuilder();

        // 逐个子目录
        foreach (var dir in Directory.GetDirectories(fullPath))
        {
            // 只显示相对路径的最后一段 + 斜杠
            sb.AppendLine($"[DIR]  {Path.GetFileName(dir)}/");
        }

        // 逐个文件
        foreach (var file in Directory.GetFiles(fullPath))
        {
            // 显示文件名 + 大小
            var info = new FileInfo(file);
            sb.AppendLine($"[FILE] {info.Name}  ({info.Length} bytes)");
        }

        // 如果目录为空，返回明确提示（而不是空字符串）
        if (sb.Length == 0)
        {
            return Task.FromResult($"(目录 {relative} 是空的，没有任何文件或子目录)");
        }

        // 返回文本结果
        return Task.FromResult(sb.ToString());
    }
}