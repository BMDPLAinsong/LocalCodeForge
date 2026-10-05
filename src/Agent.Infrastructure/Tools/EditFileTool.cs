// ============================================================
// EditFileTool.cs
// 局部修改文件。old_string 必须在文件中唯一出现。
// ============================================================

using System.Text.Json;

namespace Agent.Infrastructure.Tools;

using Agent.Infrastructure.Metrics;
using Agent.Infrastructure.Project;

/// <summary>
/// 局部编辑文件的工具。
/// 通过 old_string / new_string 精确替换文件片段。
/// </summary>
public sealed class EditFileTool : AgentToolBase
{
    private readonly string _workspaceRoot;
    private readonly string? _projectRoot;
    private readonly bool _projectWriteEnabled;
    private readonly string _taskId;

    // 【C】工具拒绝事件落库（可空：tests 直接构造不传 → 静默跳过；DI 传真实 store）
    private readonly ToolRejectionStore? _rejections;

    // 【批次1·E】显式写目标授权（CLI 层注入；敏感区源头兜底）
    private readonly List<string> _explicitWriteRoots;

    /// <summary>构造函数。</summary>
    public EditFileTool(string workspaceRoot, string? projectRoot = null, bool projectWriteEnabled = false, string? taskId = null, ToolRejectionStore? rejections = null, IEnumerable<string>? explicitWriteRoots = null)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _projectRoot = string.IsNullOrWhiteSpace(projectRoot) ? null : Path.GetFullPath(projectRoot);
        _projectWriteEnabled = projectWriteEnabled;
        _taskId = string.IsNullOrWhiteSpace(taskId) ? "default" : taskId;
        _rejections = rejections;
        _explicitWriteRoots = explicitWriteRoots?
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(Path.GetFullPath)
            .ToList() ?? new List<string>();
    }

    /// <summary>记录一次拒绝事件（fail-safe：记录失败不影响工具主流程）。</summary>
    private async Task RecordRejectionAsync(string category, string path, string message, CancellationToken ct)
    {
        try
        {
            if (_rejections is not null)
                await _rejections.RecordAsync("edit_file", category, path, _taskId, message, ct);
        }
        catch
        {
            // 静默：记录失败不影响工具主流程
        }
    }

    /// <summary>工具名。</summary>
    public override string Name => "edit_file";

    /// <summary>工具描述。</summary>
    public override string Description =>
        "在已有文件中精确替换一段文本。old_string 必须在文件中唯一出现（否则会失败）。" +
        "适合修改单个函数/类/配置项，比 write_file 整体重写更安全。" +
        "path 相对于工程根（若 use --write 工程可写），否则相对于任务工作区根目录。" +
        "使用前必须先用 read_file 查看文件当前内容。";

    /// <summary>参数 Schema。</summary>
    protected override string SchemaJson => """
    {
      "type": "object",
      "properties": {
        "path": {
          "type": "string",
          "description": "相对于工作区根目录的文件路径"
        },
        "old_string": {
          "type": "string",
          "description": "要被替换的原文，必须唯一"
        },
        "new_string": {
          "type": "string",
          "description": "替换后的新文本"
        }
      },
      "required": ["path", "old_string", "new_string"]
    }
    """;

    /// <summary>执行工具。</summary>
    public override async Task<string> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var relative = args.GetProperty("path").GetString()!;
        var oldStr = args.GetProperty("old_string").GetString()!;
        var newStr = args.GetProperty("new_string").GetString()!;
        // 【校验 1】拒绝空 old_string
        // 空 old_string 在 C# string.IndexOf 里会返回 0，但替换逻辑会出错
        if (string.IsNullOrEmpty(oldStr))
        {
            var err = "错误：old_string 不能为空。如需向空文件写入内容，请用 write_file。";
            await RecordRejectionAsync("bad_args", relative, err, ct);
            return err;
        }

        // 解析基准：工程可写时以工程根为基准（工程感知 P1），否则任务目录
        var baseRoot = _projectWriteEnabled && _projectRoot is not null ? _projectRoot : _workspaceRoot;
        var fullPath = Path.GetFullPath(Path.Combine(baseRoot, relative));

        // 【写白名单 · 双根保护】WriteRoots = {workspaceRoot} ∪ {projectRoot(工程可写)} ∪ {explicitWriteRoots(批次1·E)}；
        // PathBoundary 逐段比较根治 StartsWith 前缀假阳性（X:\proj2 不再被 X:\proj 放行）
        var insideWorkspace = PathBoundary.IsInside(_workspaceRoot, fullPath);
        var insideProject = _projectWriteEnabled && _projectRoot is not null
            && PathBoundary.IsInside(_projectRoot, fullPath);
        var insideExplicit = _explicitWriteRoots.Any(r => PathBoundary.IsInside(r, fullPath));
        if (!insideWorkspace && !insideProject && !insideExplicit)
        {
            var err = "错误：路径越界。";
            await RecordRejectionAsync("out_of_bounds", relative, err, ct);
            return err;
        }

        // 【批次1·E】敏感区源头兜底（授权入口已滤，此处防漏网）
        if (SensitivePathGuard.IsDenied(fullPath))
        {
            var err = "错误：路径属于敏感区（系统/凭据），拒绝编辑。";
            await RecordRejectionAsync("sensitive", relative, err, ct);
            return err;
        }

        // 【P-D】编辑引入类型定义冲突检测：工程可写时，new_string 定义了工程【其他文件】已有类型 → 拒绝。
        // 与 write_file 同理（模型可能用 edit_file 引入重复类型定义）；局部编辑本身少见，成本毫秒级。
        if (insideProject && _projectRoot is not null)
        {
            // 【方案A】工程模式禁止编辑项目文件（同 write_file 写前拦截）
            var projExt = Path.GetExtension(fullPath);
            if (projExt.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                || projExt.Equals(".sln", StringComparison.OrdinalIgnoreCase)
                || projExt.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
                || projExt.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
                || projExt.Equals(".vbproj", StringComparison.OrdinalIgnoreCase))
            {
                var err = $"错误：工程模式禁止修改项目文件 {relative}。工程结构由工程根已有项目锚定；" +
                       "本任务只需新增/修改 .cs 源文件，无需改动项目文件。\n退出码：126";
                await RecordRejectionAsync("project_file", relative, err, ct);
                return err;
            }

            var conflicts = TypeConflictDetector.DetectDuplicateTypes(_projectRoot, fullPath, newStr);
            if (conflicts.Count > 0)
            {
                var hint = TypeConflictDetector.BuildExistingTypesHint(
                    TypeConflictDetector.GetExistingTypes(_projectRoot, fullPath),
                    relative, true);
                var err = $"错误：编辑内容检测到类型重复定义：{string.Join("；", conflicts)}。\n" +
                       $"{hint}\n" +
                       $"请勿在 {relative} 中重复定义工程已有类型；如需修改已有类型，请替换类型内部实现而不是重新定义。";
                await RecordRejectionAsync("pd_conflict", relative, err, ct);
                return err;
            }
        }

        // 工程感知 P1：修改工程根内文件前自动备份（可 rollback 恢复）——P-B 按任务隔离
        if (insideProject)
        {
            BackupService.Backup(_workspaceRoot, fullPath, _projectRoot!, _taskId);
        }

        // 工程只读提示：工程模式未授权时，若相对路径在工程根内存在对应文件 → 拒绝并提示
        if (!_projectWriteEnabled && _projectRoot is not null)
        {
            var projPath = Path.GetFullPath(Path.Combine(_projectRoot, relative));
            if (projPath.StartsWith(_projectRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(projPath))
            {
                var err = $"错误：工程只读模式（use 未带 --write），拒绝修改工程文件 {relative}。\n如需修改工程文件，请使用 use --write 开启工程可写后重试。";
                await RecordRejectionAsync("read_only", relative, err, ct);
                return err;
            }
        }

        // 文件不存在
        if (!File.Exists(fullPath))
        {
            var err = $"错误：文件不存在：{relative}。请先用 write_file 创建。";
            await RecordRejectionAsync("not_found", relative, err, ct);
            return err;
        }

        // 读取内容
        var content = await File.ReadAllTextAsync(fullPath, ct);

        // 找到第一次出现位置
        var index = content.IndexOf(oldStr, StringComparison.Ordinal);

        if (index < 0)
        {
            // 编辑冲突检测：找不到 old_string 时查最近备份——
            // 若有备份且当前内容与备份不一致，说明文件自备份后已被外部/前一任务修改（漂移）。
            if (insideProject)
            {
                var relPath = Path.GetRelativePath(_projectRoot!, fullPath);
                var backupHit = BackupService.ListBackups(_workspaceRoot)
                    .FirstOrDefault(b => string.Equals(b.TaskId, _taskId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(b.Rel, relPath, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(backupHit.Full))
                {
                    var err = $"错误：在 {relative} 中找不到 old_string，且该文件自最近备份（{backupHit.Stamp}）后内容已变化" +
                           "——疑似被外部/前一任务修改（漂移）。请先 read_file 查看当前内容后重试；如需放弃本次改动可 rollback 恢复。";
                    await RecordRejectionAsync("drift", relative, err, ct);
                    return err;
                }
            }

            var err2 = $"错误：在 {relative} 中找不到 old_string。请先用 read_file 查看文件当前内容。";
            await RecordRejectionAsync("old_not_found", relative, err2, ct);
            return err2;
        }

        // 检查唯一性
        var secondIndex = content.IndexOf(oldStr, index + 1, StringComparison.Ordinal);
        if (secondIndex >= 0)
        {
            var err3 = $"错误：old_string 在 {relative} 中出现了多次，请扩大上下文使其唯一。";
            await RecordRejectionAsync("not_unique", relative, err3, ct);
            return err3;
        }

        // 执行替换
        var updated = content[..index] + newStr + content[(index + oldStr.Length)..];

        // 单一来源后处理（与 write_file 一致，§14-3j）：交互输入拒绝 + .py 外部 HTTP 补 timeout
        var ext = Path.GetExtension(relative).ToLowerInvariant();
        var interactiveError = PyCodeGuard.DetectInteractive(updated, relative);
        if (interactiveError is not null)
        {
            await RecordRejectionAsync("interactive", relative, interactiveError, ct);
            return interactiveError;
        }

        if (ext == ".py")
        {
            updated = PyCodeGuard.EnsureHttpTimeout(updated);
        }

        // 写回
        await File.WriteAllTextAsync(fullPath, updated, ct);

        return $"已修改 {relative}：替换 {oldStr.Length} 字符 → {newStr.Length} 字符。";
    }
}