// ============================================================
// IntentOverride.cs
// L2 意图分类的确定性覆盖层（§14-3w 补充）。
// 0.8B 意图分类器不稳：把"在 xx.cs 添加一行注释"判成 SimpleQA，
// 导致模型进入问答模式、不调用工具、空响应卡死。
// 用确定性规则兜底——SimpleQA 但含操作动词 + 文件/代码语境 → 强制 CodeGenerate。
// 属工程化/数据结构性手段，不依赖提示词。
// ============================================================

using System;

namespace Agent.Infrastructure.Routing;

/// <summary>
/// L2 意图分类结果的确定性修正。
/// </summary>
public static class IntentOverride
{
    /// <summary>操作动词。命中表示用户要"做事"而非"问答"。</summary>
    private static readonly string[] OperationVerbs =
    {
        "添加", "新增", "修改", "写入", "创建", "新建", "删除", "编辑",
        "改一下", "加上", "加入", "增加", "重构", "修复",
        "插入", "替换", "移动", "复制", "改名", "更新",
        "写", "更改", "改", "改为", "改变", "变更", "换成",
        "重写", "生成", "追加", "覆盖", "清空", "格式化", "打印", "输出",
        "加", "注释掉"
    };

    /// <summary>文件/代码语境。命中表示操作对象是文件或代码。</summary>
    private static readonly string[] CodeContext =
    {
        ".cs", ".py", ".js", ".ts", ".json", ".csproj", ".sln",
        ".slnx", ".md", ".txt", ".xml", ".yaml", ".yml",
        "代码", "文件", "注释", "属性", "函数", "方法", "类",
        "脚本", "模块", "配置", "接口", "实现"
    };

    /// <summary>读/分析动词。命中表示用户要"读/理解代码"而非"问答/改"（对称写动词表）。</summary>
    private static readonly string[] ReadVerbs =
    {
        "读取", "读一下", "查看", "看看", "看一下", "列出", "显示",
        "检查", "分析", "理解", "找到", "查找", "定位", "内容", "结构"
    };

    /// <summary>
    /// 覆盖意图分类结果：
    /// SimpleQA + 操作动词 + 文件/代码语境 → CodeGenerate（改代码类任务）。
    /// 非 SimpleQA 结果原样返回。
    /// </summary>
    public static IntentType Apply(IntentType intent, string input)
    {
        if (intent != IntentType.SimpleQA)
            return intent;

        return IsWriteTask(input) ? IntentType.CodeGenerate : intent;
    }

    /// <summary>
    /// 是否为写操作任务：操作动词 + 文件/代码语境同时命中。
    /// （工程可写模式路由直连 large 的依据；读/查询类不含写动词，不受影响。）
    /// </summary>
    public static bool IsWriteTask(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;

        return ContainsAny(input, OperationVerbs) && ContainsAny(input, CodeContext);
    }

    /// <summary>
    /// 是否为读/分析任务：读动词 + 文件/代码语境同时命中。
    /// （读任务数据闭环升级 agent_pro、analyze_code 工具引导的依据；
    ///   与写任务互斥——含写动词时 IsWriteTask 优先，由路由顺序保证。）
    /// </summary>
    public static bool IsReadTask(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;

        return ContainsAny(input, ReadVerbs) && ContainsAny(input, CodeContext);
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
