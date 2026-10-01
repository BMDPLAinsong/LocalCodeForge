// 命名空间声明
namespace Agent.Core.Models;

/// <summary>
/// 一次任务的输入参数。
/// 用 record 而不是 class，因为这是不可变的数据载体。
/// </summary>
/// <param name="SessionId">会话 ID，用于多轮对话，每次新会话生成一个 GUID</param>
/// <param name="Input">用户输入的自然语言需求</param>
/// <param name="WorkspaceRoot">工作区根目录，Agent 只能在此目录内操作文件</param>
public sealed record AgentRequest(
    string SessionId,
    string Input,
    string WorkspaceRoot);