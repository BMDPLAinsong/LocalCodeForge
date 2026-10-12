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
    private readonly RoutingDecisionStore _decisions;   // ← 3A：路由决策落库（调权数据地基）

    /// <summary>L0 查询词（只走 small，跳过规则与分类器；词表单处维护）。</summary>
    private static readonly string[] QueryKeywords =
    {
        "看看", "查看", "看一下", "列出", "显示", "有哪些文件", "什么文件"
        // 删掉 "读一下"、"这个文件" 等，让它们走正常流程
    };

    /// <summary>构造函数。</summary>
    public ModelRouter(
        IRuleEngine rules,
        IIntentClassifier classifier,
        ILogger<ModelRouter> logger,
        AgentOptions options,
        IMetricsCollector metrics,
        RoutingDecisionStore decisions)
    {
        _rules = rules;
        _classifier = classifier;
        _logger = logger;
        _options = options;
        _metrics = metrics;
        _decisions = decisions;
    }

    /// <summary>
    /// 【P2 调权地基】取调权用历史表现：时代边界已配置（LawUnifiedEpoch 非空）→ 只统计 30B 时代样本
    /// （4B 历史不再污染 agent_pro/small 的能力判定）；未配置 → 全量（兼容旧行为）。
    /// </summary>
    private async Task<ServiceStats> GetTuningStatsAsync(
        string serviceId, string routingTier, int take, CancellationToken ct)
    {
        var epoch = _options.LawUnifiedEpoch;
        return epoch is not null
            ? await _metrics.GetServiceStatsSinceAsync(serviceId, routingTier, epoch.Value, take, ct)
            : await _metrics.GetServiceStatsAsync(serviceId, routingTier, take, ct);
    }

    /// <summary>E2E 冒烟运行判定（LAW_E2E=1）。路由决策落库守卫的唯一逻辑源，
    /// public static 供 tests 行为验证（见 3A 块"路由决策 E2E 源头隔离"）。</summary>
    public static bool IsE2ERun =>
        string.Equals(Environment.GetEnvironmentVariable("LAW_E2E"), "1", StringComparison.Ordinal);

    /// <summary>路由决策落库（fail-safe：记录失败不影响主流程）。</summary>
    private async Task RecordDecisionAsync(RoutingDecision d, string input, CancellationToken ct)
    {
        // 【10-10 真实口径下沉】E2E 冒烟用例不产生路由决策记录（与 AgentLoopMetricsDecorator /
        // OrchestratorMetricsDecorator 同款 LAW_E2E 环境变量标记）。routing_decisions 无 is_e2e 列、
        // 属行为观测表——源头隔离：E2E 物理上不落库，防未来 3B 调权（按特征类/服务分布）踩 E2E 污染坑。
        if (IsE2ERun)
            return;

        try
        {
            await _decisions.RecordAsync(d, input, sessionId: null, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[路由决策] 落库失败（不影响主流程）。");
        }
    }

    /// <summary>
    /// 三层漏斗：规则 → 意图分类 → 服务分发。
    /// </summary>
    public async Task<RoutingDecision> RouteAsync(RoutingContext context, CancellationToken ct)
    {

        // 【L0-查询】查询类任务直接走 Small，跳过所有规则和分类器
        if (QueryKeywords.Any(k => context.UserInput.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogInformation("[L0-查询] 命中查询词，直接走本地 small");
            var d = new RoutingDecision(
                RoutingDecision.Small,
                RoutingTier.Rule,
                "查询类任务，走本地 Small");
            await RecordDecisionAsync(d, context.UserInput, ct);
            return d;
        }

        // ---------- L0.5：写任务 → 直连 large ----------
        // （路由层修复 A）本地 small/agent_pro（4B）对"读+改"两步工具链生成不稳、
        // 常空响应（每层空等 10-20 分钟再升级），最终仍需 large 执行。
        // 写操作任务（操作动词+文件/代码语境）全模式直连 large（不再要求工程可写）：
        // 一次成功率更高，总时间反而更短。读任务保持 small（已验证能完成）。
        if (IntentOverride.IsWriteTask(context.UserInput))
        {
            // ──（2026-10-12 方案A）读改混合任务不参与降级──
            // "index.html 读取这个文件，并对这个文件进行修复" 同时命中读动词+写动词：
            // 必须先读工程内已存在文件再修改。降级 small 后模型拿到任务却不读目标文件
            // （大工程 CompactBlock 截断导致目标文件未入上下文时，实测自创无关任务跑偏）。
            // 只有纯新建写任务（无读动词）才参与 L0.5 数据闭环降级。
            if (IntentOverride.IsReadTask(context.UserInput))
            {
                _logger.LogInformation("[L0.5] 读改混合任务（读+写动词），保持 large 不降级。");
                var mixedD = new RoutingDecision(
                    RoutingDecision.Large,
                    RoutingTier.Rule,
                    "读改混合任务保持 large（需先读已有文件再修改）");
                await RecordDecisionAsync(mixedD, context.UserInput, ct);
                return mixedD;
            }

            // ── 数据闭环（方案 C）：历史数据回流调权 ──
            // 写任务默认直连 large（修复 A 的确定性结论）。
            // 但若历史表明同场景（tier=Rule）small 写任务成功率足够高且显著省时，
            // 降级 small（省 large CPU 推理时间）；失败由升级链（small→large）兜底，不劣化。
            // 查询失败/数据不足 → 保持 large。
            try
            {
                var smallStats = await GetTuningStatsAsync(
                    RoutingDecision.Small, RoutingTier.Rule.ToString(), 10, ct);
                var largeStats = await GetTuningStatsAsync(
                    RoutingDecision.Large, RoutingTier.Rule.ToString(), 10, ct);

                if (RoutingIntelligence.TrySuggestDowngrade(
                    smallStats, largeStats,
                    minSamples: _options.RoutingTuningMinSamples,
                    minSuccessRate: _options.RoutingDowngradeMinSuccessRate, out var reason))
                {
                    _logger.LogInformation("[数据闭环] L0.5 写任务降级 small（{Reason}）", reason);
                    var d = new RoutingDecision(
                        RoutingDecision.Small,
                        RoutingTier.Rule,
                        $"写任务降级 small（{reason}）");
                    await RecordDecisionAsync(d, context.UserInput, ct);
                    return d;
                }

                _logger.LogDebug("保持 large：{Reason}", reason ?? "条件不满足（样本不足/耗时不占优）");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[数据闭环] 查询历史表现失败，保持 large。");
            }

            _logger.LogInformation("[L0.5-写任务] 写任务 → 直连 large");
            var writeD = new RoutingDecision(
                RoutingDecision.Large,
                RoutingTier.Rule,
                "写任务直连 large（跳过 4B 空响应）");
            await RecordDecisionAsync(writeD, context.UserInput, ct);
            return writeD;
        }

        // ---------- Layer 1：规则拦截 ----------
        var ruleHit = _rules.Evaluate(context);
        if (ruleHit is not null)
        {
            // ── L0.5b 读任务数据闭环：small 读任务空响应率高 → 直连 agent_pro ──
            // 读任务默认走 small（规则命中）。若历史表明 small 读任务成功率偏低
            // （空响应重试拖慢）且 agent_pro 样本足、成功率达标、更省时，则升级 agent_pro；
            // 失败由升级链（agent_pro→large）兜底，不劣化。查询失败/数据不足 → 保持 small。
            if (ruleHit.ServiceId == RoutingDecision.Small && IntentOverride.IsReadTask(context.UserInput))
            {
                try
                {
                    var smallStats = await GetTuningStatsAsync(
                        RoutingDecision.Small, RoutingTier.Rule.ToString(), 10, ct);
                    var proStats = await GetTuningStatsAsync(
                        RoutingDecision.AgentPro, RoutingTier.Rule.ToString(), 10, ct);

                    if (RoutingIntelligence.TrySuggestReadTier(
                        proStats, smallStats,
                        minSamples: _options.RoutingTuningMinSamples,
                        badRate: _options.RoutingReadUpgradeBadRate,
                        minSuccessRate: _options.RoutingReadUpgradeMinSuccessRate, out var readReason))
                    {
                        _logger.LogInformation("[数据闭环] L0.5b 读任务升级 agent_pro（{Reason}）", readReason);
                        var d = new RoutingDecision(
                            RoutingDecision.AgentPro,
                            RoutingTier.Rule,
                            $"读任务升级 agent_pro（{readReason}）");
                        await RecordDecisionAsync(d, context.UserInput, ct);
                        return d;
                    }

                    _logger.LogDebug("读任务保持 small：{Reason}", readReason ?? "条件不满足（样本不足/成功率达标）");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[数据闭环] 读任务历史查询失败，保持 small。");
                }
            }

            _logger.LogInformation(
                "[L1-规则] {Rule} → {ServiceId}",
                ruleHit.Reason, ruleHit.ServiceId);
            await RecordDecisionAsync(ruleHit, context.UserInput, ct);
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

        // ── 【P2】ComplexReason → agent_pro 自动调权 ──
        // 默认 ComplexToPro=true 走 agent_pro；若时代过滤历史表明 agent_pro 该层表现差
        // （样本 ≥5 且成功率 <0.5），自动降级 large（数据不足/表现好 → 保持，保守不冒险）。
        if (serviceId == RoutingDecision.AgentPro)
        {
            try
            {
                var proStats = await GetTuningStatsAsync(
                    RoutingDecision.AgentPro, RoutingTier.Rule.ToString(), 10, ct);
                if (RoutingIntelligence.TrySuggestDropAgentPro(
                    proStats,
                    minSamples: _options.RoutingDropAgentProMinSamples,
                    minSuccessRate: _options.RoutingDropAgentProMinSuccessRate, out var dropReason))
                {
                    _logger.LogInformation("[数据闭环] P2 ComplexReason 降级 large（{Reason}）", dropReason);
                    serviceId = RoutingDecision.Large;
                }
                else
                {
                    _logger.LogDebug("ComplexReason 保持 agent_pro：{Reason}", dropReason ?? "条件不满足（样本不足/成功率达标）");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[数据闭环] agent_pro 调权查询失败，保持 agent_pro。");
            }
        }

        // 记录日志
        _logger.LogInformation(
    "[L3-分发] {Intent} → {ServiceId}{Extra}",
            intent,
            serviceId,
            serviceId == RoutingDecision.AgentPro ? " (AgentPro/30B)" : "");

        // 返回决策，携带意图信息
        // 注意：RoutingTier.SmallModel 在此仅表示"L3 意图分发层"（层标记），
        // 不代表模型规模——实际角色由 serviceId 决定（agent_pro 的 L3 记录也是此 tier，
        // 调权查询按 serviceId + SmallModel 区分，勿误读为 small 模型数据）。
        var finalD = new RoutingDecision(
            serviceId,
            RoutingTier.SmallModel,
            $"意图 {intent}",
            intent);
        await RecordDecisionAsync(finalD, context.UserInput, ct);
        return finalD;
    }
}