// 引入日志
using Agent.Core.Models;
using Agent.Infrastructure.Metrics;
using Microsoft.Extensions.Logging;

// 命名空间
namespace Agent.Infrastructure.Routing;

/// <summary>
/// 三层漏斗路由器。
/// </summary>
public sealed class ModelRouter : IModelRouter
{
    // Layer 1
    private readonly IRuleEngine _rules;
    // Layer 2
    private readonly IIntentClassifier _classifier;
    // 日志
    private readonly ILogger<ModelRouter> _logger;
    private readonly AgentOptions _options;   // ← 新增
    private readonly IMetricsCollector _metrics;   // ← 数据闭环：历史表现回流

    /// <summary>构造函数。</summary>
    public ModelRouter(
        IRuleEngine rules,
        IIntentClassifier classifier,
        ILogger<ModelRouter> logger,
        AgentOptions options,
        IMetricsCollector metrics)
    {
        _rules = rules;
        _classifier = classifier;
        _logger = logger;
        _options = options;
        _metrics = metrics;
    }

    /// <summary>
    /// 三层漏斗：规则 → 意图分类 → 服务分发。
    /// </summary>
    public async Task<RoutingDecision> RouteAsync(RoutingContext context, CancellationToken ct)
    {

        // 【新增】查询类任务直接走 Small，跳过所有规则和分类器
        var queryKeywords = new[]
     {
    "看看", "查看", "看一下", "列出", "显示", "有哪些文件", "什么文件"
    // 删掉 "读一下"、"这个文件" 等，让它们走正常流程
};

        if (queryKeywords.Any(k => context.UserInput.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogInformation("[L0-查询] 命中查询词，直接走本地 small");
            return new RoutingDecision(
                RoutingDecision.Small,
                RoutingTier.Rule,
                "查询类任务，走本地 Small");
        }

        // ---------- L0.5：写任务 → 直连 large ----------
        // （路由层修复 A）本地 small/agent_pro（4B）对"读+改"两步工具链生成不稳、
        // 常空响应（每层空等 10-20 分钟再升级），最终仍需 large 执行。
        // 写操作任务（操作动词+文件/代码语境）全模式直连 large（不再要求工程可写）：
        // 一次成功率更高，总时间反而更短。读任务保持 small（已验证能完成）。
        if (IntentOverride.IsWriteTask(context.UserInput))
        {
            // ── 数据闭环（方案 C）：历史数据回流调权 ──
            // 写任务默认直连 large（修复 A 的确定性结论）。
            // 但若历史表明同场景（tier=Rule）small 写任务成功率足够高且显著省时，
            // 降级 small（省 large CPU 推理时间）；失败由升级链（small→large）兜底，不劣化。
            // 查询失败/数据不足 → 保持 large。
            try
            {
                var smallStats = await _metrics.GetServiceStatsAsync(
                    RoutingDecision.Small, RoutingTier.Rule.ToString(), 10, ct);
                var largeStats = await _metrics.GetServiceStatsAsync(
                    RoutingDecision.Large, RoutingTier.Rule.ToString(), 10, ct);

                if (RoutingIntelligence.TrySuggestDowngrade(
                    smallStats, largeStats, minSamples: 3, minSuccessRate: 0.6, out var reason))
                {
                    _logger.LogInformation("[数据闭环] L0.5 写任务降级 small（{Reason}）", reason);
                    return new RoutingDecision(
                        RoutingDecision.Small,
                        RoutingTier.Rule,
                        $"写任务降级 small（{reason}）");
                }

                _logger.LogDebug("保持 large：{Reason}", reason);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[数据闭环] 查询历史表现失败，保持 large。");
            }

            _logger.LogInformation("[L0.5-写任务] 写任务 → 直连 large");
            return new RoutingDecision(
                RoutingDecision.Large,
                RoutingTier.Rule,
                "写任务直连 large（跳过 4B 空响应）");
        }

        // ---------- Layer 1：规则拦截 ----------
        var ruleHit = _rules.Evaluate(context);
        if (ruleHit is not null)
        {
            _logger.LogInformation(
                "[L1-规则] {Rule} → {ServiceId}",
                ruleHit.Reason, ruleHit.ServiceId);
            return ruleHit;
        }

        // ---------- Layer 2：意图分类 ----------
        IntentType intent;
        try
        {
            intent = await _classifier.ClassifyAsync(context.UserInput, ct);

            // 【确定性覆盖】0.8B 分类器不稳：SimpleQA + 操作动词 + 文件/代码语境 → CodeGenerate
            // （避免"在 xx.cs 添加一行注释"被判 SimpleQA 进入问答模式、不调工具卡死）
            var overridden = IntentOverride.Apply(intent, context.UserInput);
            if (overridden != intent)
                _logger.LogInformation("[L2-意图] 覆盖 {From} → {To}（操作动词+文件语境）", intent, overridden);
            intent = overridden;

            _logger.LogInformation("[L2-意图] {Intent}", intent);
        }
        catch (Exception ex)
        {
            // 分类失败 → Unknown → 云端兜底
            _logger.LogWarning(ex, "[L2-意图] 分类失败，兜底 Unknown。");
            intent = IntentType.Unknown;
        }

        // ─────── Layer 3: 意图 → 服务映射 ───────
        // 【核心映射表】
        //   本地（省钱）    : SimpleQA / CodeGenerate / FileProcess
        //   本地 AgentPro  : ComplexReason（如果 ComplexToPro = true）
        //   云端（保质量）  : Search / Unknown / ComplexReason（如果 ComplexToPro = false）
        var serviceId = intent switch
        {
            IntentType.SimpleQA => RoutingDecision.Small,
            IntentType.CodeGenerate => RoutingDecision.Small,
            IntentType.FileProcess => RoutingDecision.Small,
            IntentType.Search => RoutingDecision.Large,
            IntentType.ComplexReason => _options.ComplexToPro
                ? RoutingDecision.AgentPro
                : RoutingDecision.Large,
            IntentType.Unknown => RoutingDecision.Large,
            _ => RoutingDecision.Large
        };

        // 记录日志
        _logger.LogInformation(
    "[L3-分发] {Intent} → {ServiceId}{Extra}",
            intent,
            serviceId,
            serviceId == RoutingDecision.AgentPro ? " (AgentPro/本地4B)" : "");

        // 返回决策，携带意图信息
        return new RoutingDecision(
            serviceId,
            RoutingTier.SmallModel,
            $"意图 {intent}",
            intent);
    }
}