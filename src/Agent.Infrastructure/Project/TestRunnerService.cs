// ============================================================
// TestRunnerService.cs
// 工程级测试自动执行（完成门升级）：确定性检测并运行测试命令，
// 结果回填给模型收尾。无 LLM、纯 Process 执行，可测。
// ============================================================

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Agent.Infrastructure.Project;

/// <summary>测试运行结果。</summary>
public sealed record TestRunResult(int? ExitCode, string Summary, string Output, bool TimedOut);

/// <summary>
/// 工程测试检测与执行。
/// </summary>
public static class TestRunnerService
{
    // ── 【10-14 P0 词边界】csproj 名判定收紧：子串 Contains("Test") 会误报 Contestant/MyTester/Latest
    // 等非测试工程（测试门是失败判定依据，误报会错杀正常工程）。Test/Tests 须为独立词（start/分隔符
    // .-_ 空格 之后）或 PascalCase 段（小写→大写 hump，如 UnitTests/MyProjectTests）；词尾须为
    // end/分隔符/大写或数字。全大写（TESTS.csproj）不命中——罕见形态，宁漏检不误报。
    // 【10-14 P1-5】原 TestProjectDetector.HasTests（经检索确认生产零调用）已随 P1-5 删除，
    // 此前"测试门双出口"的历史债就此收敛——本类 DetectTest 为测试入口唯一判定（9/22 教训落点）。
    private static readonly Regex TestWordRe = new(
        @"(?:(?<=^|[._\-\s])[Tt]ests?|(?<=[a-z0-9])Tests?)(?=$|[._\-\s]|(?=[A-Z0-9]))",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>csproj 文件名（不含扩展名）是否为测试工程名：Test/Tests 为独立词或 PascalCase 段。</summary>
    public static bool IsTestProjectName(string? nameWithoutExtension) =>
        !string.IsNullOrEmpty(nameWithoutExtension) && TestWordRe.IsMatch(nameWithoutExtension);

    /// <summary>
    /// 检测工程的测试命令。任一命中即返回 (命令, 参数, 显示名)；无测试入口返回 null。
    /// 优先级：
    ///  1. *.Tests.csproj 含 xunit/nunit/mstest → dotnet test &lt;csproj&gt;
    ///  2. *.Tests.csproj（console 断言形态，无测试框架包）→ dotnet run --project &lt;csproj&gt;
    ///  3. pytest.ini / pyproject.toml 含 pytest → pytest -q
    ///  4. package.json 含 "test" script → npm test --silent
    /// </summary>
    public static (string Command, string Args, string Display)? DetectTest(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            return null;

        try
        {
            // 1) 测试工程（.csproj 文件名含 Test）
            var csprojs = Directory.GetFiles(projectRoot, "*.csproj", SearchOption.AllDirectories)
                .Where(p => IsTestProjectName(Path.GetFileNameWithoutExtension(p)))
                .ToList();
            foreach (var p in csprojs)
            {
                var content = File.ReadAllText(p);
                if (content.Contains("xunit", StringComparison.OrdinalIgnoreCase)
                    || content.Contains("nunit", StringComparison.OrdinalIgnoreCase)
                    || content.Contains("mstest", StringComparison.OrdinalIgnoreCase))
                {
                    return ("dotnet", $"test \"{p}\" --no-restore", $"dotnet test {Path.GetFileName(p)}");
                }
            }
            // console 断言形态：第一个 *.Tests.csproj
            if (csprojs.Count > 0)
            {
                var p0 = csprojs[0];
                return ("dotnet", $"run --project \"{p0}\"", $"dotnet run {Path.GetFileName(p0)}");
            }

            // 2) pytest
            if (File.Exists(Path.Combine(projectRoot, "pytest.ini")))
                return ("pytest", "-q", "pytest -q");
            var pyproject = Path.Combine(projectRoot, "pyproject.toml");
            if (File.Exists(pyproject) && File.ReadAllText(pyproject).Contains("pytest", StringComparison.OrdinalIgnoreCase))
                return ("pytest", "-q", "pytest -q");

            // 3) package.json test script
            var pkg = Path.Combine(projectRoot, "package.json");
            if (File.Exists(pkg) && File.ReadAllText(pkg).Contains("\"test\"", StringComparison.Ordinal))
                return ("npm", "test --silent", "npm test");

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 运行测试。超时杀死进程；退出码非 0 视为失败。
    /// Summary 为一行摘要（退出码 / 超时 / 关键输出行）。
    /// </summary>
    public static async Task<TestRunResult> RunAsync(string projectRoot, int timeoutMs, CancellationToken ct)
    {
        var detect = DetectTest(projectRoot);
        if (detect is null)
            return new TestRunResult(null, "未检测到测试命令", "", false);

        var (command, args, display) = detect.Value;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = args,
                WorkingDirectory = projectRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = new Process { StartInfo = psi };
            var outBuilder = new StringBuilder();
            var errBuilder = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) outBuilder.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) errBuilder.AppendLine(e.Data); };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                var partial = TrimTail(outBuilder.ToString());
                return new TestRunResult(null, $"测试超时（>{timeoutMs / 1000}s），已终止。", partial, true);
            }

            var stdout = TrimTail(outBuilder.ToString());
            var stderr = TrimTail(errBuilder.ToString());
            var output = stdout;
            if (!string.IsNullOrWhiteSpace(stderr))
                output += "\n--- stderr ---\n" + stderr;

            var summary = Summarize(proc.ExitCode, stdout, stderr, display);
            return new TestRunResult(proc.ExitCode, summary, output, false);
        }
        catch (Exception ex)
        {
            return new TestRunResult(null, $"测试执行失败：{ex.Message}", "", false);
        }
    }

    private static string Summarize(int exitCode, string stdout, string stderr, string display)
    {
        var lines = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Contains("passed", StringComparison.OrdinalIgnoreCase)
                     || l.Contains("failed", StringComparison.OrdinalIgnoreCase)
                     || l.Contains("错误", StringComparison.OrdinalIgnoreCase)
                     || l.Contains("结果:", StringComparison.OrdinalIgnoreCase))
            .Take(4)
            .ToList();

        var head = lines.Count > 0
            ? string.Join(" | ", lines)
            : (string.IsNullOrWhiteSpace(stdout) ? "" : stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "");

        return $"测试 {display}：退出码 {exitCode}" + (head.Length > 0 ? $"；{head}" : "");
    }

    private static string TrimTail(string s, int max = 6000)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
        return s[^max..];
    }
}
