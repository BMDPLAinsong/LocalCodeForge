// 引入 JSON 类型
using System.Text.Json;

// 命名空间
namespace Agent.Core.Abstractions;

/// <summary>
/// Agent 可调用的一个工具。
/// 所有工具（文件读写、命令执行、后续的 MCP 工具）都实现这个接口。
/// </summary>
public interface IAgentTool
{
    /// <summary>
    /// 工具名。LLM 通过这个名字来调用工具。
    /// 建议用蛇形命名，如 read_file、write_file。
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 工具描述。LLM 据此判断什么时候该调用这个工具。
    /// 描述要精准，这是 LLM 选择工具的唯一依据。
    /// </summary>
    string Description { get; }

    /// <summary>
    /// 参数的 JSON Schema。
    /// LLM 会按这个 Schema 生成参数。
    /// </summary>
    JsonElement InputSchema { get; }

    /// <summary>
    /// 执行工具。
    /// </summary>
    /// <param name="args">LLM 按 Schema 生成的参数</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>结构化工具结果（Message 即给 LLM 看的文本输出，文本逐字兼容旧契约）</returns>
    Task<Agent.Core.Models.ToolResult> InvokeAsync(JsonElement args, CancellationToken ct);
}