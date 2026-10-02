// ============================================================
// AgentOptions.cs
// Agent 的行为配置。所有可调参数集中在这里。
// ============================================================

// 命名空间
namespace Agent.Core.Models;

/// <summary>
/// Agent 的行为配置。
/// </summary>
public sealed class AgentOptions
{
    // ─────────── 模型端点配置 ───────────

    /// <summary>
    /// 意图分类器端点（本地 0.8B / 1.5B）。
    /// 只做意图分类，不做生成，速度越快越好。
    /// </summary>
    public ModelEndpoint Classifier { get; set; } = new()
    {
        Provider = "ollama",
        Model = "qwen2.5:1.5b",
        Endpoint = "http://localhost:11434",
        ApiKey = "ollama"
    };

    /// <summary>
    /// 小模型端点（law-small：30B Custom Model，temp 0.3，单文件/轻任务确定性优先）。
    /// 承担 SimpleQA / CodeGenerate / FileProcess 三类任务的生成。
    /// 单模型三角色：small/large/agent_pro 共用同一底层 30B，Modelfile 各自定制，运行时任务粒度清场单驻。
    /// </summary>
    public ModelEndpoint Small { get; set; } = new()
    {
        Provider = "ollama",
        Model = "law-small",
        Endpoint = "http://localhost:11434",
        ApiKey = "ollama"
    };

    /// <summary>
    /// 大模型端点（law-large：30B Custom Model，temp 0.7，复杂推理/多文件/规划；
    /// 云端 DashScope 仅预留兜底，当前不使用云端）。
    /// 承担 Search / ComplexReason / Unknown 三类任务。
    /// </summary>
    public ModelEndpoint Large { get; set; } = new()
    {
        Provider = "ollama",
        Model = "law-large",
        Endpoint = "http://localhost:11434",
        ApiKey = "ollama"
    };

    /// <summary>
    /// Agent 专用端点（law-agentpro：30B Custom Model，temp 0.5，工程理解中介；
    /// 由 4B 升级，补齐工程理解能力短板）。
    /// 承担复杂 Agent 任务：多文件项目、探索性需求、需要规划的任务。
    /// </summary>
    public ModelEndpoint AgentPro { get; set; } = new()
    {
        Provider = "ollama",
        Model = "law-agentpro",
        Endpoint = "http://localhost:11434",
        ApiKey = "ollama"
    };

    // ─────────── 路由配置 ───────────

    /// <summary>
    /// 是否启用智能路由。
    /// true  = 三层漏斗（规则 → 意图分类 → 服务分发）
    /// false = 全部走 Large
    /// </summary>
    public bool EnableRouting { get; set; } = true;

    /// <summary>
    /// 是否将 ComplexReason 意图路由到本地 AgentPro（Qwen3.5:4b）。
    /// true  = 走本地 AgentPro（省钱，速度略慢）
    /// false = 走 Large 云端（保质量，消耗 token）
    /// 默认 true。
    /// </summary>
    public bool ComplexToPro { get; set; } = true;

    // ─────────── 循环配置 ───────────

    /// <summary>
    /// 单次任务最大推理步数，防止 LLM 陷入死循环。
    /// </summary>
    public int MaxSteps { get; set; } = 20;

    /// <summary>
    /// 历史对话携带的最大轮数。
    /// </summary>
    public int MaxHistoryTurns { get; set; } = 10;

    /// <summary>
    /// 采样温度。写代码建议低一些。
    /// </summary>
    public double Temperature { get; set; } = 0.2;

    // ─────────── Prompt ───────────

    /// <summary>
    /// 系统提示词。
    /// 属性名用 SystemPromptText，避免和 SystemPrompt 类名冲突。
    /// </summary>
    public string SystemPromptText { get; set; } = SystemPrompt.Default;

    // ─────────── 规划 ───────────

    /// <summary>
    /// 单个文件的 Executor 最大重试次数（修复编译错误）。
    /// </summary>
    public int ExecutorMaxRetries { get; set; } = 3;

    // ─────────── 工程感知 ───────────

    /// <summary>
    /// 工程根目录（工程感知 P0，可选）。
    /// null = 沙箱模式（默认，工具只读写任务目录）；
    /// 非 null = 工程模式（只读工具可感知工程文件，任务上下文注入工程数据块）。
    /// </summary>
    public string? ProjectRoot { get; set; }

    /// <summary>
    /// 工程可写授权（工程感知 P1）。
    /// false = 工程模式只读（写工具仍锁任务目录）；
    /// true = 写工具可在工程根内创建/修改文件（改动前自动备份，CLI rollback 可恢复）。
    /// 仅 ProjectRoot 非 null 时生效；开启方式：use --write &lt;路径&gt; 或 --project &lt;路径&gt; --write。
    /// </summary>
    public bool ProjectWriteEnabled { get; set; }
}

/// <summary>
/// 单个模型端点的配置。
/// </summary>
public sealed class ModelEndpoint
{
    /// <summary>
    /// 提供方：ollama 或 openai。
    /// </summary>
    public string Provider { get; set; } = "ollama";

    /// <summary>
    /// 模型名。Ollama 模式填本地模型名，OpenAI 模式填服务方模型名。
    /// </summary>
    public string Model { get; set; } = "";

    /// <summary>
    /// 服务地址。
    /// Ollama: http://localhost:11434
    /// DashScope: https://dashscope.aliyuncs.com/compatible-mode/v1
    /// </summary>
    public string Endpoint { get; set; } = "";

    /// <summary>
    /// API Key。仅 OpenAI 兼容模式需要。Ollama 模式填任意非空字符串。
    /// </summary>
    public string ApiKey { get; set; } = "";
}