// ============================================================
// Routing / ToolLayering.cs
// 【批次6 ⑧ 能力分层】工具按能力门槛分层，路由后按目标服务注入对应子集。
// 数据驱动（每工具带能力标签 + 每服务允许层级表），非提示词——
// 低层模型（small）只暴露基础工具，分析类/记忆类工具仅 agent_pro/large 可见。
// 纯静态确定性逻辑，可测；不依赖模型。
// ============================================================
using Agent.Core.Abstractions;

namespace Agent.Infrastructure.Routing;

/// <summary>
/// 工具能力分层表：工具名 → 能力层级；服务 → 允许层级。
/// 新增工具必须登记层级（未登记默认 basic，防"新工具静默全可见"）。
/// </summary>
public static class ToolLayering
{
    /// <summary>基础层：读写/目录/命令，全部模型可见。</summary>
    public const string Basic = "basic";

    /// <summary>分析层：符号/代码结构分析，仅 agent_pro/large 可见（低层模型用不动也易误用）。</summary>
    public const string Analysis = "analysis";

    /// <summary>记忆层：跨任务记忆写入，仅 agent_pro/large 可见（写记忆是高阶能力）。</summary>
    public const string Memory = "memory";

    /// <summary>工具能力标签表（数据驱动；OrdinalIgnoreCase 与 ToolRegistry 一致）。</summary>
    public static readonly IReadOnlyDictionary<string, string> TierByName =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["read_file"] = Basic,
            ["write_file"] = Basic,
            ["edit_file"] = Basic,
            ["list_dir"] = Basic,
            ["create_dir"] = Basic,
            ["run_command"] = Basic,
            ["find_symbol"] = Analysis,
            ["analyze_code"] = Analysis,
            ["dependency_analyzer"] = Analysis,
            ["remember"] = Memory,
        };

    /// <summary>服务 → 允许层级表（数据驱动）。small 仅基础；agent_pro/large 全量。</summary>
    public static readonly IReadOnlyDictionary<string, string[]> AllowedTiersByService =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [RoutingDecision.Small] = new[] { Basic },
            [RoutingDecision.AgentPro] = new[] { Basic, Analysis, Memory },
            [RoutingDecision.Large] = new[] { Basic, Analysis, Memory },
        };

    /// <summary>工具的能力层级（未登记 → basic，防新工具静默全可见）。</summary>
    public static string TierOf(string toolName)
        => TierByName.TryGetValue(toolName, out var tier) ? tier : Basic;

    /// <summary>服务是否允许某层级（分层关闭时全放行）。</summary>
    public static bool ServiceHasTier(string serviceId, string tier, bool enabled)
    {
        if (!enabled) return true;
        return AllowedTiersByService.TryGetValue(serviceId, out var tiers)
            && tiers.Contains(tier, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>按目标服务过滤工具集。分层关闭 / 服务未登记 → 全量（兼容现状，行为不变）。</summary>
    public static IReadOnlyCollection<IAgentTool> Filter(
        IEnumerable<IAgentTool> all, string serviceId, bool enabled)
    {
        var list = all.ToList();
        if (!enabled) return list;
        if (!AllowedTiersByService.TryGetValue(serviceId, out var allowedTiers)) return list;
        var allowed = new HashSet<string>(allowedTiers, StringComparer.OrdinalIgnoreCase);
        return list.Where(t => allowed.Contains(TierOf(t.Name))).ToList();
    }
}
