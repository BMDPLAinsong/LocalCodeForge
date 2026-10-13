// 引入 JSON 类型
using System.Text.Json;
// 引入自研工具接口
using Agent.Core.Abstractions;

// 命名空间
namespace Agent.Infrastructure.Tools;

/// <summary>
/// 工具基类。封装 InputSchema 的解析与缓存，避免每个工具重复写。
/// 所有具体工具继承它，只需实现 Name / Description / SchemaJson / InvokeAsync。
/// </summary>
public abstract class AgentToolBase : IAgentTool
{
    // 缓存解析后的 JsonElement，避免每次调用都重新解析
    private JsonElement? _cachedSchema;

    /// <summary>工具名，子类实现。</summary>
    public abstract string Name { get; }

    /// <summary>工具描述，子类实现。</summary>
    public abstract string Description { get; }

    /// <summary>JSON Schema 的字符串形式，子类实现。</summary>
    protected abstract string SchemaJson { get; }

    /// <summary>
    /// 参数的 JSON Schema。
    /// 首次访问时解析并缓存，之后直接返回缓存。
    /// </summary>
    public JsonElement InputSchema
    {
        get
        {
            // 如果还没解析过
            if (_cachedSchema is null)
            {
                // 解析 JSON 字符串，克隆 RootElement 以便长期持有
                _cachedSchema = JsonDocument.Parse(SchemaJson).RootElement.Clone();
            }
            // 返回缓存
            return _cachedSchema.Value;
        }
    }

    /// <summary>执行工具，子类实现。</summary>
    public abstract Task<Agent.Core.Models.ToolResult> InvokeAsync(JsonElement args, CancellationToken ct);
}