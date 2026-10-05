// 命名空间声明
namespace Agent.Core.Models;

/// <summary>
/// 一次任务的输入参数。
/// 用 record 而不是 class，因为这是不可变的数据载体。
/// </summary>
/// <param name="SessionId">会话 ID，用于多轮对话，每次新会话生成一个 GUID</param>
/// <param name="Input">用户输入的自然语言需求</param>
/// <param name="WorkspaceRoot">工作区根目录，Agent 只能在此目录内操作文件</param>
/// <param name="ExplicitWriteTarget">批次1·E：CLI 层解析并授权的用户显式写目标（"X盘根目录 test.txt" → X:\test.txt）；未授权为 null</param>
/// <param name="ExplicitWriteTargetIsFile">显式写目标是否为文件级（目录级按 Directory.Exists 验证）</param>
public sealed record AgentRequest(
    string SessionId,
    string Input,
    string WorkspaceRoot,
    string? ExplicitWriteTarget = null,
    bool ExplicitWriteTargetIsFile = false);