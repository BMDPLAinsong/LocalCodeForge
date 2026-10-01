// 命名空间
namespace Agent.Core.Models;

/// <summary>
/// 持久化的一轮对话。存进 SQLite 的就是这个。
/// 【重要】Core 层零依赖，不引用 MEAI / SK 的任何类型。
/// 转换到 SK 的 ChatMessageContent 由 Infrastructure 层负责。
/// </summary>
/// <param name="Role">角色：user 或 assistant</param>
/// <param name="Content">文本内容</param>
public sealed record ChatTurn(string Role, string Content);