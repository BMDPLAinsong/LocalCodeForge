// ============================================================
// OutputVerifier.cs
// run_command 输出判定公共验证器。
// agent（AgentLoop）与 orchestrator（Orchestrator）双路径共用一份实现，
// 根治"只改一条路径"导致的双路径不一致（2026-09-22 曾踩 3 次）。
// 仅收敛"判定逻辑"；失败后的反馈文案由调用方自行拼接（保留两路径差异）。
// 信号集取两路径并集；stderr 阈值统一为"剥离后非空即失败"。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Agent.Infrastructure.Quality;

/// <summary>
/// 输出判定公共验证器（双路径共用，判定逻辑单一来源）。
/// </summary>
public static class OutputVerifier
{
    // ──────────────────────────────────────────────────────────
    // 检查 0.5：外部凭据失败（硬失败，无容错）
    // 两路径同构：AgentLoop 检查 0.5 / Orchestrator 检查 0.5
    // ──────────────────────────────────────────────────────────
    private static readonly string[] CredentialPatterns =
    {
        @"\b401\b", @"\b403\b",
        "unauthorized", "invalid api key", "api key", "api_key", "appid",
        "无效的密钥", "密钥无效", "认证失败", "授权失败"
    };

    public static bool HasCredentialFailure(string output, out string pattern)
    {
        foreach (var p in CredentialPatterns)
        {
            if (Regex.IsMatch(output, p, RegexOptions.IgnoreCase))
            {
                pattern = p;
                return true;
            }
        }

        pattern = "";
        return false;
    }

