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
        bool projectWriteEnabled = false)
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
        services.AddSingleton<IIntentClassifier>(sp =>
        {
            var k = sp.GetRequiredService<Kernel>();
            var chat = k.GetRequiredService<IChatCompletionService>(RoutingDecision.Classifier);
            return new IntentClassifier(chat);
        });

        // ─────────── 路由：Layer 3 ───────────
        // 【关键】要把 AgentOptions 注入进去（AgentPro 分流用）
        services.AddSingleton<IModelRouter>(sp =>
        {
            var rules = sp.GetRequiredService<IRuleEngine>();
            var classifier = sp.GetRequiredService<IIntentClassifier>();
            var logger = sp.GetRequiredService<ILogger<ModelRouter>>();
            var opts = sp.GetRequiredService<AgentOptions>();
            var metrics = sp.GetRequiredService<IMetricsCollector>();
            return new ModelRouter(rules, classifier, logger, opts, metrics);
        });

        // ─────────── 【关键】工具（少了这几行就报这个错） ───────────
        // 工程感知 P0/P1：只读工具可感知工程根（双根）；写工具在 --write 授权下可写工程根（改前备份）
        services.AddSingleton<IAgentTool>(_ => new ListDirTool(workspaceRoot, projectRoot));
        services.AddSingleton<IAgentTool>(_ => new ReadFileTool(workspaceRoot, projectRoot));
        services.AddSingleton<IAgentTool>(_ => new WriteFileTool(workspaceRoot, projectRoot, projectWriteEnabled));
        services.AddSingleton<IAgentTool>(_ => new EditFileTool(workspaceRoot, projectRoot, projectWriteEnabled));    // ← 新增
        services.AddSingleton<IAgentTool>(_ => new CreateDirTool(workspaceRoot, projectRoot, projectWriteEnabled));   // ← 新增
        services.AddSingleton<IAgentTool>(_ => new RunCommandTool(workspaceRoot, projectRoot));
        services.AddSingleton<IAgentTool>(_ => new RememberTool(_.GetRequiredService<AgentMemoryStore>()));   // ← P3：模型级记忆

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