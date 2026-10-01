// ============================================================
// KernelFactory.cs
// 根据 AgentOptions 构建 Kernel，注册四个命名 ChatCompletion 服务。
// ============================================================

using Microsoft.SemanticKernel;
using Agent.Core.Models;
using Agent.Infrastructure.Routing;

namespace Agent.Infrastructure;

/// <summary>
/// Kernel 工厂。
/// </summary>
public static class KernelFactory
{
    /// <summary>根据配置创建 Kernel 实例。</summary>
    public static Kernel Create(AgentOptions options)
    {
        var builder = Kernel.CreateBuilder();

        RegisterEndpoint(builder, RoutingDecision.Classifier, options.Classifier);
        RegisterEndpoint(builder, RoutingDecision.Small, options.Small);
        RegisterEndpoint(builder, RoutingDecision.Large, options.Large);
        RegisterEndpoint(builder, RoutingDecision.AgentPro, options.AgentPro);

        return builder.Build();
    }

    /// <summary>注册一个命名的 ChatCompletion 服务。</summary>
    private static void RegisterEndpoint(
        IKernelBuilder builder,
        string serviceId,
        ModelEndpoint endpoint)
    {
        switch (endpoint.Provider?.ToLowerInvariant())
        {
            // ─────────── Ollama（本地） ───────────
            case "ollama":
                var ollamaKey = string.IsNullOrWhiteSpace(endpoint.ApiKey) ? "ollama" : endpoint.ApiKey;

                // 【关键】本地模型用 10 分钟超时的 HttpClient
                // 9B 模型 + 多轮生成，100 秒默认超时远远不够
                var ollamaHttp = new HttpClient
                {
                    Timeout = TimeSpan.FromMinutes(10)
                };

                builder.AddOpenAIChatCompletion(
                    modelId: endpoint.Model,
                    apiKey: ollamaKey,
                    endpoint: new Uri($"{endpoint.Endpoint.TrimEnd('/')}/v1"),
                    serviceId: serviceId,
                    httpClient: ollamaHttp);
                break;

            // ─────────── OpenAI 兼容（云端） ───────────
            case "openai":
                if (string.IsNullOrWhiteSpace(endpoint.ApiKey))
                {
                    throw new InvalidOperationException(
                        $"服务 {serviceId} 的 Provider 为 openai，但 ApiKey 为空。");
                }

                // 云端也用长超时，避免长时间生成被中断
                var cloudHttp = new HttpClient
                {
                    Timeout = TimeSpan.FromMinutes(5)
                };

                builder.AddOpenAIChatCompletion(
                    modelId: endpoint.Model,
                    apiKey: endpoint.ApiKey,
                    endpoint: new Uri(endpoint.Endpoint),
                    serviceId: serviceId,
                    httpClient: cloudHttp);
                break;

            default:
                throw new InvalidOperationException(
                    $"服务 {serviceId} 的 Provider 不支持：{endpoint.Provider}。");
        }
    }
}