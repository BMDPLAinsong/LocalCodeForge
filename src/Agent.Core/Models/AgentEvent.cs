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
    [WireName("thinking")]
    public sealed record Thinking(string Text) : AgentEvent;

    /// <summary>
    /// Agent 决定调用某个工具。
    /// </summary>
    /// <param name="Name">工具名</param>
    /// <param name="Args">工具参数，原样透传 JSON</param>
    [WireName("toolCall")]
    public sealed record ToolCall(string Name, JsonElement Args) : AgentEvent;

    /// <summary>
    /// 工具执行结果。
    /// </summary>
    /// <param name="Name">工具名</param>
    /// <param name="Output">工具返回的文本</param>
    /// <param name="Success">是否成功</param>
    [WireName("toolResultEvent")]
    public sealed record ToolResultEvent(string Name, string Output, bool Success) : AgentEvent;

    /// <summary>
    /// 任务完成，最终交付文本。
    /// 收到这个事件后，前端应停止等待，展示答案。
    /// </summary>
    /// <param name="Text">最终交付说明</param>
    [WireName("finalAnswer")]
    public sealed record FinalAnswer(string Text) : AgentEvent;

    /// <summary>
    /// 发生错误。
    /// </summary>
    /// <param name="Message">错误信息</param>
    [WireName("error")]
    public sealed record Error(string Message) : AgentEvent;

    /// <summary>
    /// 路由决策事件。
    /// </summary>
    /// <param name="ServiceId">目标服务</param>
    /// <param name="Tier">命中的层级</param>
    /// <param name="Reason">原因</param>
    /// <param name="Intent">意图类型（可选）</param>
    [WireName("routing")]
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
    [WireName("planGenerated")]
    public sealed record PlanGenerated(
        string Summary,
        string Language,
        List<string> FilePaths) : AgentEvent;

    /// <summary>
    /// 文件已写入事件。
    /// </summary>
    /// <param name="Path">文件路径</param>
    /// <param name="Size">文件大小（字符数）</param>
    [WireName("fileWritten")]
    public sealed record FileWritten(string Path, int Size) : AgentEvent;
}

/// <summary>
/// 事件在 wire（JSON/SSE）层的短名标记（【10-14 P2-2】）。
/// record 序列化不带 type 判别字段，UI 层需补短名供前端单入口渲染——此前短名表在 UI 层用
/// nameof 手工维护（新增/重命名事件需手工同步两处，漏改则该事件两视图不一致）。
/// 标记与类型声明同处一地：映射随类型走，编译期可见，无法漂移；未标记类型兜底用类名原样（不吞事件）。
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class WireNameAttribute : Attribute
{
    /// <summary>wire 层短名（如 toolCall）。</summary>
    public string Name { get; }

    /// <summary>构造函数。</summary>
    public WireNameAttribute(string name) => Name = name;
}