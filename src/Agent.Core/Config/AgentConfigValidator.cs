// ============================================================
// Config / AgentConfigValidator.cs
// 配置校验（10-13 批次1：消"无效配置静默生效"缺陷）。
// 确定性纯函数：Validate(AgentOptions) → 错误列表（空 = 通过）。
// CLI 启动时调用——有错误即打印并退出，绝不静默跑在矛盾配置上。
// ============================================================

namespace Agent.Core.Config;

using Agent.Core.Models;

/// <summary>
/// AgentOptions 配置校验器（零依赖、可单测）。
/// </summary>
public static class AgentConfigValidator
{
    /// <summary>校验配置，返回错误列表（空 = 通过）。</summary>
    public static List<string> Validate(AgentOptions o)
    {
        var errors = new List<string>();

        // ─── 正整数类 ───
        CheckPositive(errors, nameof(o.MaxSteps), o.MaxSteps);
        CheckPositive(errors, nameof(o.ReadQueryMaxSteps), o.ReadQueryMaxSteps);
        CheckPositive(errors, nameof(o.MaxHistoryTurns), o.MaxHistoryTurns);
        CheckNonNegative(errors, nameof(o.ExecutorMaxRetries), o.ExecutorMaxRetries);
        CheckPositive(errors, nameof(o.MemoryPruneDays), o.MemoryPruneDays);
        CheckNonNegative(errors, nameof(o.MemoryPruneMinHits), o.MemoryPruneMinHits);
        CheckNonNegative(errors, nameof(o.TaskTimeoutMinutes), o.TaskTimeoutMinutes);
        CheckPositive(errors, nameof(o.OllamaHttpTimeoutMinutes), o.OllamaHttpTimeoutMinutes);
        CheckPositive(errors, nameof(o.ContextTargetInjectBytes), o.ContextTargetInjectBytes);
        CheckPositive(errors, nameof(o.ContextToolResultBytes), o.ContextToolResultBytes);
        CheckPositive(errors, nameof(o.ContextFoldThreshold), o.ContextFoldThreshold);
        CheckPositive(errors, nameof(o.ChatTurnRetentionDays), o.ChatTurnRetentionDays);
        CheckPositive(errors, nameof(o.EventRetentionDays), o.EventRetentionDays);
        CheckPositive(errors, nameof(o.RoutingDecisionRetentionDays), o.RoutingDecisionRetentionDays);

        // ─── 路由调权率 ∈ [0,1] ───
        CheckRate(errors, nameof(o.RoutingDowngradeMinSuccessRate), o.RoutingDowngradeMinSuccessRate);
        CheckRate(errors, nameof(o.RoutingReadUpgradeBadRate), o.RoutingReadUpgradeBadRate);
        CheckRate(errors, nameof(o.RoutingReadUpgradeMinSuccessRate), o.RoutingReadUpgradeMinSuccessRate);
        CheckRate(errors, nameof(o.RoutingDropAgentProMinSuccessRate), o.RoutingDropAgentProMinSuccessRate);

        // ─── 温度 ∈ [0,2] ───
        CheckTemperature(errors, nameof(o.Temperature), o.Temperature);

        // ─── 语义关系（防矛盾配置静默生效） ───
        if (o.ContextFoldThreshold < o.ContextToolResultBytes)
            errors.Add($"ContextFoldThreshold({o.ContextFoldThreshold}) 必须 >= ContextToolResultBytes({o.ContextToolResultBytes})——折叠是累计预算，低于单次截断阈值则截断永远优先、折叠形同虚设");

        // ─── 端点校验 ───
        CheckEndpoint(errors, "Classifier", o.Classifier);
        CheckEndpoint(errors, "Small", o.Small);
        CheckEndpoint(errors, "Large", o.Large);
        CheckEndpoint(errors, "AgentPro", o.AgentPro);

        return errors;
    }

    private static void CheckPositive(List<string> e, string name, int v)
    {
        if (v <= 0) e.Add($"{name} 必须 > 0（当前 {v}）");
    }

    private static void CheckNonNegative(List<string> e, string name, int v)
    {
        if (v < 0) e.Add($"{name} 必须 >= 0（当前 {v}）");
    }

    private static void CheckRate(List<string> e, string name, double v)
    {
        if (v < 0 || v > 1) e.Add($"{name} 必须在 0-1（当前 {v}）");
    }

    private static void CheckTemperature(List<string> e, string name, double v)
    {
        if (v < 0 || v > 2) e.Add($"{name} 必须在 0-2（当前 {v}）");
    }

    private static void CheckEndpoint(List<string> e, string name, ModelEndpoint ep)
    {
        if (ep is null) { e.Add($"{name} 端点未配置（null）"); return; }
        var provider = ep.Provider?.Trim() ?? "";
        if (provider is not ("ollama" or "openai"))
            e.Add($"{name}.Provider 必须为 ollama 或 openai（当前 {provider}）");
        if (string.IsNullOrWhiteSpace(ep.Model))
            e.Add($"{name}.Model 不能为空");
        var endpoint = ep.Endpoint?.Trim() ?? "";
        if (!endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            e.Add($"{name}.Endpoint 必须以 http(s):// 开头（当前 {endpoint}）");
    }
}
