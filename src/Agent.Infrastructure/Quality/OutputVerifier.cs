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
    // 检查 3.5：通用失败词 + JSON 错误字段 + 成功证据判定
    // 【10-08 证据链先行（A 方案）】结构收敛：原 4 层容错（expected 含失败词 /
    // 成功信号 / 演示计算行 / 演示查找行）合并为统一的"成功证据"检测器
    // HasSuccessEvidence——语义从"豁免魔法"变为"证据存在性"：
    //   命中软失败信号（失败词/查找信号/JSON 字段）时，
    //   有成功证据 → 边缘成功（不判失败，调用方可标记可疑）；
    //   无成功证据 → 判失败。
    // 行为与历史容错完全等价（tests A/L 区块 20 断言逐条核对），
    // 规则堆从"4 个并列容错分支"收敛为"1 个证据函数 + 4 个证据形态"。
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

    /// <summary>文本是否含软失败信号（查找类 / 通用失败词 / JSON 错误字段）。expected 例外由调用方处理。</summary>
    private static bool ContainsSoftSignal(string text)
    {
        if (LookupFailSignals.Any(s => text.Contains(s, StringComparison.OrdinalIgnoreCase)))
            return true;
        if (GenericFailHits.Any(s => text.Contains(s, StringComparison.OrdinalIgnoreCase)))
            return true;
        if (GenericFailRegexes.Any(p => Regex.IsMatch(text, p, RegexOptions.IgnoreCase)))
            return true;
        if (JsonErrorFields.Any(f => text.Contains(f, StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    /// <summary>3 参版本（保留签名，tests 反射兼容）：命中软信号且无成功证据 → true（判失败）。</summary>
    public static bool HasGenericFailure(string stdout, string? expectedOutput, out string reason)
    {
        return HasGenericFailure(stdout, expectedOutput, out reason, out _);
    }

    /// <summary>4 参版本（调用方推荐）：多输出 hadEvidence——命中软信号但存在成功证据（边缘成功，不判失败）。
    /// 调用方语义：返回 true → 失败；返回 false 且 hadEvidence → 标记可疑（数据回流，防容错误放被静默吞掉）。</summary>
    public static bool HasGenericFailure(string stdout, string? expectedOutput, out string reason, out bool hadEvidence)
    {
        reason = ""; // 兜底：干净输出/边缘成功默认（各路径按需覆盖，保证编译流分析）
        hadEvidence = false;
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return false;
        }

        // 双路径独立判定（保持历史保守语义）：
        // 路径 1 查找类信号——命中且预期未含该信号时，查"演示查找"证据（证据 1/4）判定；
        // 路径 2 通用失败词/JSON 字段——命中时查"演示计算/成功报告/预期含失败词"证据（证据 1/2/3）判定。
        // 两条路径各自独立：查找豁免只豁免查找信号本身，通用失败词仍独立判定（防叠加信号被吞）。

        // 路径 1：查找类
        var lookupHit = LookupFailSignals.FirstOrDefault(s =>
            stdout.Contains(s, StringComparison.OrdinalIgnoreCase) &&
            !(expectedOutput != null &&
              expectedOutput.Contains(s, StringComparison.OrdinalIgnoreCase)));
        if (lookupHit is not null)
        {
            // 证据：预期含失败信号（演示查找失败场景）或 正向条目形态（演示查找流程）
            var evidence = (expectedOutput != null && ContainsSoftSignal(expectedOutput)) ||
                HasDemoLookupSuccess(stdout);
            if (!evidence)
            {
                reason = lookupHit;
                return true;
            }
            hadEvidence = true;
            reason = lookupHit; // 边缘成功保留信号描述（供调用方标记/落库）
            // 查找豁免成立，继续检查通用词路径（保守：不因查找豁免吞掉其他失败词）
        }

        // 路径 2：通用失败词 + JSON 错误字段
        var genericHit = GenericFailHits.Any(s => stdout.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
            GenericFailRegexes.Any(p => Regex.IsMatch(stdout, p, RegexOptions.IgnoreCase)) ||
            JsonErrorFields.Any(f => stdout.Contains(f, StringComparison.OrdinalIgnoreCase));
        if (genericHit)
        {
            // 证据：预期含失败信号 / 成功计数报告 / 演示计算程序
            var evidence = (expectedOutput != null && ContainsSoftSignal(expectedOutput)) ||
                Regex.IsMatch(stdout, @"成功\s*\d", RegexOptions.IgnoreCase) ||
                (HasDemoCalcLine(stdout) && HasDemoFailMessage(stdout));
            if (!evidence)
            {
                reason = "失败/failed/error";
                return true;
            }
            hadEvidence = true;
            reason = "失败/failed/error"; // 边缘成功保留信号描述（供调用方标记/落库）
        }

        // 无软信号或无证据 → 干净输出；reason 已在开头兜底为 ""
        // （hadEvidence=true 时 reason 已在各路径覆盖为信号描述，不得再清空）。
        return false;
    }

    /// <summary>
    /// 成功证据检测（【10-08 A 方案】统一证据层）：
    /// 判断 stdout 是否呈现"程序正常运行 + 业务分支"的证据形态（边缘成功）。
    /// 供调用方在 hasEvidence=true 时标记可疑；证据形态与历史容错逐条等价。
    /// </summary>
    public static bool HasSuccessEvidence(string stdout, string? expectedOutput)
    {
        if (expectedOutput != null && ContainsSoftSignal(expectedOutput))
            return true;
        if (Regex.IsMatch(stdout, @"成功\s*\d", RegexOptions.IgnoreCase))
            return true;
        if (HasDemoCalcLine(stdout) && HasDemoFailMessage(stdout))
            return true;
        if (HasDemoLookupSuccess(stdout))
            return true;
        return false;
    }

    /// <summary>演示程序的成功计算行：`数字 op 数字 = 数字`（如 `10 + 5 = 15` / `15 / 3 = 5.0`）。</summary>
    private static bool HasDemoCalcLine(string stdout) =>
        Regex.IsMatch(stdout,
            @"\b\d+(?:\.\d+)?\s*[+\-*/%]\s*\d+(?:\.\d+)?\s*=\s*-?\d+(?:\.\d+)?\b",
            RegexOptions.IgnoreCase);

    /// <summary>演示程序的异常分支消息行：业务错误消息形态（中文 错误/失败 或 英文 error/fail + 冒号 + 具体内容），
    /// 排除「错误: 0 / 失败: 0 条」等否定/计数形态（0 后无描述）。10-08 A' 放宽：不再要求算式前缀
    /// （`数字 op 数字 -> 错误`），真实模型演示程序常输出独立错误消息行。</summary>
    private static bool HasDemoFailMessage(string stdout) =>
        Regex.IsMatch(stdout,
            @"(?:错误|失败)[:：](?!\s*0\b)\s*\S|(?:\berror\b|\bfail\w*)[:：](?!\s*0\b)\s*\S",
            RegexOptions.IgnoreCase);

    /// <summary>演示程序的查找流程正向形态：stdout 含"找到"词 / ID 列表条目 / 成功操作词
    /// （如"找到图书: 数据结构与算法"、"ID: 1, 标题:..."、"已添加 3 本图书"）。
    /// 说明"未找到"行是演示分支而非孤立失败。10-08 新增：LookupFailSignals 演示容错（容错 3b）。
    /// 注意排除否定前缀陷阱：`(?<!未)找到` 拒绝"未找到"的"找到"、"not found" 由 `(?<!not\s)found` 拒绝
    /// ——否则正向形态永远命中（"未找到"本身含"找到"、not found 本身含 found），豁免形同虚设。</summary>
    private static bool HasDemoLookupSuccess(string stdout) =>
        Regex.IsMatch(stdout, @"(?<!未)找到", RegexOptions.IgnoreCase) ||
        Regex.IsMatch(stdout, @"(?<!not\s)found", RegexOptions.IgnoreCase) ||
        Regex.IsMatch(stdout, @"ID\s*[:：]", RegexOptions.IgnoreCase) ||
        stdout.Contains("已添加", StringComparison.OrdinalIgnoreCase) ||
        stdout.Contains("已成功", StringComparison.OrdinalIgnoreCase) ||
        stdout.Contains("成功", StringComparison.OrdinalIgnoreCase) ||
        stdout.Contains("added", StringComparison.OrdinalIgnoreCase) ||
        stdout.Contains("removed", StringComparison.OrdinalIgnoreCase);

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
    /// 模板检测降级（方案 1+修正 C）：stdout 单行内容被任务输入/预期输出明确包含时，
    /// 视为任务要求的合法输出（如任务要求"打印 hello world"，type 输出 Hello, World! 是正确结果），
    /// 豁免 IsTemplateOutput 判定——避免"创建 hello 文件"被误判模板而死循环。
    /// 比较前归一化（小写 + 非字母数字折叠为空白）——"Hello, World!" ↔ "hello world" 匹配。
    /// 任务无关的 Hello World 糊弄（如任务要算加法、输出 hello）仍会被模板检测拦截。
    /// </summary>
    public static bool IsTaskExpectedOutput(string expectedText, IReadOnlyList<string> stdoutLines)
    {
        if (string.IsNullOrWhiteSpace(expectedText))
            return false;

        var expectedNorm = NormalizeForMatch(expectedText);
        foreach (var line in stdoutLines)
        {
            var trimmed = NormalizeForMatch(line);
            if (trimmed.Length > 0 && expectedNorm.Contains(trimmed))
                return true;
        }
        return false;
    }

    /// <summary>归一化：小写 + 非字母数字折叠为空白 + 折叠连续空白。中文保留。供文件内容符合性检查复用。</summary>
    public static string NormalizeForMatch(string s)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;

        var sb = new System.Text.StringBuilder(s.Length);
        var prevSpace = true;
        foreach (var ch in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                prevSpace = false;
            }
            else if (!prevSpace)
            {
                sb.Append(' ');
                prevSpace = true;
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// 【P0-2】严格模式：expected 的每一非空行（归一化后）都必须在 stdout 行集（归一化）中
    /// 精确匹配（整行相等）。与宽松模式（任一 stdout 行被 expected 包含）互补：
    /// 严格模式用于要求全部预期输出逐项出现的场景（工程改动验证等），消除"预期缺失仍判成功"。
    /// 空 expected 返回 false（无预期文本不适用严格判定）。
    /// </summary>
    public static bool IsTaskExpectedOutputStrict(string expectedText, IReadOnlyList<string> stdoutLines)
    {
        if (string.IsNullOrWhiteSpace(expectedText))
            return false;

        var stdoutNorm = stdoutLines
            .Select(NormalizeForMatch)
            .Where(s => s.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var rawLine in expectedText.Split('\n'))
        {
            var lineNorm = NormalizeForMatch(rawLine);
            if (lineNorm.Length == 0)
                continue;
            if (!stdoutNorm.Contains(lineNorm))
                return false;
        }
        return true;
    }
}
