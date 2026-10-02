using Agent.Core.Models;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Agent.Infrastructure.Ollama;

/// <summary>
/// 任务粒度清场组件（单模型三角色方案配套）：
/// 工作台 30B 三角色（law-small / law-large / law-agentpro）共用同一底层模型，
/// 但不同 tag 在 Ollama 中是独立实例，同时常驻会占 3×19GB 内存（超出物理内存）。
/// 因此每个任务开始前 stop 全部 law-* 模型，保证任意时刻最多一份 30B 常驻；
/// 任务内由 24h 保活承担（agent 循环多轮调用不重载），任务间切换仅冷启动重载。
/// </summary>
public sealed class OllamaStopService
{
    private readonly HttpClient _http;
    private readonly ILogger<OllamaStopService> _logger;
    private readonly string[] _models;

    public OllamaStopService(AgentOptions options, ILogger<OllamaStopService> logger)
    {
        _logger = logger;
        var ollamaEndpoints = new[]
        {
            options.Classifier, options.Small, options.Large, options.AgentPro
        }
        .Where(e => e.Provider == "ollama")
        .ToArray();

        // 只清工作台 Custom Model（law-* 30B）；分类器 1.5b 等轻模型不清（常驻无妨）
        _models = ollamaEndpoints
            .Where(e => e.Model.StartsWith("law-"))
            .Select(e => e.Model)
            .Distinct()
            .ToArray();

        var endpoint = ollamaEndpoints.FirstOrDefault()?.Endpoint ?? "http://localhost:11434";
        _http = new HttpClient { BaseAddress = new Uri(endpoint) };
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    /// <summary>stop 全部 law-* 模型。未加载/Ollama 未启动均容忍（清场是尽力而为）。</summary>
    public async Task StopAllAsync(CancellationToken ct)
    {
        if (_models.Length == 0) return;
        foreach (var model in _models)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "/api/stop")
                {
                    Content = JsonContent.Create(new StopRequest(model))
                };
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                // 404=未加载、400=已在加载/卸载中，均属正常，忽略
                if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 404 && (int)resp.StatusCode != 400)
                {
                    _logger.LogDebug("stop {Model} 非预期状态 {Code}（容忍）", model, (int)resp.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("stop {Model} 失败（容忍）: {Msg}", model, ex.Message);
            }
        }
    }

    private sealed class StopRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; }
        public StopRequest(string model) => Model = model;
    }
}
