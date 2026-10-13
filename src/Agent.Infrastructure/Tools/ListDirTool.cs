// 引入 JSON
using System.Text.Json;

// 命名空间
namespace Agent.Infrastructure.Tools;

using Agent.Infrastructure.Project;

/// <summary>
/// 列出目录内容的工具。让 Agent 能探查项目结构。
/// </summary>
public sealed class ListDirTool : AgentToolBase
{
    // 工作区根目录 + 工程根（可选，工程感知 P0）
    private readonly string _workspaceRoot;
    private readonly string? _projectRoot;

    /// <summary>构造函数，注入工作区根目录（+ 可选工程根）。</summary>
    public ListDirTool(string workspaceRoot, string? projectRoot = null)
    {
        // 规范化路径，去掉末尾的斜杠
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _projectRoot = string.IsNullOrWhiteSpace(projectRoot) ? null : Path.GetFullPath(projectRoot);
    }

    /// <summary>工具名。</summary>
    public override string Name => "list_dir";

    /// <summary>工具描述。</summary>
    public override string Description =>
        "列出指定目录下的文件和子目录。path 相对于工程根（若已 use/--project 设置工程模式），否则相对于任务工作区根目录；传 \".\" 表示根目录。";

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
    public override Task<Agent.Core.Models.ToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        // 取出 path 参数
        var relative = args.TryGetProperty("path", out var p) ? p.GetString() ?? "." : ".";

        // 解析基准：工程模式下相对路径以工程根为基准（工程感知 P0）
        var baseRoot = _projectRoot ?? _workspaceRoot;

        // 拼接并规范化完整路径
        var fullPath = Path.GetFullPath(Path.Combine(baseRoot, relative));

        // 【读策略 · 双根保护】沙箱模式仅限工作区根内；工程模式放开。
        // 统一：敏感区源头硬拒（内容不进上下文）。
        if (_projectRoot is null && !PathBoundary.IsInside(_workspaceRoot, fullPath))
        {
            return Task.FromResult(Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.OutOfBounds, "错误：路径越界。"));
        }
        if (SensitivePathGuard.IsDenied(fullPath))
        {
            return Task.FromResult(Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.SensitivePath, $"错误：路径涉及敏感区（{fullPath}），禁止读取。"));
        }

        // 目录不存在
        if (!Directory.Exists(fullPath))
        {
            return Task.FromResult(Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.NotFound, $"错误：目录不存在：{relative}"));
        }

        // 【防呆】条目上限 500：读放开后 list_dir C:\Users 可能几万行——封顶并提示缩小范围
        var sb = new System.Text.StringBuilder();
        var dirs = Directory.GetDirectories(fullPath);
        var files = Directory.GetFiles(fullPath);
        if (dirs.Length + files.Length > 500)
        {
            return Task.FromResult(Agent.Core.Models.ToolResult.Fail(
                Agent.Core.Models.ToolErrorCodes.TooLarge,
                $"错误：目录条目过多（{dirs.Length + files.Length} 项），仅返回前 500 项会造成信息失真。" +
                $"请缩小范围（列出子目录后用 list_dir 逐级进入）。"));
        }

        // 逐个子目录
        foreach (var dir in dirs)
        {
            // 只显示相对路径的最后一段 + 斜杠
            sb.AppendLine($"[DIR]  {Path.GetFileName(dir)}/");
        }

        // 逐个文件
        foreach (var file in files)
        {
            // 显示文件名 + 大小
            var info = new FileInfo(file);
            sb.AppendLine($"[FILE] {info.Name}  ({info.Length} bytes)");
        }

        // 如果目录为空，返回明确提示（而不是空字符串）
        if (sb.Length == 0)
        {
            return Task.FromResult(Agent.Core.Models.ToolResult.Ok($"(目录 {relative} 是空的，没有任何文件或子目录)"));
        }

        // 返回文本结果
        return Task.FromResult(Agent.Core.Models.ToolResult.Ok(sb.ToString()));
    }
}