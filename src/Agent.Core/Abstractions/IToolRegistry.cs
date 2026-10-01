// 命名空间
namespace Agent.Core.Abstractions;

/// <summary>
/// 工具注册表。管理当前可用的全部工具。
/// </summary>
public interface IToolRegistry
{
    /// <summary>
    /// 所有已注册的工具。AgentLoop 会把它们全部交给 LLM。
    /// </summary>
    IReadOnlyCollection<IAgentTool> All { get; }

    /// <summary>
    /// 按名字查找工具。AgentLoop 收到 LLM 的工具调用请求时用它。
    /// </summary>
    /// <param name="name">工具名</param>
    /// <param name="tool">找到的工具</param>
    /// <returns>是否找到</returns>
    bool TryGet(string name, out IAgentTool tool);
}