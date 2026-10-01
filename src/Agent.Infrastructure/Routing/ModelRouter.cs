// 引入日志
using Agent.Core.Models;
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

    /// <summary>构造函数。</summary>
    public ModelRouter(
        IRuleEngine rules,
        IIntentClassifier classifier,
        ILogger<ModelRouter> logger,
         AgentOptions options)
    {
        _rules = rules;
        _classifier = classifier;
        _logger = logger;
        _options = options;
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