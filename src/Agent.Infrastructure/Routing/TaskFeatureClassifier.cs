using System.Text.RegularExpressions;

namespace Agent.Infrastructure.Routing;

/// <summary>
/// 【D2】任务特征分类器（确定性关键词，非模型）：按输入文本把任务归入特征类，
/// 供路由数据校准（高失败率特征类 → 收紧重试/注记）与 stats 归因报表使用。
/// 特征类语义（与 D1 归因一致）：
///   ProjectIncremental 工程增量修改（在工程中新增/修改类——30B 能力边界，历史高失败）
///   ExternalCredential  外部凭据/API（天气/IP/汇率——免 Key 环境约束注入）
///   MultiFile          多文件生成（系统/项目设计，走 orchestrator）
///   SingleFile         单文件代码生成（走 agent）
///   ReadQuery          读取/查询（读工程/问答）
///   Other              其他
/// </summary>
public static class TaskFeatureClassifier
{
    public enum Feature
    {
        ProjectIncremental,
        ExternalCredential,
        MultiFile,
        SingleFile,
        ReadQuery,
        Other,
    }

    private static readonly Regex ProjectIncrementalRegex = new(
        @"在工程中|工程里新增|新增.*(类|服务|模块)|工程.*新增|给.*(类|文件).*新增|在.*(类|文件|\.cs|模块)\s*中\s*(新增|加入|调用|修改)|向.*添加.*(类|方法)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ExternalCredentialRegex = new(
        @"天气|weather|ip地址|ip地址|汇率|实时|股价|新闻|api|api_key|key",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MultiFileRegex = new(
        @"多文件|分.*(模块|文件)|系统(，|,)|\b系统\b.*(模型|服务|入口)|项目.*(模型|服务|入口)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ReadQueryRegex = new(
        @"^读取|^查看|^检查|^列出|^显示|^分析|^解释|^介绍|^总结|^问答|是什么|有哪些|怎么用|如何",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SingleFileRegex = new(
        @"写一个|写个|创建一个|生成一个|实现一个|用 (c#|python|java|go|rust|js|ts)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>分类任务输入。匹配顺序：工程增量 → 外部凭据 → 读/查 → 多文件 → 单文件 → 其他。</summary>
    public static Feature Classify(string input)
    {
        var t = input ?? "";
        if (ProjectIncrementalRegex.IsMatch(t)) return Feature.ProjectIncremental;
        if (ExternalCredentialRegex.IsMatch(t)) return Feature.ExternalCredential;
        if (ReadQueryRegex.IsMatch(t)) return Feature.ReadQuery;
        if (MultiFileRegex.IsMatch(t)) return Feature.MultiFile;
        if (SingleFileRegex.IsMatch(t)) return Feature.SingleFile;
        return Feature.Other;
    }

    public static string FeatureName(Feature f) => f switch
    {
        Feature.ProjectIncremental => "工程增量",
        Feature.ExternalCredential => "外部凭据",
        Feature.MultiFile => "多文件生成",
        Feature.SingleFile => "单文件生成",
        Feature.ReadQuery => "读/查",
        _ => "其他",
    };
}
