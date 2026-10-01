// 引入自研工具接口
using Agent.Core.Abstractions;

// 命名空间
namespace Agent.Infrastructure;

/// <summary>
/// 工具注册表的默认实现。
/// 用字典保存所有工具，按名字查找。
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    // 内部字典，key 是工具名，value 是工具实例
    private readonly Dictionary<string, IAgentTool> _tools;

    /// <summary>构造函数，接收所有工具。</summary>
    public ToolRegistry(IEnumerable<IAgentTool> tools)
    {
        // 以工具名作为 key 建立字典，忽略大小写
        _tools = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>所有工具。</summary>
    public IReadOnlyCollection<IAgentTool> All => _tools.Values;

    /// <summary>按名字查找工具。</summary>
    public bool TryGet(string name, out IAgentTool tool)
    {
        // 直接从字典取
        return _tools.TryGetValue(name, out tool!);
    }
}