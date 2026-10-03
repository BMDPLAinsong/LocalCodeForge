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
    /// 小模型端点（law-unified：30B Custom Model，请求级 Temperature=0.3 确定性优先）。
    /// 承担 SimpleQA / CodeGenerate / FileProcess 三类任务的生成。
    /// 单模型三角色：small/large/agent_pro 共用同一 tag，角色参数由请求级 options 注入。
    /// </summary>
    public ModelEndpoint Small { get; set; } = new()
    {
        Provider = "ollama",
        Model = "law-unified",
        Endpoint = "http://localhost:11434",
        ApiKey = "ollama",
        Temperature = 0.3
    };

    /// <summary>
    /// 大模型端点（law-unified：30B Custom Model，请求级 Temperature=0.7 探索；
    /// 云端 DashScope 仅预留兜底，当前不使用云端）。
    /// 承担 Search / ComplexReason / Unknown 三类任务。
    /// </summary>
    public ModelEndpoint Large { get; set; } = new()
    {
        Provider = "ollama",
        Model = "law-unified",
        Endpoint = "http://localhost:11434",
        ApiKey = "ollama",
        Temperature = 0.7
    };

    /// <summary>
    /// Agent 专用端点（law-unified：30B Custom Model，请求级 Temperature=0.5 居中，
    /// 工程理解中介）。
    /// 承担复杂 Agent 任务：多文件项目、探索性需求、需要规划的任务。
    /// </summary>
    public ModelEndpoint AgentPro { get; set; } = new()
    {
        Provider = "ollama",
        Model = "law-unified",
        Endpoint = "http://localhost:11434",
        ApiKey = "ollama",
        Temperature = 0.5
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

    /// <summary>
    /// 【P0-2】expected_output 严格校验开关（Orchestrator 验证阶段）。
    /// false（默认）= 宽松：任一 stdout 行被 expected 包含即通过（防假阳，保持现状）；
    /// true         = 严格：expected 每一非空行都须在 stdout 中精确出现（消除"预期缺失仍判成功"）。
    /// 严格模式适合工程改动验证等要求逐项兑现的场景；普通生成任务建议保持宽松。
    /// </summary>
    public bool StrictOutputVerification { get; set; } = false;

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

    /// <summary>
    /// 按服务角色取模型端点（small / large / agent_pro / classifier）。
    /// 单模型三角色方案：各端点可配置不同 Temperature，请求时按当前服务注入。
    /// </summary>
    public ModelEndpoint? GetEndpoint(string serviceId) => serviceId switch
    {
        "small" => Small,
        "large" => Large,
        "agent_pro" => AgentPro,
        "classifier" => Classifier,
        _ => null
    };

    /// <summary>
    /// 按服务角色取生效温度：端点显式配置优先，未配置回退全局 Temperature。
    /// </summary>
    public double GetEffectiveTemperature(string serviceId) =>
        GetEndpoint(serviceId)?.Temperature ?? Temperature;

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

    // ─────────── 记忆治理 ───────────

    /// <summary>
    /// 【记忆治理收尾】自动过期清理阈值：
    /// 创建/最近命中早于 MemoryPruneDays 天且命中数 &lt; MemoryPruneMinHits 的条目，
    /// 在每次任务启动时自动删除。minHits=1 表示"仅 0 命中的条目可被清理"。
    /// </summary>
    public int MemoryPruneDays { get; set; } = 30;

    /// <summary>自动治理的最低命中数（命中数达到该值即使久未用也保留）。</summary>
    public int MemoryPruneMinHits { get; set; } = 1;

    /// <summary>
    /// 【时代隔离】模型时代切换点（law-unified 30B 落地时刻，本地时间）。
    /// stats 面板按此拆分 agent_pro 等服务的成功率（当前时代 vs 历史时代）。
    /// null = 不启用时代拆分。
    /// </summary>
    public DateTimeOffset? LawUnifiedEpoch { get; set; }

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

    /// <summary>
    /// 请求级采样温度（单模型三角色方案的运行时注入参数）。
    /// null = 未配置，回退 AgentOptions.Temperature 全局值；
    /// 非 null = 请求时以 OpenAIPromptExecutionSettings.Temperature 覆盖 Modelfile 默认。
    /// </summary>
    public double? Temperature { get; set; }
}