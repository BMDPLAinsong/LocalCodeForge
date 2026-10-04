// 引入 JSON
using System.Text.Json;

// 命名空间
namespace Agent.Infrastructure.Tools;

using Agent.Infrastructure.Metrics;
using Agent.Infrastructure.Project;

/// <summary>
/// 写入文件的工具。用于创建新文件。
/// </summary>
public sealed class WriteFileTool : AgentToolBase
{
    // 工作区根目录 + 工程根（可选）+ 工程可写授权（P1）+ 任务归属（P-B 备份/标记隔离）
    private readonly string _workspaceRoot;
    private readonly string? _projectRoot;
    private readonly bool _projectWriteEnabled;
    private readonly string _taskId;

    // 【C】工具拒绝事件落库（可空：tests 直接构造不传 → 静默跳过；DI 传真实 store）
    private readonly ToolRejectionStore? _rejections;

    // 【B】P-D 熔断语义化：同文件被 P-D 类型冲突拒绝的次数（per-task 实例，无跨任务污染）。
    // 实证：模型被 P-D 拒后不换 edit_file、反复 write_file → 3 连拒熔断白跑。
    // 第 2 次拒绝即返回确定性指令（数据驱动引导，非提示词）。
    private readonly Dictionary<string, int> _pdRejectionCounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>构造函数。</summary>
    public WriteFileTool(string workspaceRoot, string? projectRoot = null, bool projectWriteEnabled = false, string? taskId = null, ToolRejectionStore? rejections = null)
    {
        // 规范化
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _projectRoot = string.IsNullOrWhiteSpace(projectRoot) ? null : Path.GetFullPath(projectRoot);
        _projectWriteEnabled = projectWriteEnabled;
        _taskId = string.IsNullOrWhiteSpace(taskId) ? "default" : taskId;
        _rejections = rejections;
    }

    /// <summary>记录一次拒绝事件（fail-safe：记录失败不影响工具主流程）。</summary>
    private async Task RecordRejectionAsync(string category, string path, string message, CancellationToken ct)
    {
        try
        {
            if (_rejections is not null)
                await _rejections.RecordAsync("write_file", category, path, _taskId, message, ct);
        }
        catch
        {
            // 静默：记录失败不影响工具主流程
        }
    }

    /// <summary>工具名。</summary>
    public override string Name => "write_file";

    /// <summary>工具描述。</summary>
    public override string Description =>
        "创建或覆盖一个文本文件。path 相对于工程根（若 use --write 工程可写），否则相对于任务工作区根目录。";

    /// <summary>参数 Schema。</summary>
    protected override string SchemaJson => """
    {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "相对于工作区根目录的文件路径" },
        "content": { "type": "string", "description": "要写入的完整文件内容" }
      },
      "required": ["path", "content"]
    }
    """;

    /// <summary>执行工具。</summary>
    public override async Task<string> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        // 取出 path
        var relative = args.GetProperty("path").GetString()!;
        // 取出 content
        var content = args.GetProperty("content").GetString()!;

        // 【校验 1】拒绝空内容（白名单除外）
        if (content.Length == 0)
        {
            // 白名单：允许空的占位文件
            var fileName = Path.GetFileName(relative).ToLowerInvariant();
            var allowedEmptyFiles = new[]
            {
                "__init__.py",
                ".gitkeep",
                ".keep",
                ".gitignore"
            };

            if (!allowedEmptyFiles.Contains(fileName))
            {
                var err = "错误：内容为空，拒绝写入。如果需要创建空占位文件（如 __init__.py），请写入单个换行符 \"\\n\"。";
                await RecordRejectionAsync("empty_content", relative, err, ct);
                return err;
            }

            // 白名单文件允许空内容——继续往下走，不做替换
        }

        // 【后处理 1】根据文件类型做强制修正
        content = PostProcess(content, relative);

        // 【后处理 1.5】交互输入拒绝（验证环境无终端输入，含 input/Console.ReadLine 会挂起）
        var interactiveError = PyCodeGuard.DetectInteractive(content, relative);
        if (interactiveError is not null)
        {
            await RecordRejectionAsync("interactive", relative, interactiveError, ct);
            return interactiveError;
        }

