// ============================================================
// Plan.cs
// Planner 输出的计划结构。
// 属性名用 PascalCase，通过 JsonPropertyName 映射 JSON 的 snake_case。
// ============================================================

using System.Text.Json.Serialization;

namespace Agent.Core.Models;

/// <summary>
/// 规划器输出的完整实现计划。
/// </summary>
public sealed class Plan
{
    /// <summary>一句话总结整体目标。</summary>
    [JsonPropertyName("summary")]
    public string Summary { get; init; } = "";

    /// <summary>编程语言：python / csharp / javascript。</summary>
    [JsonPropertyName("language")]
    public string Language { get; init; } = "";

    /// <summary>要创建的文件清单（有序，顺序即依赖顺序）。</summary>
    [JsonPropertyName("files")]
    public List<FileSpec> Files { get; init; } = new();

    /// <summary>验证命令，如 python main.py。</summary>
    [JsonPropertyName("run_command")]
    public string RunCommand { get; init; } = "";

    /// <summary>期望输出摘要，供 Validator 参考。</summary>
    [JsonPropertyName("expected_output")]
    public string ExpectedOutput { get; init; } = "";

    /// <summary>
    /// 环境约束数据块（外部数据任务时由 Orchestrator 注入，非 Planner 输出）。
    /// Executor 生成 / 修复文件时附加到用户消息。
    /// </summary>
    public string EnvironmentConstraint { get; set; } = "";
}

/// <summary>
/// 单个文件的规格。
/// </summary>
public sealed class FileSpec
{
    /// <summary>相对路径，如 calc/operations.py。</summary>
    [JsonPropertyName("path")]
    public string Path { get; init; } = "";

    /// <summary>文件职责，一句话。</summary>
    [JsonPropertyName("purpose")]
    public string Purpose { get; init; } = "";

    /// <summary>具体要求，分号分隔。</summary>
    [JsonPropertyName("requirements")]
    public string Requirements { get; init; } = "";

    /// <summary>可选：关键函数签名，供 Executor 参考。</summary>
    [JsonPropertyName("skeleton")]
    public string? Skeleton { get; init; }
}