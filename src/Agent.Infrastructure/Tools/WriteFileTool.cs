// 引入 JSON
using System.Text.Json;

// 命名空间
namespace Agent.Infrastructure.Tools;

using Agent.Infrastructure.Project;

/// <summary>
/// 写入文件的工具。用于创建新文件。
/// </summary>
public sealed class WriteFileTool : AgentToolBase
{
    // 工作区根目录 + 工程根（可选）+ 工程可写授权（P1）
    private readonly string _workspaceRoot;
    private readonly string? _projectRoot;
    private readonly bool _projectWriteEnabled;

    /// <summary>构造函数。</summary>
    public WriteFileTool(string workspaceRoot, string? projectRoot = null, bool projectWriteEnabled = false)
    {
        // 规范化
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _projectRoot = string.IsNullOrWhiteSpace(projectRoot) ? null : Path.GetFullPath(projectRoot);
        _projectWriteEnabled = projectWriteEnabled;
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
                return "错误：内容为空，拒绝写入。如果需要创建空占位文件（如 __init__.py），请写入单个换行符 \"\\n\"。";
            }

            // 白名单文件允许空内容——继续往下走，不做替换
        }

        // 【后处理 1】根据文件类型做强制修正
        content = PostProcess(content, relative);

        // 【后处理 1.5】交互输入拒绝（验证环境无终端输入，含 input/Console.ReadLine 会挂起）
        var interactiveError = PyCodeGuard.DetectInteractive(content, relative);
        if (interactiveError is not null)
        {
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

        // 越界检查：任务工作区 或（工程可写时）工程根 内
        var insideWorkspace = fullPath.StartsWith(_workspaceRoot, StringComparison.OrdinalIgnoreCase);
        var insideProject = _projectWriteEnabled && _projectRoot is not null
            && fullPath.StartsWith(_projectRoot, StringComparison.OrdinalIgnoreCase);
        if (!insideWorkspace && !insideProject)
            return "错误：路径越界。";

        // 工程感知 P1：写工程根内已存在文件前自动备份（可 rollback 恢复）
        if (insideProject && File.Exists(fullPath))
        {
            BackupService.Backup(_workspaceRoot, fullPath, _projectRoot!);
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