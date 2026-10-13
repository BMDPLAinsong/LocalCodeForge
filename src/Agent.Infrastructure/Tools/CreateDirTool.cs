// ============================================================
// CreateDirTool.cs
// 创建工作区内的目录（含所有父目录）。
// ============================================================

using System.Text.Json;

namespace Agent.Infrastructure.Tools;

using Agent.Infrastructure.Project;

/// <summary>
/// 创建目录的工具。
/// 已存在时不报错，无副作用（幂等）。
/// </summary>
public sealed class CreateDirTool : AgentToolBase
{
    private readonly string _workspaceRoot;
    private readonly string? _projectRoot;
    private readonly bool _projectWriteEnabled;
    private readonly List<string> _explicitWriteRoots;

    /// <summary>构造函数。</summary>
    public CreateDirTool(string workspaceRoot, string? projectRoot = null, bool projectWriteEnabled = false, IEnumerable<string>? explicitWriteRoots = null)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _projectRoot = string.IsNullOrWhiteSpace(projectRoot) ? null : Path.GetFullPath(projectRoot);
        _projectWriteEnabled = projectWriteEnabled;
        _explicitWriteRoots = explicitWriteRoots?
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(Path.GetFullPath)
            .ToList() ?? new List<string>();
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
    public override Task<Agent.Core.Models.ToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var relative = args.GetProperty("path").GetString()!;

        // 解析基准：工程可写时以工程根为基准（工程感知 P1），否则任务目录
        var baseRoot = _projectWriteEnabled && _projectRoot is not null ? _projectRoot : _workspaceRoot;
        var fullPath = Path.GetFullPath(Path.Combine(baseRoot, relative));

        // 【写白名单 · 双根保护】WriteRoots = {workspaceRoot} ∪ {projectRoot(工程可写)} ∪ {explicitWriteRoots(批次1·E)}；逐段比较根治前缀假阳性
        var insideWorkspace = PathBoundary.IsInside(_workspaceRoot, fullPath);
        var insideProject = _projectWriteEnabled && _projectRoot is not null
            && PathBoundary.IsInside(_projectRoot, fullPath);
        var insideExplicit = _explicitWriteRoots.Any(r => PathBoundary.IsInside(r, fullPath));
        if (!insideWorkspace && !insideProject && !insideExplicit)
            return Task.FromResult(Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.OutOfBounds, "错误：路径越界。"));

        // 【批次1·E】敏感区源头兜底（授权入口已滤，此处防漏网）
        if (SensitivePathGuard.IsDenied(fullPath))
            return Task.FromResult(Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.SensitivePath, "错误：路径属于敏感区（系统/凭据），拒绝创建。"));

        // 创建（幂等）
        Directory.CreateDirectory(fullPath);

        return Task.FromResult(Agent.Core.Models.ToolResult.Ok($"已创建目录 {relative}。"));
    }
}