// ============================================================
// TaskMetric.cs
// 一次任务的指标记录。
// ============================================================

namespace Agent.Core.Models;

/// <summary>
/// 一次任务的指标数据。
/// </summary>
public sealed class TaskMetric
{
    /// <summary>任务唯一 ID。</summary>
    public string TaskId { get; set; } = "";

    /// <summary>会话 ID。</summary>
    public string SessionId { get; set; } = "";

    /// <summary>用户输入（截断到 500 字符）。</summary>
    public string UserInput { get; set; } = "";

    /// <summary>执行模式：agent / orchestrator。</summary>
    public string Mode { get; set; } = "";

    /// <summary>路由决策：Rule / SmallModel / LargeModel。</summary>
    public string RoutingTier { get; set; } = "";

    /// <summary>路由目标：small / agent_pro / large。</summary>
    public string ServiceId { get; set; } = "";

    /// <summary>路由原因（如"规则 force_code_simple"）。</summary>
    public string RoutingReason { get; set; } = "";

    /// <summary>是否成功（退出码 0 + 无错误信号）。</summary>
    public bool Success { get; set; }

    /// <summary>
    /// 是否可疑（触发过"模板输出检测"或类似警告）。
    /// true = 系统曾怀疑过这次任务，可能"表面成功但未真正实现需求"。
    /// </summary>
    public bool IsSuspicious { get; set; }

    /// <summary>
    /// 【10-08 metrics 数据治理】是否 E2E 测试用例（CLI 由环境变量 LAW_E2E=1 标记，
    /// 历史数据按 workspace\e2e 路径特征回填）。E2E 用例故意构造失败验证行为，
    /// 计入 stats 会污染成功率/失败类别——真实统计必须剔除并单列。
    /// </summary>
    public bool IsE2E { get; set; }

    /// <summary>修复次数。</summary>
    public int RetryCount { get; set; }

    /// <summary>总耗时（毫秒）。</summary>
    public long TotalMs { get; set; }

    /// <summary>规划耗时（毫秒，仅 orchestrator 模式）。</summary>
    public long PlannerMs { get; set; }

    /// <summary>执行耗时（毫秒）。</summary>
    public long ExecutorMs { get; set; }

    /// <summary>验证+修复耗时（毫秒）。</summary>
    public long ValidatorMs { get; set; }

    /// <summary>Lint 阻断错误数。</summary>
    public int LintBlockingErrors { get; set; }

    /// <summary>Lint 警告数。</summary>
    public int LintWarningErrors { get; set; }

    /// <summary>错误摘要（成功为空，失败为最后错误的前 500 字符）。</summary>
    public string ErrorSummary { get; set; } = "";

    /// <summary>任务开始时间。</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>任务结束时间。</summary>
    public DateTimeOffset FinishedAt { get; set; }
}