        // 【后处理 2】csproj 文件名检查：如果是 ".csproj" 或异常名，自动改名
        if (relative.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var fileNameWithoutExt = Path.GetFileNameWithoutExtension(relative);

            // 异常场景 1：文件名叫 ".csproj"
            // 异常场景 2：项目名以点开头（如 ".net.csproj"）
            if (string.IsNullOrEmpty(fileNameWithoutExt) || fileNameWithoutExt.StartsWith('.'))
            {
                var dir = Path.GetDirectoryName(relative) ?? "";
                var newName = string.IsNullOrEmpty(dir)
                    ? "ConsoleApp.csproj"
                    : $"{SanitizeProjectName(Path.GetFileName(dir))}.csproj";

                var newRelative = string.IsNullOrEmpty(dir)
                    ? newName
                    : Path.Combine(dir, newName).Replace('\\', '/');

                relative = newRelative;
            }
        }

        // 解析基准：工程可写时以工程根为基准（工程感知 P1），否则任务目录
        var baseRoot = _projectWriteEnabled && _projectRoot is not null ? _projectRoot : _workspaceRoot;

        // 完整路径
        var fullPath = Path.GetFullPath(Path.Combine(baseRoot, relative));

        // 【写白名单 · 双根保护】WriteRoots = {workspaceRoot} ∪ {projectRoot(工程可写)}；
        // PathBoundary 逐段比较根治 StartsWith 前缀假阳性（X:\proj2 不再被 X:\proj 放行）
        var insideWorkspace = PathBoundary.IsInside(_workspaceRoot, fullPath);
        var insideProject = _projectWriteEnabled && _projectRoot is not null
            && PathBoundary.IsInside(_projectRoot, fullPath);
        if (!insideWorkspace && !insideProject)
        {
            var err = "错误：路径越界。";
            await RecordRejectionAsync("out_of_bounds", relative, err, ct);
            return err;
        }

