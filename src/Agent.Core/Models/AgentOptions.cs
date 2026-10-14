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

    /// <summary>【10-14 A1 模型 A/B】Executor（逐文件生成/修复）的模型覆盖标签。
    /// null（默认）= 沿用 Small（law-unified）；非空 = 请求级覆盖 ModelId（Ollama /v1 接受任意已拉取的 tag）。
    /// 用于 A/B 对比更强的 coder 模型（如 qwen3-coder-local / task-expert / code-forge 7b），
    /// 不改动路由与生成逻辑——纯模型替换实验开关。</summary>
    public string? ExecutorModel { get; set; }

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

    /// <summary>【批次6 ⑧ 能力分层】工具按目标服务分层注入开关。
    /// true（默认）= 按 ToolLayering 表过滤：small 仅基础工具（read/write/edit/list/create/run），
    ///                分析类（find_symbol/analyze_code/dependency_analyzer）与记忆类（remember）仅
    ///                agent_pro/large 可见；false = 全量注入（兼容旧行为）。
    /// 分层是数据驱动（工具能力标签表，非提示词）；低层模型不暴露高阶工具防误用/乱用。</summary>
    public bool EnableToolLayering { get; set; } = true;

    // ─────────── 循环配置 ───────────

    /// <summary>
    /// 单次任务最大推理步数，防止 LLM 陷入死循环。
    /// </summary>
    public int MaxSteps { get; set; } = 20;

    /// <summary>
    /// 【10-09 读任务步数分级】读/分析类任务（ReadQuery 特征类，如"分析一下这个项目"）
    /// 的最大步数：探索（list_dir/read_file）+ 总结需要更多步，写任务保持 MaxSteps 防空转。
    /// 30B 读任务实测 20 步截断（2026-10-09 "分析这个项目" 359s/20步失败），默认放大到 40。
    /// </summary>
    public int ReadQueryMaxSteps { get; set; } = 40;

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
    /// 【路由调权收尾】调权参数配置化（此前硬编码在 ModelRouter 三个调用点）。
    /// 改调权敏感度只需改配置，无需改代码。
    /// </summary>
    /// <summary>L0.5 写降级 / L0.5b 读升级：最小样本数（低于则保持默认路径，不冒险）。</summary>
    public int RoutingTuningMinSamples { get; set; } = 3;
    /// <summary>L0.5 写降级：small 写任务最小成功率（达到且省时 → 降级 small）。</summary>
    public double RoutingDowngradeMinSuccessRate { get; set; } = 0.6;
    /// <summary>L0.5b 读升级：small 读任务坏率阈值（超过 → 考虑升级 agent_pro）。</summary>
    public double RoutingReadUpgradeBadRate { get; set; } = 0.6;
    /// <summary>L0.5b 读升级：agent_pro 最小成功率。</summary>
    public double RoutingReadUpgradeMinSuccessRate { get; set; } = 0.5;
    /// <summary>P2 ComplexReason 降级：最小样本数。</summary>
    public int RoutingDropAgentProMinSamples { get; set; } = 5;
    /// <summary>P2 ComplexReason 降级：最小成功率（低于 → 降级 large）。</summary>
    public double RoutingDropAgentProMinSuccessRate { get; set; } = 0.5;

    /// <summary>【10-08 ② 任务级超时】单任务整体超时（分钟）。0 = 禁用。
    /// 主循环任务开始时 taskCts.CancelAfter——与终端 q 键 / web POST /api/cancel 同一取消令牌，
    /// 超时后任务在阶段间响应取消 → cancelled → 照常走失败回滚承接。默认 30 分钟防失控任务永久卡队列。</summary>
    public int TaskTimeoutMinutes { get; set; } = 30;

    /// <summary>【10-08 C 项】本地 Ollama HttpClient 超时（分钟）。
    /// 此前硬编码 10 分钟（KernelFactory.cs），30B 本地推理大文件多轮任务实测被 600s 超时腰斩
    /// （10-04 BigService 890s 任务实证）；默认 20 分钟，让任务至少能跑到熔断诚实失败而非超时误杀。</summary>
    public int OllamaHttpTimeoutMinutes { get; set; } = 20;

    /// <summary>【10-14 P3-4】备份保留份数（BackupService.PruneBackups 的 keepLastN）。
    /// 原为调用点硬编码 8（Program.cs 启动清理），改配置需改代码——此处显式化。</summary>
    public int BackupKeepLastN { get; set; } = 8;

    /// <summary>【10-13 上下文预算】任务目标文件 X1 全文注入上限（字节）。
    /// ≤ 阈值：全文注入 + read_file 短路（单份，消双份——X1 40KB + read_file 40KB = 90KB
    /// 在 CPU 30B 上 prefill >20min 超时实证）；&gt; 阈值：骨架注入（路径+行数+前 50 行），
    /// 靠 read_file 分页定向。默认 40KB（600 行源码量级，单份 prefill ~93s 实测可接受）。</summary>
    public int ContextTargetInjectBytes { get; set; } = 40000;

    /// <summary>【10-13 上下文预算】read_file 工具结果注入上限（字节）。
    /// &gt; 阈值：头尾截断 + 行数提示（完整内容靠 offset 分页续读），防大文件一次撑爆上下文。</summary>
    public int ContextToolResultBytes { get; set; } = 25000;

    /// <summary>【10-13 上下文预算】工具结果累计注入阈值（字节）。
    /// 累计注入超阈值后，后续 read_file 结果改走【折叠摘要】（ContextFolder：结构定义行+头尾，
    /// 语义保留优于纯截断）；完整内容已在 TaskEventStore 全量持久化（事件存储 = cold 层，可回放）。
    /// 默认 90KB——单轮上下文 ≈ 系统提示 15KB + 任务注入 20KB + 工具结果 90KB 内受控。</summary>
    public int ContextFoldThreshold { get; set; } = 90000;

    /// <summary>【10-13 批次1】持久层生命周期：会话历史（turns 表）保留天数。
    /// 每轮对话全量明文（含生成内容），只增不清；默认 90 天启动清理（> 会话语义所需，
    /// 且 30B 多轮只带最近 MaxHistoryTurns 轮——旧轮次无运行时价值）。</summary>
    public int ChatTurnRetentionDays { get; set; } = 90;

    /// <summary>【10-13 批次1】持久层生命周期：任务事件（task_events 表）保留天数。
    /// Dashboard 详情回放依赖近 N 天事件；默认 30 天（此前硬编码 30，现配置化）。</summary>
    public int EventRetentionDays { get; set; } = 30;

    /// <summary>【10-13 批次1】持久层生命周期：路由决策明细（routing_decisions 表）保留天数。
    /// 调权仅用近 10 条聚合；默认 90 天（此前硬编码 90，现配置化）。</summary>
    public int RoutingDecisionRetentionDays { get; set; } = 90;

    /// <summary>【10-14 P5-27 双端统一】单条命令/任务输入长度上限（字符）。
    /// web /api/commands 与终端任务输入**同源同限**——原 web 硬编码 2000、终端无限制（同一产品两个入口行为不同）。
    /// 超限：web 返回 400；终端打印提示跳过。默认 8000（覆盖长需求粘贴，仍防上下文预算被单条输入打穿）。</summary>
    public int MaxCommandInputLength { get; set; } = 8000;

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