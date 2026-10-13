// 引入诊断
using System.Diagnostics;
// 引入 JSON
using System.Text.Json;

// 命名空间
namespace Agent.Infrastructure.Tools;

/// <summary>
/// 执行命令的工具。带白名单与超时限制。
/// </summary>
public sealed class RunCommandTool : AgentToolBase
{
    // 工作区根目录，命令在此目录下执行
    private readonly string _workspaceRoot;
    // 工程根（可选，工程感知 §14-3t）：非 null 时启用路径边界检查
    private readonly string? _projectRoot;
    // 【10-10 问题2】工程可写标志：只读工程模式下写工具基准 = run 目录，run_command cwd 必须同基准，
    // 否则"文件写进 run 目录、命令在工程根执行"错位（E2E 实证：dotnet run --project parity 报路径不存在）。
    private readonly bool _projectWriteEnabled;

    // 允许执行的命令白名单，按命令的第一个 token 匹配
    private static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "python", "python3", "pip", "pip3",
        "dotnet", "node", "npm", "npx",
        "git", "dir", "ls", "type", "cat",
        "tree", "find", "findstr", "where",
        "grep", "Select-String"
    };

    // 单次命令超时（秒）：普通模式 30s；工程模式放宽 90s（dotnet build 大工程 / 长测试常 >30s 被误杀）
    private const int TimeoutSecondsSandbox = 30;
    private const int TimeoutSecondsProject = 90;

    /// <summary>构造函数。</summary>
    public RunCommandTool(string workspaceRoot, string? projectRoot = null, bool projectWriteEnabled = false)
    {
        // 规范化
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _projectRoot = string.IsNullOrWhiteSpace(projectRoot) ? null : Path.GetFullPath(projectRoot);
        _projectWriteEnabled = projectWriteEnabled;
    }

    /// <summary>
    /// 【10-10 问题2】解析 run_command 工作目录（确定性纯函数，可测）：
    /// 写工具基准与 run_command cwd 必须一致——可写工程模式两者都是工程根；
    /// 只读工程模式与沙箱模式写基准是 run 目录，cwd 也用 run 目录，
    /// 否则任务产物（run 目录）无法被 dotnet run --project 找到。
    /// </summary>
    public static string ResolveWorkingDirectory(string workspaceRoot, string? projectRoot, bool projectWriteEnabled)
    {
        var fullWs = Path.GetFullPath(workspaceRoot);
        if (projectWriteEnabled && !string.IsNullOrWhiteSpace(projectRoot))
            return Path.GetFullPath(projectRoot);
        return fullWs;
    }

    /// <summary>
    /// 解析命令超时秒数（10-08 B）：工程模式 90s；沙箱模式仅 dotnet 验证命令放宽到 90s——
    /// 实证：倒计时任务 dotnet run 冷编译（restore+build+run）在 30B 推理占 CPU 下 >30s 卡线、两次必败；
    /// 其余命令保持 30s（交互/死循环快速失败，不拖慢失败反馈）。
    /// </summary>
    public static int ResolveTimeoutSeconds(string? projectRoot, string command)
    {
        if (!string.IsNullOrWhiteSpace(projectRoot)) return TimeoutSecondsProject;
        var head = (command ?? "").TrimStart();
        if (head.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("dotnet ", StringComparison.OrdinalIgnoreCase)) return TimeoutSecondsProject;
        return TimeoutSecondsSandbox;
    }

    /// <summary>工具名。</summary>
    public override string Name => "run_command";

    /// <summary>工具描述。</summary>
    public override string Description =>
        "在工作区内执行一条命令并返回输出。只允许白名单内的命令（python/pip/dotnet/node/npm/git/dir/ls/type/cat/find/findstr/where/grep/Select-String）。";

    /// <summary>参数 Schema。</summary>
    protected override string SchemaJson => """
    {
      "type": "object",
      "properties": {
        "command": { "type": "string", "description": "要执行的完整命令，例如 python main.py" }
      },
      "required": ["command"]
    }
    """;

    /// <summary>执行工具。</summary>
    public override async Task<Agent.Core.Models.ToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        // 取出 command 参数
        var command = args.GetProperty("command").GetString()!;

        // 【新增】命令规范化
        command = NormalizeCommand(command);

        // 【双根保护】敏感路径源头拒（跨模式：工程/沙箱都生效）——防 run_command 绕过读黑名单：
        // 命令串先展开环境变量（%USERPROFILE% 等），再正则提取路径 token，归一化后 IsDenied。
        // 与 read_file 等读工具的敏感区硬拒同源：敏感内容不进 stdout → 不进 chatHistory/SQLite/记忆库。
        var sensitiveError = CheckSensitivePaths(command);
        if (sensitiveError is not null)
        {
            return Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.SensitiveCmd, sensitiveError);
        }

        // 【工程感知 §14-3t】工程模式路径边界：命令中的显式盘符路径必须落在
        // 任务根 或 工程根 内；.. 穿越直接拒绝（检查在 NormalizePython 之前，
        // 工具注入的系统 python 绝对路径不参与检查）。沙箱模式（projectRoot=null）保持现状。
        if (_projectRoot is not null)
        {
            var boundaryError = CheckPathBoundary(command, _workspaceRoot, _projectRoot);
            if (boundaryError is not null)
            {
                return Agent.Core.Models.ToolResult.Fail(boundaryError.Value.Code, boundaryError.Value.Message);
            }

            // 【D1 衍生】项目名幻觉校验（10-10 修正基准）：命令中的 .csproj 引用必须以
            // 实际 cwd（ResolveWorkingDirectory，与写工具基准一致）为准校验存在性。
            // 实测失败：模型乱写 `dotnet run --project OrderSystem.csproj` → 路径不存在。
            // 修正：原实现固定扫"工程根顶层"（TopDirectoryOnly）——本项目 csproj 全在
            // src/tests 子目录 → 空列表误拦一切 .csproj 引用；且只读工程模式产物在 run 目录
            // 也被误判"不存在于工程根"。改为相对 cwd 存在性 + 递归同名查找。
            var projError = CheckProjectName(command, ResolveWorkingDirectory(_workspaceRoot, _projectRoot, _projectWriteEnabled));
            if (projError is not null)
            {
                return Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.NotFound, projError);
            }

            // 【10-11 ③ 多 csproj 歧义】dotnet run 不带 --project 且 cwd 顶层 ≥2 个 .csproj
            // → MSB1011 无法确定启动项目（多文件写任务常见失败：模型同目录写多个项目）。
            // 进程执行前拦截，附目录内实际项目清单（数据驱动，非提示词）。
            var ambError = CheckDotnetRunAmbiguity(command, ResolveWorkingDirectory(_workspaceRoot, _projectRoot, _projectWriteEnabled));
            if (ambError is not null)
            {
                return Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.InvalidArg, ambError);
            }
        }

        // 提取第一个 token 作为命令名
        var firstToken = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];

        // 白名单校验
        if (!AllowedCommands.Contains(firstToken))
            // 【修复】返回体必须带非 0 退出码，否则下游（Orchestrator/AgentLoop）
            // 只认"退出码：N"判定失败，会把拒绝误判成成功
            return Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.CmdNotAllowed, $"错误：命令 {firstToken} 不在白名单中。\n退出码：127");

        // 【修复】拒绝 inline 执行——必须在启动进程之前检查
        // （原来放在进程跑完之后，命令实际已经执行过了）
        if (command.Contains(" -c ", StringComparison.OrdinalIgnoreCase) ||
            command.Contains(" -e ", StringComparison.OrdinalIgnoreCase))
        {
            return Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.InlineExec, "错误：出于安全考虑，禁止使用 -c / -e 的内联执行方式。请改用脚本文件。\n退出码：126");
        }

        // 【新增】Python 解释器归一化（2026-09-22）：
        // 裸 python 可能命中豆包沙箱解释器（无第三方包），而裸 pip 命中系统解释器（有包），
        // 造成"pip 已装但 python 找不到"的错位。统一到系统 Python。
        command = NormalizePython(command);

        // 创建进程
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            // 先切到 UTF-8 代码页
            Arguments = $"/c chcp 65001 > nul && {command}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // 【10-10 问题2】cwd 与写工具基准统一：可写工程模式 → 工程根（dotnet 构建工程）；
            // 只读工程模式 / 沙箱 → run 目录（任务产物验证）。此前只读工程模式 cwd=工程根，
            // 写工具却落在 run 目录 → dotnet run --project 找不到产物（E2E parity 实证）。
            WorkingDirectory = ResolveWorkingDirectory(_workspaceRoot, _projectRoot, _projectWriteEnabled),
            // 明确指定 UTF-8 编码
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        // 【新增】设置 Python 输出编码
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        // 启动进程
        using var process = Process.Start(psi)!;

        // 异步读取 stdout
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        // 异步读取 stderr
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        // 用 Task.WhenAny 实现超时
        var waitTask = process.WaitForExitAsync(ct);
        var timeoutSeconds = ResolveTimeoutSeconds(_projectRoot, command);
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), ct);

        // 哪个先完成
        var completed = await Task.WhenAny(waitTask, timeoutTask);

        // 超时则杀进程
        if (completed == timeoutTask)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
            // 【修复】带非 0 退出码（124 = timeout 惯用码），
            // 并注明常见原因：交互式程序（ReadLine/input）等待输入会一直挂起
            return Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.Timeout, $"错误：命令执行超时（>{timeoutSeconds}秒）。常见原因：程序含交互输入（Console.ReadLine/input）或死循环。\n退出码：124");
        }

        // 拿到输出
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        // 拼接
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"退出码：{process.ExitCode}");
        if (!string.IsNullOrWhiteSpace(stdout)) sb.AppendLine("--- stdout ---").AppendLine(stdout);
        if (!string.IsNullOrWhiteSpace(stderr)) sb.AppendLine("--- stderr ---").AppendLine(stderr);

        // 返回
        return Agent.Core.Models.ToolResult.Ok(sb.ToString());
    }

    /// <summary>
    /// Python 解释器归一化：把命令中的 python/python3/pip/pip3 统一到系统 Python 绝对路径，
    /// 避免裸 python 命中豆包沙箱解释器（无第三方包）而裸 pip 命中系统解释器（有包）的错位。
    /// </summary>
    private static string NormalizePython(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return command;

        if (!command.Contains("python", StringComparison.OrdinalIgnoreCase) &&
            !command.Contains("pip", StringComparison.OrdinalIgnoreCase))
            return command;

        var py = ResolveSystemPython();
        if (py is null) return command;

        var quoted = "\"" + py + "\"";

        // 保护 "python -m pip" 中的 pip（-m pip 是参数，不是命令），占位后还原
        const string marker = "\u0001";
        command = System.Text.RegularExpressions.Regex.Replace(
            command, @"-m\s+pip3?\b", marker, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // python / python3 → 系统 Python 绝对路径
        command = System.Text.RegularExpressions.Regex.Replace(
            command, @"\bpython3?\b", quoted, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // 剩余独立 pip / pip3 → 系统 python -m pip
        command = System.Text.RegularExpressions.Regex.Replace(
            command, @"\bpip3?\b", quoted + " -m pip", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // 还原 -m pip
        command = command.Replace(marker, "-m pip");

        return command;
    }

    private static string? _systemPython;

    /// <summary>解析系统 Python 绝对路径（跳过豆包沙箱解释器）。结果缓存。</summary>
    private static string? ResolveSystemPython()
    {
        if (_systemPython is not null) return _systemPython;

        // 1) py launcher：py -c 打印默认解释器的可执行路径
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "py",
                Arguments = "-c \"import sys; print(sys.executable)\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            if (p is not null)
            {
                var exe = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(3000);
                if (p.ExitCode == 0 && !string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
                {
                    _systemPython = exe;
                    return _systemPython;
                }
            }
        }
        catch { /* 忽略，走常见路径探测 */ }

        // 2) 常见安装路径（LocalAppData\Programs\Python\Python3xx）
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var ver in new[] { "Python311", "Python310", "Python312", "Python313", "Python39" })
        {
            var candidate = Path.Combine(local, "Programs", "Python", ver, "python.exe");
            if (File.Exists(candidate))
            {
                _systemPython = candidate;
                return _systemPython;
            }
        }

        return null;
    }

    /// <summary>
    /// 敏感路径源头拒（双根保护）：命令串展开环境变量后提取路径 token，
    /// 归一化判定是否命中敏感区。接受"正则提取"精度（用户确认）——
    /// 误伤边界（如 echo "C:\Windows" 字面输出）以明确提示兜底。
    /// </summary>
    private static string? CheckSensitivePaths(string command)
    {
        // 1) 环境变量展开（%VAR% 形态；cmd 运行时同样展开，此处仅用于路径判定）
        string expanded;
        try { expanded = Environment.ExpandEnvironmentVariables(command); }
        catch { expanded = command; }

        // 2) 提取路径 token：引号包裹路径 / 盘符绝对路径 / UNC 路径（含 \\?\ 前缀形态）
        var pathMatches = System.Text.RegularExpressions.Regex.Matches(
            expanded, "\"[^\"]+\"|\\b[A-Za-z]:\\\\[^ \\t\\n\\r\"]*|\\\\\\\\[^ \\t\\n\\r\"]+");

        foreach (System.Text.RegularExpressions.Match m in pathMatches)
        {
            var token = m.Value.Trim('"');
            if (token.Length == 0) continue;

            var norm = Agent.Infrastructure.Project.PathBoundary.Normalize(token);
            if (string.IsNullOrEmpty(norm)) continue;

            // 非绝对路径 token（相对路径/裸词）跳过——无法归一化到敏感区
            if (!Path.IsPathRooted(norm)) continue;

            if (Agent.Infrastructure.Project.SensitivePathGuard.IsDenied(norm))
            {
                return $"错误：命令涉及敏感路径，禁止执行：{token}\n" +
                       "命令引用了系统区或凭据区路径（内容不得进入上下文）。" +
                       "若仅为字面文本输出，请改写命令避免引用真实路径。\n退出码：126";
            }
        }

        return null;
    }

    /// <summary>
    /// 工程模式路径边界检查（§14-3t）。
    /// 1) 命令含 .. 路径片段（..\ 或 ../）→ 拒绝（防穿越逃逸）。
    /// 2) 显式盘符绝对路径（含引号包裹的空格路径）→ Path.GetFullPath 解析，
    ///    必须位于任务工作区 或 工程根 内，否则拒绝。
    /// 返回 (Code, Message) 表示拒绝；null 表示放行（结构化契约：调用方直接读 Code 包 ToolResult）。
    /// </summary>
    private static (string Code, string Message)? CheckPathBoundary(string command, string workspaceRoot, string projectRoot)
    {
        // 1) 路径穿越
        if (System.Text.RegularExpressions.Regex.IsMatch(command, @"\.\.(\\|/)"))
        {
            return (Agent.Core.Models.ToolErrorCodes.PathEscape, "错误：路径越界（命令含路径穿越 ..），拒绝执行。\n退出码：126");
        }

        // 2) 显式盘符绝对路径：引号包裹的完整路径 或 未加引号的盘符路径
        var pathMatches = System.Text.RegularExpressions.Regex.Matches(
            command, "\"[A-Za-z]:[^\"]*\"|\\b[A-Za-z]:\\\\[^ \t\n\r]*");
        foreach (System.Text.RegularExpressions.Match m in pathMatches)
        {
            var token = m.Value.Trim('"');
            string full;
            try { full = Path.GetFullPath(token); }
            catch { continue; }

            var insideWorkspace = Agent.Infrastructure.Project.PathBoundary.IsInside(workspaceRoot, full);
            var insideProject = Agent.Infrastructure.Project.PathBoundary.IsInside(projectRoot, full);
            if (!insideWorkspace && !insideProject)
            {
                return (Agent.Core.Models.ToolErrorCodes.OutOfBounds, $"错误：路径越界（命令引用工作区/工程根之外的路径）：{token}\n退出码：126");
            }
        }

        return null;
    }

    /// <summary>
    /// 【D1 衍生】项目名幻觉校验（10-10 修正）：命令中引用的 .csproj 以实际 cwd 为基准
    /// 校验存在性——①相对 cwd 直接存在放行；②cwd 递归同名放行（模型常只给文件名）；
    /// ③均不存在 → 拦截并给出 cwd 实际顶层项目提示。cwd 由 ResolveWorkingDirectory 决定
    /// （可写工程=工程根 / 只读工程+沙箱=run 目录），与写工具基准一致，杜绝双基准错位。
    /// </summary>
    public static string? CheckProjectName(string command, string cwd)
    {
        var projMatches = System.Text.RegularExpressions.Regex.Matches(
            command, @"[A-Za-z0-9_.\-]+\.csproj", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (projMatches.Count == 0) return null;

        foreach (System.Text.RegularExpressions.Match m in projMatches)
        {
            // ① 相对 cwd 直接存在
            if (File.Exists(Path.Combine(cwd, m.Value))) continue;

            // ② cwd 递归查找同名（模型常只给文件名，工程可能多层嵌套）
            var name = Path.GetFileName(m.Value);
            bool found;
            try { found = Directory.EnumerateFiles(cwd, name, SearchOption.AllDirectories).Any(); }
            catch { found = false; }
            if (found) continue;

            // ③ 拦截：给出 cwd 实际顶层项目作为提示
            var actualNames = new System.Collections.Generic.List<string>();
            try
            {
                foreach (var f in Directory.EnumerateFiles(cwd, "*.csproj", SearchOption.TopDirectoryOnly))
                    actualNames.Add(Path.GetFileName(f));
            }
            catch { }
            var hint = actualNames.Count > 0 ? string.Join(" / ", actualNames) : "(工作目录未找到 .csproj)";
            return $"错误：项目 {name} 不存在于工作目录。实际项目：{hint}。\n" +
                   "请使用正确的项目名，或直接运行 dotnet run（不带 --project，自动定位）。\n退出码：126";
        }
        return null;
    }

    /// <summary>
    /// 【10-11 ③ 多 csproj 歧义】dotnet run 不带 --project 但 cwd 顶层有 ≥2 个 .csproj →
    /// MSB1011 无法确定启动项目（多文件写任务常见失败：模型在同一目录写了多个项目）。
    /// 进程执行前确定性拦截，附目录内实际项目清单（数据驱动，非提示词，让模型立刻知道选哪个）。
    /// 纯函数可测；命令带 --project/-p/显式 .csproj 或 cwd 项目数 &lt;2 → 放行（null）。
    /// </summary>
    public static string? CheckDotnetRunAmbiguity(string command, string cwd)
    {
        // 仅拦截 dotnet run 形式（含前导空白/大小写）
        if (!System.Text.RegularExpressions.Regex.IsMatch(command,
            @"^\s*dotnet\s+run\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return null;

        // 已显式指定项目 → 不歧义，放行
        if (System.Text.RegularExpressions.Regex.IsMatch(command,
            @"--project\b|\s-p\b|\.csproj", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return null;

        System.Collections.Generic.List<string> projs;
        try
        {
            projs = Directory.EnumerateFiles(cwd, "*.csproj", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(x => x is not null)
                .Select(x => x!)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return null;
        }

        if (projs.Count < 2) return null;
        var list = string.Join("、", projs);
        return $"错误：当前目录包含多个项目文件（{list}），dotnet run 无法确定启动项目。\n" +
               "请用 dotnet run --project <文件名> 指定要运行的项目。\n退出码：125";
    }

    /// <summary>
    /// 命令规范化：把过时的缩写替换成推荐写法。
    /// 主要处理 dotnet 的缩写参数（-p → --project）。
    /// </summary>
    private static string NormalizeCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return command;

        // dotnet run -p xxx  →  dotnet run --project xxx
        command = System.Text.RegularExpressions.Regex.Replace(
            command,
            @"(\bdotnet\s+run\s+)-p\s+",
            "$1--project ",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // dotnet build -p xxx  →  dotnet build --project xxx
        command = System.Text.RegularExpressions.Regex.Replace(
            command,
            @"(\bdotnet\s+build\s+)-p\s+",
            "$1--project ",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // dotnet test -p xxx  →  dotnet test --project xxx
        command = System.Text.RegularExpressions.Regex.Replace(
            command,
            @"(\bdotnet\s+test\s+)-p\s+",
            "$1--project ",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return command;
    }
}