// ============================================================
// FailureCategorizer.cs
// 【3】失败类别自动归类：task_metrics 落库时按 error_summary 词表自动打
// failure_category 列（数据结构性，无模型）。与 OutputVerifier 信号集对齐，
// 另加工具/任务级信号。优先级从高到低，命中即返回。
// 让失败归因可聚合（stats 按类别分布 + 3B 调权可按类别），不再靠人读原文。
// ============================================================

namespace Agent.Infrastructure.Metrics;

/// <summary>失败类别判定（纯静态、确定性、可测）。</summary>
public static class FailureCategorizer
{
    /// <summary>按 error_summary 判定失败类别（按优先级从高到低命中即返回）。</summary>
    public static string Categorize(string errorSummary)
    {
        if (string.IsNullOrWhiteSpace(errorSummary))
            return "other";

        var s = errorSummary;

        // 外部凭据：401/403/api_key/unauthorized
        if (ContainsAny(s, "401", "403", "api_key", "api key", "unauthorized", "credential", "invalid key"))
            return "external_credential";

        // 编译错误：C# / MSBuild / .NET SDK
        if (ContainsAny(s, "error CS", "错误 CS", "error MSB", "错误 MSB", "error NETSDK", "错误 NETSDK",
            "编译错误", "生成失败"))
            return "compile_error";

        // 运行崩溃：Python Traceback / 未捕获异常
        if (ContainsAny(s, "Traceback", "ZeroDivisionError", "NameError", "TypeError",
            "SyntaxError", "ImportError", "ModuleNotFoundError", "AttributeError",
            "FileNotFoundError", "Unhandled exception", "is not defined", "no attribute"))
            return "runtime_error";

        // 超时
        if (ContainsAny(s, "超时", "timeout", "退出码：124", "退出码: 124"))
            return "timeout";

        // 写任务熔断：写了文件但验证未通过（H2，防假成功误伤态——目标可能已落地）
        if (ContainsAny(s, "写入文件但验证未通过"))
            return "write_unverified";

        // 写任务熔断：要求写但未产生任何写入（H4 只读循环）
        if (ContainsAny(s, "未产生任何写入"))
            return "write_not_attempted";

        // 完成门：显式目标不存在（写了别处 ≠ 用户目标，假完成嫌疑）
        if (ContainsAny(s, "但该目标不存在", "与用户指定不符"))
            return "target_mismatch";

        // 工具拒绝：拦截类（白名单/越界/P-D 类型冲突/只读/空内容/交互/项目文件/完成门写被拒）
        if (ContainsAny(s, "不在白名单", "白名单", "路径越界", "类型重复定义",
            "只读模式", "内容为空", "交互输入", "交互", "项目文件", "退出码：127",
            "退出码: 127", "退出码：126", "退出码: 126",
            "写入被拒绝", "写目标未落地"))
            return "tool_rejected";

        // 文件/对象未找到
        if (ContainsAny(s, "文件不存在", "找不到", "未找到", "not found", "not exists",
            "does not exist", "No such file"))
            return "not_found";

        // 模型空响应/空答案
        if (ContainsAny(s, "空答案", "空响应", "返回空内容", "连续返回空"))
            return "empty_response";

        // 达到最大步数终止
        if (ContainsAny(s, "最大步数", "任务终止"))
            return "max_steps";

        // 模板误判
        if (ContainsAny(s, "模板", "template"))
            return "template_flag";

        return "other";
    }

    private static bool ContainsAny(string text, params string[] signals)
    {
        foreach (var sig in signals)
        {
            if (text.Contains(sig, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
