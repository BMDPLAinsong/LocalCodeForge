// ============================================================
// OllamaEmbedder.cs
// 【10-18 P2-4】本地 Ollama OpenAI 兼容 /v1/embeddings 的实现。
// 默认模型 nomic-embed-text（137M F16，768 维）——本机已拉取，与 law-unified
// 30B 主模型分离（记忆检索只跑轻量 embedding，不占生成算力）。
// 确定性降级：HTTP 失败/超时/维度不符 → Embed 返回 null（调用方跳过向量通道）。
// ============================================================

using System.Net.Http.Json;
using System.Text.Json;
using Agent.Core.Models;

namespace Agent.Infrastructure.Memory;

/// <summary>
/// 基于 Ollama OpenAI 兼容端点的文本嵌入器（POST {endpoint}/v1/embeddings）。
/// </summary>
public sealed class OllamaEmbedder : IEmbedder
{
    private readonly HttpClient _http;
    private readonly string _endpoint;

    /// <summary>模型名（来自 AgentOptions.Embedding.Model）。</summary>
    public string Model { get; }

    /// <summary>期望维度：nomic-embed-text 为 768。维度不符 → 视为失败（防模型换用后静默错位）。</summary>
    public int Dimensions { get; } = 768;

    /// <summary>短超时：embedding 只在写路径/检索路径尽力而为，不允许拖慢任务主流程。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public OllamaEmbedder(ModelEndpoint endpoint)
    {
        Model = endpoint.Model;
        _endpoint = endpoint.Endpoint.TrimEnd('/');
        _http = new HttpClient { Timeout = Timeout };
    }

    /// <inheritdoc />
    public float[]? Embed(string text)
    {
        try
        {
            using var resp = _http.PostAsJsonAsync(
                $"{_endpoint}/v1/embeddings",
                new { model = Model, input = text }).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            var data = doc.RootElement.GetProperty("data");
            if (data.GetArrayLength() == 0)
                return null;

            var arr = data[0].GetProperty("embedding");
            var vec = new float[arr.GetArrayLength()];
            var i = 0;
            foreach (var e in arr.EnumerateArray())
                vec[i++] = e.GetSingle();
            return vec.Length == Dimensions ? vec : null;
        }
        catch
        {
            // 服务离线/超时/解析失败 → 降级（调用方保持倒排检索）
            return null;
        }
    }
}
