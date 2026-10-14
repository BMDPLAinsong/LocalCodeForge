// ============================================================
// Execution/ProcessRunner.cs
// 进程执行单一原语（10-14 P4-16，落地"统一执行链路"铁律）：
//   全库所有"启动子进程并读回 stdout/stderr + 超时终止"的代码走此处一处实现。
//   背景：run_command（RunCommandTool.InvokeAsync）与测试执行（TestRunnerService.RunAsync）
//   原各持一份手写实现（ProcessStartInfo + 双流读取 + Task.WhenAny/CTS 超时 + Kill 进程树），
//   两份独立演进 = 同一套超时/编码/终止语义出现两个口径（实测差异：run_command 固定 30/90s，
//   测试执行由调用方给 150s；前者走 cmd.exe+UTF-8 代码页，后者直连进程）。
//   边界：**只抽取执行原语，不合并策略**——白名单/敏感区拒/路径边界/工作目录基准/超时值/
//   输出文本格式仍各调用方自理（合并策略会改变行为：例如白名单不含 pytest、
//   cwd 基准 run_command 是 run 目录而测试执行是工程根）。
//   覆盖面：**全部"重定向双流 + 等待退出 + 终止进程树"的执行**已归一（run_command / 测试执行 /
//   ruff check / py_compile）。**两处显式例外**（有意保留手写，不属命令执行链）：
//     ①可用性探测——`ruff --version`（PythonLintService）与 `py -c print(sys.executable)`
//       （RunCommandTool.ResolveSystemPython）：同步 3s 硬等、只判存在性/取单行，无需异步原语；
//     ② 非命令类进程启动——浏览器打开（UseShellExecute）与 ollama serve 长驻守护
//       （CLI，须脱离 CLI 生命周期）。
//   语义约定（两个原调用方逐字对齐，见各自调用点注释）：
//     · 超时 或 外部取消 → Kill(entireProcessTree) → 返回 TimedOut=true（不抛异常）；
//       原 TestRunnerService 捕获 OCE 后返回"测试超时"，原 RunCommandTool 经 WhenAny
//       竞态路径同样返回超时失败——两者对外表现与本实现一致。
//     · 输出解码统一 UTF-8：原 TestRunnerService 未显式设置（.NET Core Encoding.Default
//       即 UTF-8），原 RunCommandTool 显式 UTF-8——统一为显式 UTF-8 无行为差异。
//     · TimedOut 时返回已读到的部分输出（读任务以 SafeRead 收敛，避免悬挂任务泄漏）。
//   纯确定性 IO，无 LLM 调用。
// ============================================================
using System.Diagnostics;
using System.Text;

namespace Agent.Infrastructure.Execution;

/// <summary>子进程执行结果（TimedOut=true 时 ExitCode 无意义，输出为已读到的部分）。</summary>
public sealed record ProcessRunResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);

/// <summary>进程执行单一原语（重定向 stdout/stderr + 超时/取消终止进程树 + 输出回收）。</summary>
public static class ProcessRunner
{
    /// <summary>执行进程至退出或超时。psi 的重定向/无窗口/编码由本方法统一设置（调用方无需重复设）。
    /// timeoutMs ≤ 0 视为不限时（仍受 ct 取消影响）。</summary>
    public static async Task<ProcessRunResult> RunAsync(ProcessStartInfo psi, int timeoutMs, CancellationToken ct)
    {
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        psi.StandardOutputEncoding = utf8;
        psi.StandardErrorEncoding = utf8;

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        // 双流并发读取（不等退出才读——大输出管道写满会死锁）
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs > 0) waitCts.CancelAfter(timeoutMs);

        try
        {
            await proc.WaitForExitAsync(waitCts.Token);
        }
        catch (OperationCanceledException)
        {
            // 超时或外部取消 → 杀进程树，回收已产生的部分输出（不抛异常，交调用方按超时语义处理）
            try { proc.Kill(entireProcessTree: true); } catch { /* 进程已退出 */ }
            return new ProcessRunResult(-1, await ReadSafeAsync(stdoutTask), await ReadSafeAsync(stderrTask), TimedOut: true);
        }

        return new ProcessRunResult(
            proc.ExitCode,
            await ReadSafeAsync(stdoutTask),
            await ReadSafeAsync(stderrTask),
            TimedOut: false);
    }

    /// <summary>进程被终止时读任务可能以异常/取消收尾——收敛为部分输出，不让异常掩盖终止语义。</summary>
    private static async Task<string> ReadSafeAsync(Task<string> readTask)
    {
        try { return await readTask; }
        catch { return ""; }
    }
}