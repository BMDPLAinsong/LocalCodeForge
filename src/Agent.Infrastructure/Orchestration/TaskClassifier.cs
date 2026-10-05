// ============================================================
// TaskClassifier.cs
// 判断一个任务应该走 Orchestrator（三阶段编排）还是 AgentLoop（单模型 ReAct）。
// ============================================================

using System.Text.RegularExpressions;

namespace Agent.Infrastructure.Orchestration;

/// <summary>
/// 任务分类器。纯规则判断，不调 LLM。
/// </summary>
public static class TaskClassifier
{
    /// <summary>
    /// 文件引用正则（公共单一来源，ReadOnlyGuard 复用，消双路径漂移）：
    /// 捕获组取文件名（不含尾部边界字符）；lookahead 边界（空白/结尾/路径分隔符）不消耗字符——
    /// 与历史版 `[\w\-\.]+\.[a-z]{1,5}(\s|$|/|\\)` 的 IsMatch 语义完全等价，
    /// 且 m.Groups[1].Value 为干净文件名（尾部空格/斜杠不进 Value）。
    /// 注意：后缀限 1-5 字母（cs/py/js/txt…）；csproj（6 字母）天然不匹配——与历史行为一致。
    /// </summary>
    public static readonly Regex FileReferencePattern = new(
        @"([\w\-\.]+\.[a-z]{1,5})(?=\s|$|/|\\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    /// <summary>
    /// 明确的多文件/项目级关键词。
    /// 【优先级最高】命中则直接走编排。
    /// </summary>
    private static readonly string[] StrongMultiFileKeywords =
    {
        "多文件", "多个文件", "分文件", "多模块",
        "用多个文件", "用多文件"
    };

    /// <summary>
    /// 项目级关键词。
    /// 【优先级中等】没有强单文件关键词时命中。
    /// </summary>
    private static readonly string[] ProjectKeywords =
    {
        "项目", "系统", "应用", "服务", "框架",
        "爬虫", "网站", "后台", "平台", "整套"
    };

    /// <summary>
    /// 明确的单文件/小任务关键词。
    /// 【优先级最高】命中则直接走 AgentLoop，不再检查其它。
    /// 注意：不要用过宽的词（如"计算"会命中"计算器"）。
    /// </summary>
    private static readonly string[] SingleFileKeywords =
    {
        "写一个函数", "写个函数", "写一个方法", "写个方法",
        "写一个脚本", "写个脚本", "写一个类", "写个类",
        "实现一个函数", "实现一个方法"
    };

    /// <summary>
    /// 纯查询动词。命中则强制走 AgentLoop，不走编排。
    /// </summary>
    private static readonly string[] QueryOnlyKeywords =
    {
    "看看", "查看", "看一下", "列出", "显示",
    "读一下", "读一下", "分析一下", "解释一下",
    "这个函数", "这个文件", "这段代码", "这个项目",
    "读取", "检查"
};

    /// <summary>
    /// 判断是否应该走 Orchestrator。
    /// 判断顺序（从高到低）：
    ///   1. 强多文件词 → 编排（最高优先级，避免被查询词误拦）
    ///   2. 单文件词 → AgentLoop
    ///   3. 查询词 → AgentLoop
    ///   4. 设计词 → AgentLoop（设计类不生成代码）
    ///   5. 项目词 → 编排
    ///   6. 长度 > 40 → 编排
    ///   7. 默认 → AgentLoop
    /// </summary>
    public static bool ShouldOrchestrate(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;

        // 1. 强多文件词——最高优先级，命中直接走编排
        // 理由：即使输入里有"列出"这种查询词，只要用户明确说了"多文件"，
        // 就应该走编排生成多文件项目，而不是被 L0-查询拦截
        foreach (var kw in StrongMultiFileKeywords)
        {
            if (input.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // 2. 单文件词——明确的小任务，走 AgentLoop
        foreach (var kw in SingleFileKeywords)
        {
            if (input.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // 3. 查询词——信息查询类，走 AgentLoop
        foreach (var kw in QueryOnlyKeywords)
        {
            if (input.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // 4. 设计词——设计/规划类，走 AgentLoop（不生成代码）
        var designKeywords = new[] { "设计", "规划", "架构", "方案" };
        foreach (var kw in designKeywords)
        {
            if (input.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // 5. 项目级词——走编排
        foreach (var kw in ProjectKeywords)
        {
            if (input.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // 5.5 文件引用检查——任务明确引用具体文件（含扩展名路径）时走 AgentLoop
        // （工程模式任务天然带长路径：修改/读取具体文件由 read_file/edit_file 工具处理，
        //   不需要 orchestrator 的多文件规划；避免长度规则把带路径任务误拉入编排卡死）
        if (FileReferencePattern.IsMatch(input))
        {
            return false;
        }

        // 6. 长输入（> 60 字）——倾向于编排
        // （40 太宽：带路径的读取类任务自然超 40 字，误入编排卡死；
        //   查询词已在前面拦截读取/查看/检查类，长阈值只兜底真正复杂的长描述）
        if (input.Length > 60)
            return true;

        // 7. 默认走 AgentLoop
        return false;
    }
}