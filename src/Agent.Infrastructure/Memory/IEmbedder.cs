// ============================================================
// IEmbedder.cs
// 【10-18 P2-4】记忆向量（语义）检索的嵌入器抽象。
// AgentMemoryStore 只依赖此接口：可测试（FakeEmbedder）、可降级
// （Embed 返回 null = 服务不可用 → 调用方跳过向量通道，纯 token 倒排照常）。
// ============================================================

namespace Agent.Infrastructure.Memory;

/// <summary>
/// 文本嵌入器。Embed 失败（服务离线/超时/维度不符）返回 null，不抛异常。
/// </summary>
public interface IEmbedder
{
    /// <summary>embedding 模型名（记录在 memory_vectors.model，防语义/维度漂移）。</summary>
    string Model { get; }

    /// <summary>期望向量维度（与模型输出核对，不符视为失败降级）。</summary>
    int Dimensions { get; }

    /// <summary>生成文本向量；失败返回 null。</summary>
    float[]? Embed(string text);
}
