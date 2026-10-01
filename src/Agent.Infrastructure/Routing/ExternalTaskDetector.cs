// ============================================================
// ExternalTaskDetector.cs
// 任务类型识别：外部数据任务（天气 / IP 定位 / 汇率等）。
// 识别的是"任务类型"（有限集合），不是"失败形态"（无限集合）——
// 与打地鼠式按失败补洞的根本区别。
// 命中 → 返回环境约束数据块（注入生成阶段）；未命中 → null。
// ============================================================

using System;

namespace Agent.Infrastructure.Routing;

/// <summary>
/// 确定性识别"外部数据任务"并生成环境约束数据块。
/// 单一来源：AgentLoop（agent 路径）与 Orchestrator（orchestrator 路径）共用。
/// </summary>
public static class ExternalTaskDetector
{
    /// <summary>
    /// 外部数据任务词表（小而稳，保守命中，宁可漏报不误报）。
    /// 组合词优先，避免"实时""查询"等单字误伤本地计算任务。
    /// </summary>
    private static readonly string[] Keywords =
    {
        "天气", "气温", "weather",
        "IP地址", "ip地址", "根据ip", "根据IP",
        "经纬度", "定位",
        "汇率", "货币兑换", "exchange rate",
        "实时行情", "股票行情",
    };

    /// <summary>
    /// 环境约束数据块。作为任务数据注入（与"计划预期输出"数据块同构），
    /// 不是 SystemPrompt、不是修复器规则。
    /// 事实依据：阶段 0 实测（2026-10-01）——ip-api.com / wttr.in 可达但延迟波动大（最慢 4.2s），
    /// 无 timeout 的请求在瞬时抖动时会无限挂起（10/01 14:22 实证 30s kill）。
    /// </summary>
    public const string ConstraintBlock =
        "[环境约束] 本环境无外部 API Key；ip-api.com / openweathermap.org 等海外服务可能不可达或需注册。\n" +
        "本任务若需外部数据：① 优先免 Key 服务（IP 定位用 http://ip-api.com/json/，天气用 https://wttr.in/）；\n" +
        "② 所有 HTTP 请求必须带 timeout=8 并处理超时异常；③ 失败时快速明确报错（如\"无法获取天气信息\"），禁止无限等待。";

    /// <summary>
    /// 检测任务文本是否属于外部数据任务。
    /// </summary>
    /// <param name="input">用户任务文本。</param>
    /// <returns>命中返回环境约束数据块；未命中返回 null。</returns>
    public static string? Detect(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        foreach (var keyword in Keywords)
        {
            if (input.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return ConstraintBlock;
            }
        }

        return null;
    }
}
