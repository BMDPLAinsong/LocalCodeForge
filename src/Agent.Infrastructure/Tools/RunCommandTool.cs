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

    // 允许执行的命令白名单，按命令的第一个 token 匹配
    private static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "python", "python3", "pip", "pip3",
        "dotnet", "node", "npm", "npx",
        "git", "dir", "ls", "type", "cat",
        "tree", "find", "findstr", "where"
    };

    // 单次命令超时（秒）
    private const int TimeoutSeconds = 30;

    /// <summary>构造函数。</summary>
    public RunCommandTool(string workspaceRoot)
    {
        // 规范化
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    /// <summary>工具名。</summary>
    public override string Name => "run_command";

    /// <summary>工具描述。</summary>
    public override string Description =>
        "在工作区内执行一条命令并返回输出。只允许白名单内的命令（python/pip/dotnet/node/npm/git/dir/ls/type/cat）。";

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
    public override async Task<string> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        // 取出 command 参数
        var command = args.GetProperty("command").GetString()!;

        // 【新增】命令规范化
        command = NormalizeCommand(command);

        // 提取第一个 token 作为命令名
        var firstToken = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];

        // 白名单校验
        if (!AllowedCommands.Contains(firstToken))
            // 【修复】返回体必须带非 0 退出码，否则下游（Orchestrator/AgentLoop）
            // 只认"退出码：N"判定失败，会把拒绝误判成成功
            return $"错误：命令 {firstToken} 不在白名单中。\n退出码：127";

        // 【修复】拒绝 inline 执行——必须在启动进程之前检查
        // （原来放在进程跑完之后，命令实际已经执行过了）
        if (command.Contains(" -c ", StringComparison.OrdinalIgnoreCase) ||
            command.Contains(" -e ", StringComparison.OrdinalIgnoreCase))
        {
            return "错误：出于安全考虑，禁止使用 -c / -e 的内联执行方式。请改用脚本文件。\n退出码：126";
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
            WorkingDirectory = _workspaceRoot,
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
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds), ct);

        // 哪个先完成
        var completed = await Task.WhenAny(waitTask, timeoutTask);

        // 超时则杀进程
        if (completed == timeoutTask)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
            // 【修复】带非 0 退出码（124 = timeout 惯用码），
            // 并注明常见原因：交互式程序（ReadLine/input）等待输入会一直挂起
            return $"错误：命令执行超时（>{TimeoutSeconds}秒）。常见原因：程序含交互输入（Console.ReadLine/input）或死循环。\n退出码：124";
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
        return sb.ToString();
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