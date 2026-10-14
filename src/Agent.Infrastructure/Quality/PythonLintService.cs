// ============================================================
// PythonLintService.cs
// Python 静态分析服务。优先用 ruff，未安装时降级到 py_compile。
// ============================================================

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Agent.Infrastructure.Quality;

/// <summary>
/// Python Lint 服务。
/// 优先用 ruff（快、规则全），未安装时降级到 python -m py_compile。
/// </summary>
public sealed class PythonLintService : ILintService
{
    private readonly ILogger<PythonLintService> _logger;
    private readonly bool _ruffAvailable;

    public PythonLintService(ILogger<PythonLintService> logger)
    {
        _logger = logger;
        _ruffAvailable = CheckRuffAvailable();

        if (!_ruffAvailable)
        {
            _logger.LogWarning(
                "未检测到 ruff，降级到 py_compile。建议安装：pip install ruff");
        }
    }

    /// <summary>检查 ruff 是否可用。</summary>
    private bool CheckRuffAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ruff",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is null) return false;
            process.WaitForExit(3000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>检查一批文件。</summary>
    public async Task<LintResult> LintAsync(
        string workspaceRoot,
        IReadOnlyList<string> filePaths,
        string language,
        CancellationToken ct)
    {
        // 只处理 Python 文件
        var pyFiles = filePaths
            .Where(p => p.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (pyFiles.Count == 0)
            return LintResult.Success();

        return _ruffAvailable
            ? await LintWithRuffAsync(workspaceRoot, pyFiles, ct)
            : await LintWithPyCompileAsync(workspaceRoot, pyFiles, ct);
    }

    // ─────────── ruff 路径 ───────────

    // 【10-14 P4-16】执行超时（毫秒）——原实现无超时，仅靠 ct；卡死的 ruff/py_compile 会无限等待。
    private const int RuffTimeoutMs = 60_000;
    private const int PyCompileTimeoutMs = 30_000;

    private async Task<LintResult> LintWithRuffAsync(
        string workspaceRoot,
        List<string> pyFiles,
        CancellationToken ct)
    {
        // 关键规则集：
        //   F821 - 未定义名（作用域外引用、漏 import）
        //   F811 - 重复定义
        //   F841 - 未使用变量
        //   F401 - 未使用 import
        // 语法错误 ruff 默认检测，不需要 select
        var fileArgs = string.Join(" ", pyFiles.Select(f => $"\"{f}\""));

        var psi = new ProcessStartInfo
        {
            FileName = "ruff",
            Arguments = $"check --select F821,F811,F841,F401 --output-format=json --no-fix {fileArgs}",
            WorkingDirectory = workspaceRoot
        };

        // 【10-14 P4-16 统一执行链路】改走 Execution.ProcessRunner 单一原语（原为此处第三份手写
        // 执行实现）。**顺带补上原先完全缺失的超时**：原实现只靠 ct，ruff 卡死即无限等待。
        var run = await Agent.Infrastructure.Execution.ProcessRunner.RunAsync(psi, RuffTimeoutMs, ct);
        if (run.TimedOut)
        {
            _logger.LogWarning("ruff 执行超时（>{RuffTimeoutMs}ms），按工具异常不阻断流程", RuffTimeoutMs);
            return LintResult.Success();
        }

        var stdout = run.Stdout;
        var stderr = run.Stderr;

        // 退出码：0 = 无错误；1 = 有 lint 错误；2 = 工具异常
        if (run.ExitCode == 0)
            return LintResult.Success(stdout);

        if (run.ExitCode == 2)
        {
            _logger.LogWarning("ruff 执行异常：{Stderr}", stderr);
            return LintResult.Success();   // 工具错误不阻断流程
        }

        var errors = ParseRuffOutput(stdout);
        return LintResult.Fail(errors, stdout);
    }

    /// <summary>解析 ruff 的 JSON 输出。</summary>
    private static List<LintError> ParseRuffOutput(string json)
    {
        var result = new List<LintError>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var code = item.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
                var message = item.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                var filename = item.TryGetProperty("filename", out var f) ? f.GetString() ?? "" : "";

                var line = 0;
                var col = 0;
                if (item.TryGetProperty("location", out var loc))
                {
                    if (loc.TryGetProperty("row", out var r)) line = r.GetInt32();
                    if (loc.TryGetProperty("column", out var cl)) col = cl.GetInt32();
                }

                result.Add(new LintError(filename, line, col, code, message));
            }
        }
        catch
        {
            // JSON 解析失败，返回空
        }

        return result;
    }

    // ─────────── py_compile 兜底路径 ───────────

    private async Task<LintResult> LintWithPyCompileAsync(
        string workspaceRoot,
        List<string> pyFiles,
        CancellationToken ct)
    {
        var errors = new List<LintError>();
        var allOutput = new StringBuilder();

        foreach (var file in pyFiles)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"-m py_compile \"{file}\"",
                WorkingDirectory = workspaceRoot
            };

            // 【10-14 P4-16】同走 ProcessRunner 单一原语；补上原实现缺失的超时（单文件编译 30s 足够）。
            var run = await Agent.Infrastructure.Execution.ProcessRunner.RunAsync(psi, PyCompileTimeoutMs, ct);
            if (run.TimedOut)
            {
                _logger.LogWarning("py_compile 超时（>{PyCompileTimeoutMs}ms）：{File}", PyCompileTimeoutMs, file);
                continue;
            }

            var stderr = run.Stderr;

            if (run.ExitCode != 0)
            {
                allOutput.AppendLine(stderr);
                var err = ParsePyCompileError(file, stderr);
                if (err is not null) errors.Add(err);
            }
        }

        return errors.Count == 0
            ? LintResult.Success(allOutput.ToString())
            : LintResult.Fail(errors, allOutput.ToString());
    }

    /// <summary>解析 py_compile 的错误输出。</summary>
    private static LintError? ParsePyCompileError(string file, string stderr)
    {
        var lineMatch = Regex.Match(stderr, @"line (\d+)");
        var line = lineMatch.Success ? int.Parse(lineMatch.Groups[1].Value) : 0;

        var errMatch = Regex.Match(
            stderr,
            @"(SyntaxError|IndentationError|TabError|ValueError|NameError):\s*(.+)");

        var rule = errMatch.Success ? errMatch.Groups[1].Value : "SyntaxError";
        var message = errMatch.Success ? errMatch.Groups[2].Value.Trim() : "语法错误";

        return new LintError(file, line, 0, rule, message);
    }
}