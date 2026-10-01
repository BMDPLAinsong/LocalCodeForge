// 引入 System.Text.Json，用于 JsonElement 类型
using System.Text.Json;

// 命名空间：Models 文件夹下的所有类型
namespace Agent.Core.Models;

/// <summary>
/// Agent 在运行过程中向外发出的事件。
/// UI 层（CLI / VS Code 扩展 / 桌面端）订阅这些事件来展示过程。
/// 用抽象 record 作为基类，每个具体事件都是它的子类。
/// </summary>
public abstract record AgentEvent
{
    /// <summary>
    /// Agent 的思考过程，通常来自 LLM 的自然语言输出。
    /// </summary>
    /// <param name="Text">思考内容</param>
    public sealed record Thinking(string Text) : AgentEvent;

    /// <summary>
    /// Agent 决定调用某个工具。
    /// </summary>
    /// <param name="Name">工具名</param>
    /// <param name="Args">工具参数，原样透传 JSON</param>
    public sealed record ToolCall(string Name, JsonElement Args) : AgentEvent;

    /// <summary>
    /// 工具执行结果。
    /// </summary>
    /// <param name="Name">工具名</param>
    /// <param name="Output">工具返回的文本</param>
    /// <param name="Success">是否成功</param>
    public sealed record ToolResultEvent(string Name, string Output, bool Success) : AgentEvent;

    /// <summary>
    /// 任务完成，最终交付文本。
    /// 收到这个事件后，前端应停止等待，展示答案。
    /// </summary>
    /// <param name="Text">最终交付说明</param>
    public sealed record FinalAnswer(string Text) : AgentEvent;

    /// <summary>
    /// 发生错误。
    /// </summary>
    /// <param name="Message">错误信息</param>
    public sealed record Error(string Message) : AgentEvent;

    /// <summary>
    /// 路由决策事件。
    /// </summary>
    /// <param name="ServiceId">目标服务</param>
    /// <param name="Tier">命中的层级</param>
    /// <param name="Reason">原因</param>
    /// <param name="Intent">意图类型（可选）</param>
    public sealed record Routing(
        string ServiceId,
        string Tier,
        string Reason,
        string? Intent = null) : AgentEvent;

    /// <summary>
    /// 规划完成事件。
    /// </summary>
    /// <param name="Summary">整体目标</param>
    /// <param name="Language">语言</param>
    /// <param name="FilePaths">文件清单</param>
    public sealed record PlanGenerated(
        string Summary,
        string Language,
        List<string> FilePaths) : AgentEvent;

    /// <summary>
    /// 文件已写入事件。
    /// </summary>
    /// <param name="Path">文件路径</param>
    /// <param name="Size">文件大小（字符数）</param>
    public sealed record FileWritten(string Path, int Size) : AgentEvent;
}