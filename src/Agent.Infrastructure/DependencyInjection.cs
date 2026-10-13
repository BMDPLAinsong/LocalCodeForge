// ============================================================
// DependencyInjection.cs
// Agent 相关的 DI 装配。
// ============================================================

// 引入 DI
// 引入 Core
using Agent.Core.Abstractions;
using Agent.Core.Models;
using Agent.Infrastructure.Metrics;
using Agent.Infrastructure.Memory;
using Agent.Infrastructure.Orchestration;
using Agent.Infrastructure.Project;
using Agent.Infrastructure.Quality;
// 引入 Infrastructure
using Agent.Infrastructure.Routing;
using Agent.Infrastructure.Session;
using Agent.Infrastructure.Tools;
using Microsoft.Extensions.DependencyInjection;
// 引入日志
using Microsoft.Extensions.Logging;
// 引入 SK
using Microsoft.SemanticKernel;
// 引入 SK 聊天服务
using Microsoft.SemanticKernel.ChatCompletion;

// 命名空间
namespace Agent.Infrastructure;

/// <summary>
/// 依赖注入的装配扩展。
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// 注册 Agent 相关的全部服务。
    /// </summary>
    public static IServiceCollection AddAgent(
        this IServiceCollection services,
        AgentOptions options,
        string workspaceRoot,
        string dbPath,
        string? projectRoot = null,
        bool projectWriteEnabled = false,
        string? taskId = null,
        IEnumerable<string>? explicitWriteRoots = null)
    {
        // ─────────── 配置 ───────────
        services.AddSingleton(options);

        // ─────────── 日志 ───────────
        services.AddLogging(b =>
        {
            b.AddConsole();
            b.SetMinimumLevel(LogLevel.Information);
        });

        // ─────────── Kernel ───────────
        var kernel = KernelFactory.Create(options);
        services.AddSingleton(kernel);

        // ─────────── 路由：Layer 1 ───────────
        services.AddSingleton<IRuleEngine>(sp =>
        {
            var rulesPath = Path.Combine(AppContext.BaseDirectory, "Routing", "RoutingRules.json");
            var logger = sp.GetRequiredService<ILogger<RuleEngine>>();
            return new RuleEngine(rulesPath, logger);
        });

        // ─────────── 路由：Layer 2 ───────────
        // 【批次7 ⑨ 单模型串行】L2 意图分类去模型化：注册确定性分类器（纯规则词表，无模型调用）——
        // 热路径不再调 classifier（qwen2.5:1.5b），任务内模型调用收敛为主模型单模型串行。
        // 回退：换回 `return new IntentClassifier(chat)` 即恢复模型版（类文件保留）。
        services.AddSingleton<IIntentClassifier>(_ => new DeterministicIntentClassifier());

        // ─────────── 路由：Layer 3 ───────────
        // 【关键】要把 AgentOptions 注入进去（AgentPro 分流用）
        // ─────────── 3A：路由决策落库（调权数据地基） ───────────
        // routing_decisions 表放 metrics.db 同库：3B 调权需与 task_metrics 关联（成功率 vs 路由）。
        services.AddSingleton<Routing.RoutingDecisionStore>(_ =>
        {
            var storeDbPath = Path.Combine(
                Path.GetDirectoryName(dbPath) ?? ".",
                "metrics.db");
            return new Routing.RoutingDecisionStore(storeDbPath);
        });

        // ─────────── 路由：三层漏斗 ───────────
        services.AddSingleton<IModelRouter>(sp =>
        {
            var rules = sp.GetRequiredService<IRuleEngine>();
            var classifier = sp.GetRequiredService<IIntentClassifier>();
            var logger = sp.GetRequiredService<ILogger<ModelRouter>>();
            var opts = sp.GetRequiredService<AgentOptions>();
            var metrics = sp.GetRequiredService<IMetricsCollector>();
            var decisions = sp.GetRequiredService<Routing.RoutingDecisionStore>();
            return new ModelRouter(rules, classifier, logger, opts, metrics, decisions);
        });

        // ─────────── 【关键】工具（少了这几行就报这个错） ───────────
        // 工程感知 P0/P1：只读工具可感知工程根（双根）；写工具在 --write 授权下可写工程根（改前备份）
        // 【C】拒绝事件落库：ToolRejectionStore 单例（metrics.db 同库），WriteFileTool/EditFileTool 注入
        services.AddSingleton<Metrics.ToolRejectionStore>(_ =>
        {
            var rejectDbPath = Path.Combine(
                Path.GetDirectoryName(dbPath) ?? ".",
                "metrics.db");
            return new Metrics.ToolRejectionStore(rejectDbPath);
        });
        services.AddSingleton<IAgentTool>(sp => new ListDirTool(workspaceRoot, projectRoot));
        services.AddSingleton<IAgentTool>(sp => new ReadFileTool(workspaceRoot, projectRoot));
        services.AddSingleton<IAgentTool>(sp => new WriteFileTool(workspaceRoot, projectRoot, projectWriteEnabled, taskId, sp.GetRequiredService<Metrics.ToolRejectionStore>(), explicitWriteRoots, sp.GetRequiredService<AgentMemoryStore>()));
        services.AddSingleton<IAgentTool>(sp => new EditFileTool(workspaceRoot, projectRoot, projectWriteEnabled, taskId, sp.GetRequiredService<Metrics.ToolRejectionStore>(), explicitWriteRoots));    // ← 新增
        services.AddSingleton<IAgentTool>(_ => new CreateDirTool(workspaceRoot, projectRoot, projectWriteEnabled, explicitWriteRoots));   // ← 新增
        services.AddSingleton<IAgentTool>(_ => new RunCommandTool(workspaceRoot, projectRoot, projectWriteEnabled));
        services.AddSingleton<IAgentTool>(_ => new RememberTool(_.GetRequiredService<AgentMemoryStore>()));   // ← P3：模型级记忆
        services.AddSingleton<IAgentTool>(_ => new CodeAnalyzerTool(workspaceRoot, projectRoot));            // ← 工程理解专项：符号索引
        services.AddSingleton<IAgentTool>(_ => new FindSymbolTool(workspaceRoot, projectRoot,                 // ← 工程图谱：跨文件定位
            Path.Combine(Path.GetDirectoryName(dbPath) ?? ".", "symbol_index.db")));
        services.AddSingleton<IAgentTool>(_ => new DependencyAnalyzerTool(workspaceRoot, projectRoot,        // ← 工程图谱增强：依赖分析
            Path.Combine(Path.GetDirectoryName(dbPath) ?? ".", "symbol_index.db")));

        // ─────────── 【关键】工具注册表（依赖上面的 IAgentTool 集合） ───────────
        services.AddSingleton<IToolRegistry>(sp =>
            new ToolRegistry(sp.GetServices<IAgentTool>()));

        // ─────────── Lint 服务 ───────────
        services.AddSingleton<ILintService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<PythonLintService>>();
            return new PythonLintService(logger);
        });

        // ─────────── 指标采集 ───────────
        services.AddSingleton<IMetricsCollector>(_ =>
        {
            // metrics.db 放在和 sessions.db 同目录
            var metricsDbPath = Path.Combine(
                Path.GetDirectoryName(dbPath) ?? ".",
                "metrics.db");
            return new SqliteMetricsCollector(metricsDbPath);
        });

        // ─────────── D2：任务特征类历史表现聚合器（路由数据校准） ───────────
        // 启动时由 CLI 显式 Load() 一次，供 Orchestrator 重试上限校准 + stats 归因。
        services.AddSingleton<Routing.TaskMetricsAggregator>(_ =>
        {
            var metricsDbPath = Path.Combine(
                Path.GetDirectoryName(dbPath) ?? ".",
                "metrics.db");
            return new Routing.TaskMetricsAggregator(metricsDbPath);
        });

        // ─────────── 工程上下文缓存（工程感知 P2） ───────────
        services.AddSingleton<ProjectContextStore>(_ =>
        {
            // project_context.db 放在和 sessions.db 同目录
            var ctxDbPath = Path.Combine(
                Path.GetDirectoryName(dbPath) ?? ".",
                "project_context.db");
            return new ProjectContextStore(ctxDbPath);
        });

        // ─────────── 模型级记忆（工程感知 P3） ───────────
        services.AddSingleton<AgentMemoryStore>(_ =>
        {
            // agent_memory.db 放在和 sessions.db 同目录
            var memDbPath = Path.Combine(
                Path.GetDirectoryName(dbPath) ?? ".",
                "agent_memory.db");
            return new AgentMemoryStore(memDbPath);
        });

        // ─────────── 会话存储 ───────────
        services.AddSingleton<ISessionStore>(_ => new SqliteSessionStore(dbPath));

        // 在注册 IAgentLoop 之前加这两行
        services.AddSingleton<IPlannerService, PlannerService>();
        services.AddSingleton<IExecutorService, ExecutorService>();
        // 【新增】注册 Orchestrator
        // Orchestrator 用装饰器包装
        services.AddSingleton<Orchestrator>();
        services.AddSingleton<IOrchestrator>(sp =>
        {
            var inner = sp.GetRequiredService<Orchestrator>();
            var metrics = sp.GetRequiredService<IMetricsCollector>();
            var logger = sp.GetRequiredService<ILogger<OrchestratorMetricsDecorator>>();
            return new OrchestratorMetricsDecorator(inner, metrics, logger);
        });

        // ─────────── 主循环 ───────────
        // AgentLoop 用装饰器包装
        services.AddSingleton<AgentLoop>();
        services.AddSingleton<IAgentLoop>(sp =>
        {
            var inner = sp.GetRequiredService<AgentLoop>();
            var metrics = sp.GetRequiredService<IMetricsCollector>();
            var logger = sp.GetRequiredService<ILogger<AgentLoopMetricsDecorator>>();
            return new AgentLoopMetricsDecorator(inner, metrics, logger);
        });

        return services;
    }
}