        // 【P-D】写前类型冲突检测：工程可写时，新内容定义了工程【其他文件】已有类型 → 拒绝写入。
        // ShippingService 类任务 3/3 失败根因：模型把已有 class Order 重复定义进 OrderService.cs（CS0101）。
        // 确定性拦截（正则扫工程已有类型）+ 附"已有类型清单"引导（数据驱动，非提示词）。
        if (insideProject && _projectRoot is not null)
        {
            // 【方案A】工程模式写前拦截项目文件：.csproj/.sln 是工程结构锚，工程增量任务
            // （加类/改类）不应改动项目文件——拦截"侧路新建同名项目"假完成（模型新建
            // OrderSystem.csproj + 全套文件 → dotnet run 编译通过 = 假完成，demo 工程未接入）。
            var ext = Path.GetExtension(fullPath);
            if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".sln", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".vbproj", StringComparison.OrdinalIgnoreCase))
            {
                var err = $"错误：工程模式禁止修改/新建项目文件 {relative}。工程结构由工程根已有项目锚定；" +
                       "本任务只需新增/修改 .cs 源文件，无需改动项目文件。\n退出码：126";
                await RecordRejectionAsync("project_file", relative, err, ct);
                return err;
            }

            var conflicts = TypeConflictDetector.DetectDuplicateTypes(_projectRoot, fullPath, content);
            if (conflicts.Count > 0)
            {
                // 【B】P-D 熔断语义化：同路径被拒次数累计，第 2 次起返回确定性指令
                var count = _pdRejectionCounts.TryGetValue(fullPath, out var prev) ? prev + 1 : 1;
                _pdRejectionCounts[fullPath] = count;

                var hint = TypeConflictDetector.BuildExistingTypesHint(
                    TypeConflictDetector.GetExistingTypes(_projectRoot, fullPath),
                    relative, File.Exists(fullPath));

                if (count >= 2)
                {
                    var err = $"错误：{relative} 已是第 {count} 次因类型重复被拒绝。禁止再次使用 write_file 整体重写该文件。\n" +
                           $"必须改用 edit_file 做局部修改（保留文件其余内容）；如需定义新类型，请用 write_file 写入独立的新文件名。\n" +
                           $"{hint}";
                    await RecordRejectionAsync("pd_conflict", relative, err, ct);
                    return err;
                }

                var err1 = $"错误：检测到类型重复定义：{string.Join("；", conflicts)}。\n" +
                       $"{hint}\n" +
                       $"请勿在 {relative} 中重复定义工程已有类型；如需修改已有类型，请用 edit_file 局部编辑，不要整体重写文件。";
                await RecordRejectionAsync("pd_conflict", relative, err1, ct);
                return err1;
            }
        }

        // 工程只读提示：工程模式未授权（use 未带 --write）时，
        // 若相对路径在工程根内存在对应文件（模型意图是修改工程文件）→ 拒绝并提示，
        // 避免模型误以为"文件不存在"而写入任务目录产生幽灵文件。
        if (!_projectWriteEnabled && _projectRoot is not null)
        {
            var projPath = Path.GetFullPath(Path.Combine(_projectRoot, relative));
            if (projPath.StartsWith(_projectRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(projPath))
            {
                var err = $"错误：工程只读模式（use 未带 --write），拒绝写入工程文件 {relative}。\n如需修改工程文件，请使用 use --write 开启工程可写后重试。";
                await RecordRejectionAsync("read_only", relative, err, ct);
                return err;
            }
        }

        // 工程感知 P1：写工程根内已存在文件前自动备份（可 rollback 恢复）——P-B 按任务隔离
        if (insideProject && File.Exists(fullPath))
        {
            BackupService.Backup(_workspaceRoot, fullPath, _projectRoot!, _taskId);
        }
        // 【P0-1】工程可写模式写【新建】工程文件 → 标记 .backup/<taskId>/.new/<rel>，
        // 任务失败自动回滚时删除这些新文件（备份只覆盖已存在文件，新文件需单独追踪）。
        else if (insideProject && _projectRoot is not null)
        {
            BackupService.MarkNewFile(_workspaceRoot, fullPath, _projectRoot, _taskId);
        }

        // 确保父目录存在
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
        {
            Directory.CreateDirectory(parent);
        }

        // 写入文件（覆盖模式）
        await File.WriteAllTextAsync(fullPath, content, ct);

        // 【关键】如果写的是 .cs 文件，且同目录下没有 .csproj，自动补一个
        var autoCreatedCsproj = TryAutoCreateCsproj(fullPath, baseRoot);

        // 返回确认信息
        var msg = $"已写入 {relative}，共 {content.Length} 字符。";
        if (autoCreatedCsproj is not null)
        {
            msg += $"\n[自动补全] 检测到缺少 .csproj，已创建 {autoCreatedCsproj}";
        }

        return msg;
    }

    // ─────────── 后处理 ───────────

    /// <summary>
    /// 写文件前的后处理：根据文件类型强制修正内容。
    /// </summary>
    private string PostProcess(string content, string relativePath)
    {
        var ext = Path.GetExtension(relativePath).ToLowerInvariant();

        // csproj：强制 net10.0 + obj/bin 隔离
        if (ext == ".csproj")
        {
            content = EnsureCsprojTargetFramework(content);
        }

        // .py：argparse 强制有 default
        if (ext == ".py")
        {
            content = EnsureArgparseDefaults(content);
            content = PyCodeGuard.EnsureHttpTimeout(content);
        }

        return content;
    }

    /// <summary>csproj TargetFramework 强制为 net10.0，并加 obj/bin 隔离路径。</summary>
    private static string EnsureCsprojTargetFramework(string code)
    {
        // 替换现有 TargetFramework
        var result = System.Text.RegularExpressions.Regex.Replace(
            code,
            @"<TargetFramework>[^<]*</TargetFramework>",
            "<TargetFramework>net10.0</TargetFramework>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // 没有就插入
        if (!result.Contains("<TargetFramework>", StringComparison.OrdinalIgnoreCase))
        {
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                @"<PropertyGroup>",
                "<PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // 【关键】如果缺少 BaseIntermediateOutputPath，插入到第一个 PropertyGroup
        // 这解决"多个 csproj 共享 obj/ 目录"导致的 CS0579 冲突
        if (!result.Contains("BaseIntermediateOutputPath", StringComparison.OrdinalIgnoreCase))
        {
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                @"(<PropertyGroup>)",
                "$1\n    <BaseIntermediateOutputPath>obj\\</BaseIntermediateOutputPath>\n    <BaseOutputPath>bin\\</BaseOutputPath>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1));
        }

        return result;
    }

    /// <summary>Python argparse 参数强制有 default。</summary>
    private static string EnsureArgparseDefaults(string code)
    {
        if (!code.Contains("add_argument")) return code;

        var pattern = @"add_argument\(\s*[""'](--?[\w\-]+)[""']\s*,\s*([^)]*)\)";

        return System.Text.RegularExpressions.Regex.Replace(code, pattern, match =>
        {
            var argName = match.Groups[1].Value;
            var restArgs = match.Groups[2].Value.Trim();

            if (restArgs.Contains("default=", StringComparison.OrdinalIgnoreCase))
                return match.Value;

            if (restArgs.Contains("required=True", StringComparison.OrdinalIgnoreCase))
            {
                restArgs = System.Text.RegularExpressions.Regex.Replace(
                    restArgs, @"required\s*=\s*True", "required=False",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return $"add_argument(\"{argName}\", default=\"\", {restArgs})";
            }

            return $"add_argument(\"{argName}\", default=\"\", {restArgs})";
        });
    }

    // ─────────── 自动补 csproj ───────────

    /// <summary>
    /// 如果写的是 .cs 文件，且同目录下没有 .csproj，自动创建一个。
    /// </summary>
    private string? TryAutoCreateCsproj(string fullCsPath, string baseRoot)
    {
        // 只在 .cs 文件时触发
        if (!fullCsPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return null;

        // 获取目录
        var dir = Path.GetDirectoryName(fullCsPath);
        if (dir is null) return null;

        // 检查目录里是否已有 .csproj
        var existingCsproj = Directory.GetFiles(dir, "*.csproj");
        if (existingCsproj.Length > 0)
            return null;

        // 【改进 1】用 .cs 文件名作为项目名
        var csFileName = Path.GetFileNameWithoutExtension(fullCsPath);
        if (string.IsNullOrEmpty(csFileName))
            csFileName = "ConsoleApp";

        // 项目名清洗
        csFileName = SanitizeProjectName(csFileName);

        var csprojPath = Path.Combine(dir, $"{csFileName}.csproj");

        // 已存在 → 不覆盖
        if (File.Exists(csprojPath))
            return null;

        // csproj 内容（强制 net10.0 + obj/bin 隔离）
        var csprojContent = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>disable</Nullable>
                <ImplicitUsings>disable</ImplicitUsings>
                <BaseIntermediateOutputPath>obj\</BaseIntermediateOutputPath>
                <BaseOutputPath>bin\</BaseOutputPath>
              </PropertyGroup>
            </Project>
            """;

        // 写文件
        File.WriteAllText(csprojPath, csprojContent);

        // 返回相对路径（基于实际写入根）
        var relative = Path.GetRelativePath(baseRoot, csprojPath);
        return relative;
    }

    /// <summary>
    /// 清洗项目名：去掉路径非法字符，避免时间戳类名字。
    /// </summary>
    private static string SanitizeProjectName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "ConsoleApp";

        // 去掉所有非法字符
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Where(c => !invalid.Contains(c)).ToArray());

        // 清洗后为空
        if (string.IsNullOrWhiteSpace(sanitized))
            return "ConsoleApp";

        // 纯数字开头（可能是时间戳）→ 加前缀
        if (char.IsDigit(sanitized[0]))
            sanitized = "Project_" + sanitized;

        return sanitized;
    }
}