// 引入 SK 聊天服务
using Microsoft.SemanticKernel.ChatCompletion;
// 引入 SK 的 OpenAI 设置
using Microsoft.SemanticKernel.Connectors.OpenAI;

// 命名空间
namespace Agent.Infrastructure.Routing;

/// <summary>
/// 用本地 0.8B 模型做意图分类。
/// 输出 6 类意图之一。
/// </summary>
public sealed class IntentClassifier : IIntentClassifier
{
    // 0.8B 的聊天服务
    private readonly IChatCompletionService _chat;

    /// <summary>构造函数。</summary>
    public IntentClassifier(IChatCompletionService chat)
    {
        _chat = chat;
    }

    /// <summary>
    /// 分类用户意图。
    /// </summary>
    public async Task<IntentType> ClassifyAsync(string userInput, CancellationToken ct)
    {
        // 【关键】prompt 必须极短，0.8B 才能稳定输出。
        // 只给意图名 + 极简描述，不加"请""分析"这类词。
        var prompt = $"""
/no_think
分类任务，只输出一个英文词。

- SimpleQA：闲聊、概念解释、简单问答
- CodeGenerate：写代码、改代码
- FileProcess：操作文件、看目录、读文件
- Search：联网搜索、查最新信息
- ComplexReason：架构设计、多步规划、复杂分析
- Unknown：不确定

任务：{userInput}

只输出一个词：
""";

        // 温度 0，输出稳定
        var settings = new OpenAIPromptExecutionSettings
        {
            Temperature = 0.0
        };

        // 构造对话
        var history = new ChatHistory();
        history.AddUserMessage(prompt);

        // 调用 0.8B
        var response = await _chat.GetChatMessageContentAsync(history, settings, cancellationToken: ct);

        // 归一化
        var text = (response.Content ?? "").Trim().ToLowerInvariant();

        // 严格匹配 6 个意图词
        // 顺序注意：先匹配长的，再匹配短的，避免 ComplexReason 被 SimpleQA 抢先
        if (text.Contains("complexreason")) return IntentType.ComplexReason;
        if (text.Contains("codegenerate")) return IntentType.CodeGenerate;
        if (text.Contains("fileprocess")) return IntentType.FileProcess;
        if (text.Contains("simpleqa")) return IntentType.SimpleQA;
        if (text.Contains("search")) return IntentType.Search;
        if (text.Contains("unknown")) return IntentType.Unknown;

        // 无法识别，归 Unknown
        return IntentType.Unknown;
    }
}