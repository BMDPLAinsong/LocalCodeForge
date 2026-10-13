// ============================================================
// RememberTool.cs
// 模型级跨任务记忆（工程感知 P3）：
// 模型在 ReAct 循环中发现重要事实/决策时调用 remember(topic, content) 写入
// AgentMemoryStore，后续任务自动按输入检索注入——任务间真正"记住"。
// ============================================================

using System.Text.Json;

namespace Agent.Infrastructure.Tools;

using Agent.Infrastructure.Memory;

/// <summary>
/// 跨任务记忆写入工具。
/// </summary>
public sealed class RememberTool : AgentToolBase
{
    private readonly AgentMemoryStore _store;

    /// <summary>构造函数。</summary>
    public RememberTool(AgentMemoryStore store)
    {
        _store = store;
    }

    /// <summary>工具名。</summary>
    public override string Name => "remember";

    /// <summary>工具描述。</summary>
    public override string Description =>
        "把当前任务中发现的重要事实/决策存入跨任务记忆（topic 短标签，content 事实描述）。" +
        "后续任务会自动检索到相关记忆并注入上下文。" +
        "适合：工程结构关键点、API 用法、配置项含义、失败原因、已做的技术决策。";

    /// <summary>参数 Schema。</summary>
    protected override string SchemaJson => """
    {
      "type": "object",
      "properties": {
        "topic": {
          "type": "string",
          "description": "记忆短标签（如：ProjectRoot 属性含义）"
        },
        "content": {
          "type": "string",
          "description": "事实描述（如：ProjectRoot 为 null 时沙箱模式，非 null 时工程模式）"
        }
      },
      "required": ["topic", "content"]
    }
    """;

    /// <summary>执行工具。</summary>
    public override Task<Agent.Core.Models.ToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var topic = args.TryGetProperty("topic", out var t) ? t.GetString() : null;
        var content = args.TryGetProperty("content", out var c) ? c.GetString() : null;
        if (string.IsNullOrWhiteSpace(topic) || string.IsNullOrWhiteSpace(content))
            return Task.FromResult(Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.InvalidArg, "错误：topic 和 content 都不能为空。"));

        // 【任务5】决策记忆按主题打特征类标签（跨类注入时降权保留）
        _store.Add("model", topic, content,
            feature: Agent.Infrastructure.Routing.TaskFeatureClassifier
                .Classify(topic ?? "").ToString());
        return Task.FromResult(Agent.Core.Models.ToolResult.Ok($"已记住：{topic.Trim()}"));
    }
}
