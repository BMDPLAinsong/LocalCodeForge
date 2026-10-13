// 引入 JSON
using System.Text.Json;

// 命名空间
namespace Agent.Infrastructure.Tools;

using Agent.Infrastructure.Project;

/// <summary>
/// 读取文本文件内容的工具。
/// </summary>
public sealed class ReadFileTool : AgentToolBase
{
    // 工作区根目录 + 工程根（可选，工程感知 P0）
    private readonly string _workspaceRoot;
    private readonly string? _projectRoot;
    // 【10-12 B】任务级显式读目标：当前任务输入中显式给出的绝对路径文件（TryResolveAbsolutePath 命中）。
    // 沙箱模式（无 use）读严格限定 run 目录，但用户主动在输入里写出路径 = 明确指向（给路径就干活）→ 放行；
    // 敏感区仍源头硬拒。任务串行执行（CommandHub 一次一任务），单例设置无并发风险。
    private string? _explicitReadTarget;
    // 单文件读取大小上限，防止一次读入过大文件
    private const int MaxBytes = 200 * 1024;
    // 大文件首读重定向阈值：工程模式下超过此大小且未带 offset → 拒绝并引导用符号定位
    // （模型面对 5000 行文件反复 read_file 首页不定位是日志实测瓶颈——工具层强制走 find_symbol/analyze_code）
    private const int RedirectThreshold = 100 * 1024;

    /// <summary>构造函数。</summary>
    public ReadFileTool(string workspaceRoot, string? projectRoot = null)
    {
        // 规范化路径
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _projectRoot = string.IsNullOrWhiteSpace(projectRoot) ? null : Path.GetFullPath(projectRoot);
    }

    /// <summary>设置任务级显式读目标（AgentLoop 任务开始时注入；null 清除）。</summary>
    public void SetExplicitReadTarget(string? fullPath)
    {
        _explicitReadTarget = string.IsNullOrWhiteSpace(fullPath) ? null : Path.GetFullPath(fullPath);
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
    public override async Task<Agent.Core.Models.ToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        // 取出 path 参数
        var relative = args.GetProperty("path").GetString()!;

        // 分页参数（可选，行级；offset 1 起，limit 默认 500 上限 1000）
        var offset = 1;
        var limit = 500;
        var hasPaging = args.TryGetProperty("offset", out var offEl) || args.TryGetProperty("limit", out _);
        var hasOffset = args.TryGetProperty("offset", out offEl) && offEl.ValueKind == JsonValueKind.Number;
        if (hasOffset)
            offset = Math.Max(1, offEl.GetInt32());
        if (args.TryGetProperty("limit", out var limEl) && limEl.ValueKind == JsonValueKind.Number)
            limit = Math.Clamp(limEl.GetInt32(), 1, 1000);

        // 解析基准：工程模式下相对路径以工程根为基准（工程感知 P0）
        var baseRoot = _projectRoot ?? _workspaceRoot;

        // 完整路径
        var fullPath = Path.GetFullPath(Path.Combine(baseRoot, relative));

        // 【读策略 · 双根保护】沙箱模式（projectRoot null）：仅限工作区根内（隔离语义保持）；
        // 工程模式（用户显式授权）：默认放开——"给路径就干活"。
        // 【10-12 B】沙箱例外：输入显式指向的绝对路径文件（用户主动给出路径 = 明确授权读）。
        // 两种模式统一：敏感区在读取动作【之前】源头硬拒——内容物理不进 chatHistory/SQLite/记忆库。
        if (_projectRoot is null && !PathBoundary.IsInside(_workspaceRoot, fullPath)
            && (_explicitReadTarget is null || !fullPath.Equals(_explicitReadTarget, StringComparison.OrdinalIgnoreCase)))
            return Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.OutOfBounds, "错误：路径越界。");
        if (SensitivePathGuard.IsDenied(fullPath))
            return Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.SensitivePath, $"错误：路径涉及敏感区（{fullPath}），禁止读取。");

        // 文件不存在（【10-09 方案1】工程模式下附候选路径：同名/近似文件——数据驱动，
        // 消灭模型"猜路径 → 失败 → 继续猜"的确定性浪费；沙箱模式无工程索引，保持原样）
        if (!File.Exists(fullPath))
        {
            if (_projectRoot is not null)
            {
                var candidates = ProjectScanner.FindFileCandidates(_projectRoot, relative);
                if (candidates.Count > 0)
                    return Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.NotFound, $"错误：文件不存在：{relative}。工程中相近文件（候选路径）：{string.Join(", ", candidates)}。请用候选路径重试。");
            }
            return Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.NotFound, $"错误：文件不存在：{relative}");
        }

        // 【大文件首读重定向】工程模式 + 大文件 + 未带 offset → 拒绝，强制走符号定位
        // （实测：模型反复 read_file 首页不续读不定位 → 工具层行为约束，非提示词；
        //   仅 limit 不算分页意图——模型传 limit 可绕过定位，故以 hasOffset 为准）
        var info = new FileInfo(fullPath);
        if (_projectRoot is not null && info.Length > RedirectThreshold && !hasOffset)
        {
            var totalLines = File.ReadLines(fullPath).Count();
            return Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.TooLarge,
                $"错误：文件过大（{info.Length} bytes，约 {totalLines} 行）。" +
                $"请先调用 find_symbol <目标类/方法名> 或 analyze_code 获取符号地图定位目标位置，" +
                $"再使用 read_file 并携带 offset/limit 参数精确读取目标区间；不要整页翻阅大文件。");
        }

        // 文件过大：不再拒绝，改分页（行级续读）——默认返回前 limit 行 + 总行数提示
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
                     (endLine < total ? $"如需继续阅读，请用 offset={endLine + 1}（limit={limit}）续读。\n" : string.Empty) +
                     $"如需定位类/方法/符号，请用 find_symbol <名称> 或 analyze_code 获取符号地图，避免逐页翻阅。\n";
        }

        // 返回
        return Agent.Core.Models.ToolResult.Ok(header + string.Join("\n", slice));
    }
}