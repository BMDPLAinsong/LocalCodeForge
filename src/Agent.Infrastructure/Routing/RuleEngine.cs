// 引入 JSON
using System.Text.Json;
// 引入正则
using System.Text.RegularExpressions;
// 引入日志
using Microsoft.Extensions.Logging;

// 命名空间
namespace Agent.Infrastructure.Routing;

/// <summary>
/// Layer 1：规则引擎。
/// 从 JSON 文件加载规则，按优先级匹配用户输入。
/// 命中即返回决策，未命中返回 null 让上层继续下探。
/// </summary>
public sealed class RuleEngine : IRuleEngine
{
    // 已编译好的规则列表（按 priority 降序）
    private readonly List<CompiledRule> _rules;
    // 日志
    private readonly ILogger<RuleEngine> _logger;

    /// <summary>
    /// 构造函数。加载并编译规则。
    /// </summary>
    /// <param name="rulesFilePath">规则 JSON 文件路径</param>
    /// <param name="logger">日志</param>
    public RuleEngine(string rulesFilePath, ILogger<RuleEngine> logger)
    {
        _logger = logger;

        // 读取 JSON 文件
        var json = File.ReadAllText(rulesFilePath);

        // 反序列化
        var config = JsonSerializer.Deserialize<RuleConfig>(json, new JsonSerializerOptions
        {
            // JSON 里的字段是 camelCase，C# 属性是 PascalCase
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("规则文件解析失败。");

        // 编译每条规则
        _rules = config.Rules
            // 按 priority 从大到小排序
            .OrderByDescending(r => r.Priority)
            // 转成 CompiledRule
            .Select(r => new CompiledRule
            {
                Name = r.Name,
                Description = r.Description,
                Type = r.Type,
                Target = r.Target,
                // regex 类型预编译，提升匹配性能
                Regex = r.Type == "regex" && r.Pattern is not null
                    ? new Regex(r.Pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase)
                    : null,
                Min = r.Min,
                Max = r.Max,                       // ← 新增
                Keywords = r.Keywords,              // ← 新增
                Prefixes = r.Prefixes               // ← 新增
            })
            .ToList();

        // 日志
        _logger.LogInformation("RuleEngine 已加载 {Count} 条规则。", _rules.Count);
    }

    /// <summary>
    /// 评估输入，返回第一条命中的规则。
    /// </summary>
    public RoutingDecision? Evaluate(RoutingContext context)
    {
        // 按优先级顺序逐条评估
        foreach (var rule in _rules)
        {
            // 未命中则跳过
            if (!Matches(rule, context))
                continue;

            // 命中：记录日志
            _logger.LogInformation(
                "规则命中：{RuleName} → {Target}",
                rule.Name, rule.Target);

            // 返回决策
            return new RoutingDecision(
                rule.Target,
                RoutingTier.Rule,
                $"规则 {rule.Name}：{rule.Description}");
        }

        // 全部未命中
        return null;
    }

    /// <summary>
    /// 判断单条规则是否命中。
    /// </summary>
    private static bool Matches(CompiledRule rule, RoutingContext ctx)
    {
        var input = ctx.UserInput ?? "";

        return rule.Type switch
        {
            // 正则匹配
            "regex" => rule.Regex?.IsMatch(input) == true,

            // 长度 >= min
            "length_min" => input.Length >= rule.Min,

            // 长度 <= max
            "length_max" => input.Length <= rule.Max && input.Length > 0,

            // 旧的长度语义（保留兼容）
            "length" => input.Length >= rule.Min,

            // 关键词任意命中
            "keyword_any" => rule.Keywords?.Any(k => input.Contains(k, StringComparison.OrdinalIgnoreCase)) == true,

            // 前缀任意命中
            "prefix_any" => rule.Prefixes?.Any(p => input.StartsWith(p, StringComparison.OrdinalIgnoreCase)) == true,

            // 上一步失败
            "after_failure" => ctx.LastStepFailed,

            _ => false
        };
    }

    /// <summary>
    /// JSON 配置的顶层结构。
    /// </summary>
    private sealed class RuleConfig
    {
        // 规则列表
        public List<RuleDto> Rules { get; set; } = new();
    }

    /// <summary>
    /// 单条规则的原始形态(未编译)
    /// </summary>
    private sealed class RuleDto
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Type { get; set; } = "regex";
        public string? Pattern { get; set; }
        public int Min { get; set; } = 0;
        public int Max { get; set; } = int.MaxValue;      // ← 新增
        public List<string>? Keywords { get; set; }       // ← 新增
        public List<string>? Prefixes { get; set; }       // ← 新增
        public string Target { get; set; } = "small";
        public int Priority { get; set; } = 0;
    }

    /// <summary>
    ///  编译后的规则，用于实际匹配。
    /// </summary>
    private sealed class CompiledRule
    {
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public string Type { get; init; } = "regex";
        public string Target { get; init; } = "small";
        public Regex? Regex { get; init; }
        public int Min { get; init; }
        public int Max { get; init; } = int.MaxValue;      // ← 新增
        public List<string>? Keywords { get; init; }        // ← 新增
        public List<string>? Prefixes { get; init; }        // ← 新增
    }
}