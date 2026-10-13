// ============================================================
// Routing / DeterministicIntentClassifier.cs
// 【批次7 ⑨ 单模型串行】L2 意图分类去模型化——
// 热路径不再调用独立 classifier 模型（qwen2.5:1.5b），改由确定性词表判定六类意图。
// 任务内模型调用收敛为「主模型单模型串行」：省一次 1.5b 调用/少一个常驻模型依赖/分类稳定。
// 纯静态确定性逻辑，可测；不依赖提示词（沿用 IntentOverride 的写/读词表，防双路径漂移）。
// 回退：DI 一行换回 IntentClassifier（模型版，类文件保留）。
// ============================================================
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Agent.Infrastructure.Routing;

/// <summary>
/// 确定性意图分类器（IIntentClassifier 的规则实现）。
/// 判定顺序（先强规则后兜底；与 ModelRouter 分层一致——到 L2 的任务多为读/问答/分析/搜索）：
///   1. Search：外部信息词命中（搜索/最新/新闻/天气/汇率等，不含文件读词）
///   2. ComplexReason：架构/设计/规划/评估/对比/方案等
///   3. FileProcess：读动词 + 文件/代码语境（复用 IntentOverride.IsReadTask）
///   4. CodeGenerate：写动词 + 文件/代码语境（复用 IntentOverride.IsWriteTask，L0.5 已拦，此为防御）
///   5. 默认 SimpleQA
/// </summary>
public sealed class DeterministicIntentClassifier : IIntentClassifier
{
    /// <summary>外部信息检索词（不含"查一下/查找/查询"等文件读词，防读任务误判 Search 走云端）。</summary>
    private static readonly string[] SearchKeywords =
    {
        "搜索", "搜一下", "搜一搜", "最新", "新闻", "天气", "汇率", "股票", "价格",
        "百度", "谷歌", "必应", "网页", "网站", "联网", "资讯", "热点", "实时", "多少钱"
    };

    /// <summary>复杂推理/规划词（不含"分析"——它在 ReadVerbs 里属文件读，且 L1 complex_local 规则先拦）。</summary>
    private static readonly string[] ComplexReasonKeywords =
    {
        "架构", "设计", "规划", "方案", "评估", "对比", "计划", "策略", "调研",
        "权衡", "系统设计", "设计模式", "拆解", "路线图", "清单式", "选型"
    };

    /// <summary>纯规则分类，无任何模型调用（同步返回已完成 Task）。</summary>
    public Task<IntentType> ClassifyAsync(string input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Task.FromResult(IntentType.Unknown);

        if (ContainsAny(input, SearchKeywords))
            return Task.FromResult(IntentType.Search);

        if (ContainsAny(input, ComplexReasonKeywords))
            return Task.FromResult(IntentType.ComplexReason);

        if (IntentOverride.IsReadTask(input))
            return Task.FromResult(IntentType.FileProcess);

        if (IntentOverride.IsWriteTask(input))
            return Task.FromResult(IntentType.CodeGenerate);

        return Task.FromResult(IntentType.SimpleQA);
    }

    private static bool ContainsAny(string input, string[] keywords)
    {
        foreach (var kw in keywords)
        {
            if (input.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