    // ──────────────────────────────────────────────────────────
    // 检查 2：stderr 无害警告剥离（先剥离再判定，避免成功程序被误杀）
    // RequestsDependencyWarning 整块剥离（含其后缩进的 warnings.warn( 延续行）；
    // NETSDK 提示替换；warning/警告 行过滤。
    // 调用方判定：剥离后非空即失败。
    // ──────────────────────────────────────────────────────────
    public static string StripHarmlessWarnings(string stderr)
    {
        var text = stderr;

        // requests 库版本兼容性提示（写入 stderr 但无害），整块剥离
        text = Regex.Replace(
            text,
            @"(?m)^.*RequestsDependencyWarning[^\r\n]*(?:\r?\n[ \t]+[^\r\n]*)*",
            "",
            RegexOptions.IgnoreCase);

        // 先整行过滤警告行（以 "warning" / "警告" 开头的行，NETSDK 提示也以 warning 开头）。
        // 注意顺序：必须先过滤行再做词替换，否则 "warning NETSDK1174" 会被 Replace
        // 破坏行首导致行过滤失效（原 Orchestrator 实现的顺序缺陷，抽取时修正）。
        var lines = text
            .Split('\n')
            .Where(l =>
                !l.TrimStart().StartsWith("warning", StringComparison.OrdinalIgnoreCase) &&
                !l.TrimStart().StartsWith("警告", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        text = string.Join("\n", lines);

        // 再去掉可忽略的编译类警告词（NETSDK 内联残留，非行首场景）
        foreach (var w in new[] { "警告 NETSDK1174", "warning NETSDK" })
        {
            text = text.Replace(w, "", StringComparison.OrdinalIgnoreCase);
        }

        return text.Trim();
    }

    // ──────────────────────────────────────────────────────────
    // 检查 3：stdout 结构性错误（程序崩溃 / 编译失败）
    // 信号集取两路径并集（AgentLoop 原有 16 项 + Orchestrator 独有
    // ZeroDivisionError / FileNotFoundError / ReferenceError /
    // does not exist / not exists / No such file / 错误 CS / 错误 MSB / 错误 NETSDK）
    // ──────────────────────────────────────────────────────────
    private static readonly string[] StructuralSignals =
    {
        // Python 崩溃
        "Traceback",
        "NameError", "ImportError", "ModuleNotFoundError",
        "AttributeError", "SyntaxError", "TypeError",
        "ZeroDivisionError", "FileNotFoundError", "ReferenceError",
        "is not defined", "no attribute", "cannot import",

        // C# 编译错误
        "error CS", "error MSB", "error NETSDK",
        "错误 CS", "错误 MSB", "错误 NETSDK",

        // 未捕获异常
        "Unhandled exception",

        // 文件不存在
        "does not exist", "not exists",
        "No such file",

        // 中文结构性错误
        "找不到", "不存在"
    };

    public static bool HasStructuralError(string stdout, out string signal)
    {
        foreach (var s in StructuralSignals)
        {
            if (stdout.Contains(s, StringComparison.OrdinalIgnoreCase))
            {
                signal = s;
                return true;
            }
        }

        signal = "";
        return false;
    }

    // ──────────────────────────────────────────────────────────
    // 检查 3.5：通用失败词 + JSON 错误字段 + 双容错
    // 收敛 AgentLoop 检查 3.5（通用词 + JSON + success 容错，无 expected）
    // 与 Orchestrator 检查 3.5 / 3.5b（查找类 + expected/success 双容错）。
    // expectedOutput 传 null 表示无预期文本（agent 路径）。
    // 查找类信号（未找到/not found/not exist）命中即失败，
    // 除非预期文本同样包含（演示查找失败场景）。
    // ──────────────────────────────────────────────────────────
    private static readonly string[] LookupFailSignals =
    {
        "未找到", "not found", "not exist"
    };

    private static readonly string[] GenericFailHits =
    {
        "失败", "failed", "错误:"
    };

    private static readonly string[] GenericFailRegexes =
    {
        @"\berror\b"
    };

    private static readonly string[] JsonErrorFields =
    {
        "\"error\"", "\"error_"
    };

    public static bool HasGenericFailure(string stdout, string? expectedOutput, out string reason)
    {
        // 查找类：命中即失败（除非预期同样含该信号）
        foreach (var s in LookupFailSignals)
        {
            if (stdout.Contains(s, StringComparison.OrdinalIgnoreCase) &&
                !(expectedOutput != null &&
                  expectedOutput.Contains(s, StringComparison.OrdinalIgnoreCase)))
            {
                reason = s;
                return true;
            }
        }

        // 通用失败词 + JSON 错误字段
        var hasFail =
            GenericFailHits.Any(s => stdout.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
            GenericFailRegexes.Any(p => Regex.IsMatch(stdout, p, RegexOptions.IgnoreCase)) ||
            JsonErrorFields.Any(f => stdout.Contains(f, StringComparison.OrdinalIgnoreCase));

        if (!hasFail)
        {
            reason = "";
            return false;
        }

        // 容错 1：预期文本含失败词（演示失败场景，如"取款失败"样例）
        var expectedHasFail = expectedOutput != null &&
            (GenericFailHits.Any(s => expectedOutput.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
             GenericFailRegexes.Any(p => Regex.IsMatch(expectedOutput, p, RegexOptions.IgnoreCase)) ||
             JsonErrorFields.Any(f => expectedOutput.Contains(f, StringComparison.OrdinalIgnoreCase)));

        // 容错 2：stdout 含成功信号（正常报告，如"失败 0 条，成功 5 条"）
        var hasSuccess = stdout.Contains("成功", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(stdout, @"\b(success|completed)\b", RegexOptions.IgnoreCase);

        if (expectedHasFail || hasSuccess)
        {
            reason = "";
            return false;
        }

        reason = "失败/failed/error";
        return true;
    }

    // ──────────────────────────────────────────────────────────
    // 检查 4.1：占位 API Key（AgentLoop 检查 4.1 使用；
    // Orchestrator 由凭据检查 0.5 覆盖，可不调用）
    // ──────────────────────────────────────────────────────────
    private static readonly string[] PlaceholderKeyPatterns =
    {
        "YOUR_API_KEY", "YOUR_OPENWEATHERMAP_API_KEY", "YOUR_KEY",
        "API_KEY = \"", "api_key = \"", "你的密钥"
    };

    public static bool HasPlaceholderKey(IReadOnlyList<string> stdoutLines, out string pattern)
    {
        foreach (var line in stdoutLines)
        {
            foreach (var p in PlaceholderKeyPatterns)
            {
                if (line.Contains(p, StringComparison.OrdinalIgnoreCase))
                {
                    pattern = p;
                    return true;
                }
            }
        }

        pattern = "";
        return false;
    }

    // ──────────────────────────────────────────────────────────
    // 检查 4：占位符 / 模板输出（两路径同构）
    // ──────────────────────────────────────────────────────────
    public static bool IsPlaceholderOutput(IReadOnlyList<string> stdoutLines, out int count, out int total)
    {
        total = stdoutLines.Count;

        // 数据特征计数（P2-1 校准：冒号/等号结尾的行不计入——
        // "标题: + 数据行"（如 fib "Fibonacci 序列:\n0 1 1 2 3"）是合法输出结构，
        // 原实现把标题行计入导致恰好 50% 阈值误判为占位）。
        count = stdoutLines.Count(l =>
            l.Contains('?') ||
            l.Contains("N/A") ||
            l.Contains("None") ||
            l.Contains("Unknown") ||
            l.Contains("未知") ||
            l.Contains("null") ||
            l.Contains("undefined"));

        if (count >= total / 2.0)
            return true;

        // 纯标题壳：所有行都以 : 或 = 结尾（"结果:" 单行 / "订单:\n客户:" 全空标题）
        return total > 0 && stdoutLines.All(l => l.EndsWith(':') || l.EndsWith('='));
    }

    public static bool IsTemplateOutput(IReadOnlyList<string> stdoutLines)
    {
        if (stdoutLines.Count != 1)
            return false;

        var line = stdoutLines[0].ToLowerInvariant();

        return line.StartsWith("hello") ||
            line == "success" ||
            line == "done" ||
            line == "ok" ||
            line.StartsWith("程序运行成功") ||
            line.StartsWith("运行成功") ||
            line.StartsWith("example") ||
            line.StartsWith("示例");
    }

    /// <summary>
    /// 模板检测降级（方案 1）：stdout 单行内容被任务输入/预期输出明确包含时，
    /// 视为任务要求的合法输出（如任务要求"内容为 hello"，type 输出 hello 是正确结果），
    /// 豁免 IsTemplateOutput 判定——避免"创建 hello 文件"被误判模板而死循环。
    /// 任务无关的 Hello World 糊弄（如任务要算加法、输出 hello）仍会被模板检测拦截。
    /// </summary>
    public static bool IsTaskExpectedOutput(string expectedText, IReadOnlyList<string> stdoutLines)
    {
        if (string.IsNullOrWhiteSpace(expectedText))
            return false;

        var expectedNorm = expectedText.ToLowerInvariant();
        foreach (var line in stdoutLines)
        {
            var trimmed = line.Trim().ToLowerInvariant();
            if (trimmed.Length > 0 && expectedNorm.Contains(trimmed))
                return true;
        }
        return false;
    }
}
