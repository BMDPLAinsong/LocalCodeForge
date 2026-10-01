// 命名空间
namespace Agent.Infrastructure.Routing;

/// <summary>
/// 意图分类器。用本地 0.8B 模型输出 IntentType。
/// </summary>
public interface IIntentClassifier
{
    /// <summary>
    /// 分类用户意图。
    /// 失败时抛异常，由上层兜底到 Unknown。
    /// </summary>
    /// <param name="userInput">用户输入</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>意图类型</returns>
    Task<IntentType> ClassifyAsync(string userInput, CancellationToken ct);
}