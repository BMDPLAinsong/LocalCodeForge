// 引入 DI
// 引入 Core
using Agent.Core.Abstractions;
using Agent.Core.Models;
// 引入 Infrastructure
using Agent.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;

// 命名空间（可保留，也可删）
namespace Agent.Cli;

using Agent.Infrastructure.Metrics;
using Agent.Infrastructure.Orchestration;
using Agent.Infrastructure.Project;
using Agent.Infrastructure.Rendering;
using Agent.Infrastructure.Routing;

/// <summary>
/// Local AI Workbench 的控制台入口。
/// </summary>
internal class Program
{
    /// <summary>
    /// 主入口。配置、启动、进入交互循环。
    /// </summary>
    static async Task Main(string[] args)
    {
        // ─────────── 输入编码加固（A-补强） ───────────
        // PS 5.1 管道注入 UTF-8 BOM（EF BB BF）时，若控制台按 GBK 解码 → "锘縰" 乱码，
        // TrimStart('\uFEFF') 无效（BOM 已被错误解码成乱码字符）。
        // 显式设为 UTF-8：EF BB BF → U+FEFF → 主循环统一 trim。重定向 stdin 时可能抛 → 静默降级。
        try { Console.InputEncoding = System.Text.Encoding.UTF8; }
        catch { /* 重定向/终端不支持时保持默认，主循环 trim 仍兜底 U+FEFF 形态 */ }

        // ─────────── 配置区 ───────────
        // 读环境变量
        var dashscopeKey = Environment.GetEnvironmentVariable("DASHSCOPE_API_KEY") ?? "";

        var options = new AgentOptions
        {
            // 意图分类器
            Classifier = new ModelEndpoint
            {
                Provider = "ollama",
                Model = "qwen2.5:1.5b",
                Endpoint = "http://localhost:11434",
                ApiKey = "ollama"
            },

            // 单模型三角色：small/large/agent_pro 共用一个 law-unified（30B Custom Model），
            // 不额外拉取模型（磁盘/内存各一份常驻）；角色差异由请求级 options 运行时注入
            // （Temperature：small 0.3 确定性 / large 0.7 探索 / agent_pro 0.5 居中）。
            // 小模型角色：单文件/轻任务，确定性优先
            Small = new ModelEndpoint
            {
                Provider = "ollama",
                Model = "law-unified",
                Endpoint = "http://localhost:11434",
                ApiKey = "ollama",
                Temperature = 0.3
            },

            // 大模型角色：复杂推理/多文件/规划（温度最高，保持探索）
            // 云端 DashScope 仅预留兜底（未来可选），当前不使用云端。
            Large = new ModelEndpoint
            {
                Provider = "ollama",
                Model = "law-unified",
                Endpoint = "http://localhost:11434",
                ApiKey = "ollama",
                Temperature = 0.7
            },

            // AgentPro 角色：工程理解中介（温度居中）
            AgentPro = new ModelEndpoint
            {
                Provider = "ollama",
                Model = "law-unified",
                Endpoint = "http://localhost:11434",
                ApiKey = "ollama",
                Temperature = 0.5
            },

            EnableRouting = true,
            MaxSteps = 20,
            Temperature = 0.2,
            // 【时代隔离】law-unified 30B 落地时刻（本地）：stats 面板按此拆分 agent_pro 等服务的
            // 当前时代（30B）与历史时代（4B）成功率，如实反映模型切换后的能力。
            // 边界实证：5d829c5（三 tag 落地）2026-10-02 22:58 / c1f4069（单 tag）23:12 → 取 23:00。
            LawUnifiedEpoch = new DateTimeOffset(2026, 10, 2, 23, 0, 0, TimeSpan.FromHours(8))
        };

        // 云端兜底预留提示（当前 large 走本地 Ollama，不依赖云端 Key）
        if (string.IsNullOrEmpty(dashscopeKey))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("提示：未设置 DASHSCOPE_API_KEY（云端兜底未启用，当前 large 走本地 Ollama）。");
            Console.ResetColor();
        }

        // ─────────── 工作区目录 ───────────
        var baseDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var runsRoot = Path.Combine(baseDir, "workspace", "runs");
        // 【新增】清理旧 run 目录（保留最近 10 个；实测 51 个累积的根因是 maxCount=50 过宽）
        CleanOldRuns(runsRoot, keepDays: 7, maxCount: 10);
        Directory.CreateDirectory(runsRoot);

        // 用启动时间 + 4 位随机数生成 run 名字
        var runName = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
        var workspace = Path.Combine(runsRoot, runName);
        Directory.CreateDirectory(workspace);

        // 【双根保护】用户自定义敏感 denylist 加载（workspace 根下 sensitive_denylist.txt，幂等）
        Agent.Infrastructure.Project.SensitivePathGuard.LoadUserDenylist(workspace);

        string? lastTaskId = null; // 手动 rollback 命令作用域：最近一次任务的 taskId（P-B）

        // SQLite 数据库：所有 run 共用一个
        var dbPath = Path.Combine(baseDir, "workspace", "sessions.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        // ─────────── 工程感知 P0/P1：--project 启动参数 + --write 授权 ───────────
        // 设置工程根 → 只读工具可感知工程文件 + 任务上下文注入工程数据块；--write 开启工程可写（改前备份）
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--project", StringComparison.OrdinalIgnoreCase))
            {
                var proj = Path.GetFullPath(args[i + 1]);
                if (Directory.Exists(proj))
                {
                    options.ProjectRoot = proj;
                    Console.WriteLine($"工程根: {proj}");
                }
                else
                {
                    Console.WriteLine($"错误：工程目录不存在：{proj}");
                }
                break;
            }
        }
        if (options.ProjectRoot is not null && args.Any(a => a.Equals("--write", StringComparison.OrdinalIgnoreCase)))
        {
            options.ProjectWriteEnabled = true;
            Console.WriteLine("工程可写模式已开启（改动前自动备份，可用 rollback 恢复）。");
        }
        // 【终端增强 10-07】--verbose：交互态工具结果输出全文（默认截断 300）
        var verbose = args.Any(a => a.Equals("--verbose", StringComparison.OrdinalIgnoreCase));

        // ─────────── 构建 DI 容器 ───────────
        var services = new ServiceCollection();
        services.AddAgent(options, workspace, dbPath, options.ProjectRoot);
        // 【10-07 Dashboard】事件多播总线（跨任务单例）：渲染器直接消费不变，Dashboard SSE 是第二消费者
        services.AddSingleton<Agent.Infrastructure.Events.TaskEventHub>();
        // 【10-07 UI 层 #1】事件持久化（metrics.db 同库 task_events 表）：Dashboard 任务详情回放地基
        services.AddSingleton<Agent.Infrastructure.Events.TaskEventStore>(_ =>
            new Agent.Infrastructure.Events.TaskEventStore(Path.Combine(Path.GetDirectoryName(dbPath) ?? ".", "metrics.db")));
        var provider = services.BuildServiceProvider();
        // 【关键】启动时显式解析 IMetricsCollector，触发 SQLite 建表
        // 否则 Singleton 工厂不会被调用，metrics.db 不会创建
        _ = provider.GetRequiredService<IMetricsCollector>();
        // 【3A】显式解析 RoutingDecisionStore，触发 routing_decisions 表建表
        _ = provider.GetRequiredService<Agent.Infrastructure.Routing.RoutingDecisionStore>();
        // 【C】路由决策表生命周期清理：启动时删除 90 天前明细（防无限增长；调权仅用近 10 条）
        provider.GetRequiredService<Agent.Infrastructure.Routing.RoutingDecisionStore>().PruneOlderThan(90);
        // 【C】显式解析 ToolRejectionStore，触发 tool_rejections 表建表
        _ = provider.GetRequiredService<Agent.Infrastructure.Metrics.ToolRejectionStore>();
        // 【10-07 Dashboard】事件总线显式解析（主容器单例，任务循环旁路发布 + SSE 订阅）
        var eventHub = provider.GetRequiredService<Agent.Infrastructure.Events.TaskEventHub>();
        // 【10-07 UI 层 #1】事件持久化显式解析（触发 task_events 建表）+ 30 天生命周期清理
        var eventStore = provider.GetRequiredService<Agent.Infrastructure.Events.TaskEventStore>();
        eventStore.PruneOlderThan(30);

        // 【D2】启动时加载任务特征类历史聚合（路由数据校准 + stats 归因）
        var featureAggregator = provider.GetRequiredService<Agent.Infrastructure.Routing.TaskMetricsAggregator>();
        featureAggregator.Load();

        // 取出 Agent 主循环
        var loop = provider.GetRequiredService<IAgentLoop>();

        // 会话 ID
        var sessionId = Guid.NewGuid().ToString("N");

        // ─────────── 渲染器装配（Spectre P0） ───────────
        // 无头判定：管道输入 / 输出重定向 / 非交互环境 → PlainTextRenderer（输出与旧 CLI 逐字节兼容，E2E 依赖）；
        // 真实终端 → SpectreRenderer（Panel 横幅 + LiveDisplay 任务实时区）。
        var renderer = RendererFactory.Create(
            !Console.IsInputRedirected && !Console.IsOutputRedirected);
        if (renderer is Agent.Infrastructure.Rendering.SpectreRenderer sp0)
            sp0.Verbose = verbose;

        // ─────────── 打印欢迎 ───────────
        renderer.Banner(workspace, dbPath);

        // 【记忆治理收尾】启动自动治理：过期清理 + 存量去重（阈值走 AgentOptions.MemoryPrune* 配置字段）
        // agent_memory 此前只靠手动 memory prune/dedup——启动自动清理与 routing_decisions(90d)/task_events(30d) 对齐
        var memoryStore = provider.GetRequiredService<Agent.Infrastructure.Memory.AgentMemoryStore>();
        var prunedMem = memoryStore.PruneExpired(options.MemoryPruneDays, options.MemoryPruneMinHits);
        var dedupedMem = memoryStore.Deduplicate();
        if (prunedMem > 0 || dedupedMem > 0)
            renderer.System($"[记忆治理] 启动清理：过期 {prunedMem} 条 / 重复合并 {dedupedMem} 条。");

        // ─────────── 打印配置 ───────────
        renderer.ConfigBlock(options);

        // ─────────── 本地模型健康检查 ───────────
        await CheckLocalModelsAsync(options);

        // ─────────── 任务事件流消费（终端增强 10-07） ───────────
        // 统一消费 Orchestrator / AgentLoop 事件流：交互态维护任务状态前缀（[N步|service|秒]）、
        // 响应 q 取消（OperationCanceledException）、任务结束打印汇总行。
        // 取消任务不归档（不污染成功率指标）；无头路径（sp==null）行为与旧版逐字节一致。
        async Task<(List<string> errors, string? answer, bool cancelled)> ConsumeTaskAsync(
            string taskId,
            IAsyncEnumerable<AgentEvent> events,
            Agent.Infrastructure.Rendering.SpectreRenderer? sp,
            CancellationToken ct)
        {
            var errors = new List<string>();
            string? answer = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var step = 0;
            var seq = 0;
            var svc = "?";
            var cancelled = false;
            try
            {
                await foreach (var evt in events.WithCancellation(ct))
                {
                    if (evt is AgentEvent.Error e) errors.Add(e.Message);
                    if (evt is AgentEvent.FinalAnswer fa) answer = fa.Text;
                    if (evt is AgentEvent.Routing r) svc = r.ServiceId;
                    if (evt is AgentEvent.ToolCall) step++;
                    if (sp is not null) sp.TaskPrefix = $"[{step}步|{svc}|{sw.Elapsed.TotalSeconds:0}s]";
                    await renderer.RenderEventAsync(evt);
                    // 【10-07 Dashboard】旁路多播：渲染器直接消费不变，hub 是第二消费者
                    // （SSE 订阅者浏览器后台/JS 卡住时 DropOldest 丢最旧，绝不阻塞任务）
                    eventHub.Publish(evt);
                    // 【10-07 UI 层 #1】事件持久化（第三消费者，fail-safe）：Dashboard 任务详情回放
                    eventStore.RecordAsync(taskId, seq++, evt);
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            finally
            {
                sw.Stop();
                if (sp is not null) sp.TaskPrefix = "";
            }
            // 汇总/取消行仅交互态输出——无头路径输出与旧版逐字节一致（E2E 断言依赖）
            if (sp is not null)
            {
                if (cancelled)
                    renderer.Warn($"[已取消] 任务已停止（{step} 步 / {sw.Elapsed.TotalSeconds:0}s / {svc}）");
                else
                    renderer.Info($"[任务汇总] {step} 步 / {sw.Elapsed.TotalSeconds:0}s / {svc}");
            }
            return (errors, answer, cancelled);
        }

        // ─────────── 主循环 ───────────
        // 【10-07 Dashboard】已启动的 dashboard URL（幂等：重复 dashboard 命令只提示不重启）
        string? dashboardUrl = null;

        // 【10-07 UI 层 #4】Tab 命令补全（交互态自制行编辑；无头/重定向退回 ReadLine 逐字节兼容 E2E）
        // 命令列表与主循环分支保持一致（use/rollback/stats/memory/plan/orch/diff/dashboard/help/exit/new）
        string[] cliCommands = { "use", "rollback", "stats", "memory", "plan", "orch", "diff", "dashboard", "help", "exit", "new" };
        string? ReadLineWithTabCompletion()
        {
            // 无头路径（管道/重定向）：E2E 输出逐字节兼容，不做行编辑
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
                return Console.ReadLine();
            var buf = new System.Text.StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(true);
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        Console.WriteLine();
                        return buf.ToString();
                    case ConsoleKey.Backspace:
                        if (buf.Length > 0) { buf.Length--; Console.Write("\b \b"); }
                        break;
                    case ConsoleKey.Tab:
                        if (buf.Length == 0) break;
                        var prefix = buf.ToString();
                        var matches = cliCommands.Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (matches.Count == 1)
                        {
                            var rest = matches[0][buf.Length..];
                            buf.Append(rest).Append(' ');
                            Console.Write(rest + " ");
                        }
                        else if (matches.Count > 1)
                        {
                            // 多候选：打印候选列表后重绘提示符 + 当前输入
                            Console.WriteLine();
                            renderer.System($"[补全] {string.Join("  ", matches)}");
                            renderer.Prompt();
                            Console.Write(buf);
                        }
                        break;
                    default:
                        if (key.KeyChar >= ' ' && key.KeyChar != '\0') { buf.Append(key.KeyChar); Console.Write(key.KeyChar); }
                        break;
                }
            }
        }

        while (true)
        {
            renderer.Prompt();
            var input = ReadLineWithTabCompletion();
            // 【A-补强】统一剥离 BOM（U+FEFF）——PS 5.1 管道注入 UTF-8 BOM 到首行时
            // StartsWith("use ")/StartsWith("plan ") 等全部不命中、命令被当任务执行。
            // 原 370 行仅 use 分支 trim；此处提到主循环入口，覆盖所有命令分支。
            // 【批次1·补充】Trim() 防 "exit "（管道/尾随空白）漏判被当任务执行（实测现象）。
            input = input?.TrimStart('\uFEFF')?.Trim();

            if (input is null || input.Length == 0 || input.Equals("exit", StringComparison.OrdinalIgnoreCase))
                break;

            if (input.Equals("new", StringComparison.OrdinalIgnoreCase))
            {
                sessionId = Guid.NewGuid().ToString("N");
                renderer.System("[系统] 已开启新会话。");
                continue;
            }

            // ═══════════ help 命令（命令表速查） ═══════════
            if (input.Equals("help", StringComparison.OrdinalIgnoreCase) || input == "?")
            {
                renderer.System(
                    "命令：use [--write] <路径>（工程模式/可写） | stats [failures|recent] | " +
                    "memory [list [N]|stats|forget <topic>|prune|dedup] | rollback [--dry-run] | " +
                    "plan <需求> | orch <需求> | new（新会话） | exit（退出） | help");
                continue;
            }

            // ═══════════ 【临时测试】Planner 测试 ═══════════
            if (input.StartsWith("plan ", StringComparison.OrdinalIgnoreCase))
            {
                var planner = provider.GetRequiredService<IPlannerService>();
                var planInput = input.Substring(5).Trim();

                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"[Planner] 正在规划：{planInput}");
                Console.ResetColor();

                var plan = await planner.GeneratePlanAsync(
                    planInput, workspace, CancellationToken.None);

                if (plan is null)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[Plan 解析失败]");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[Plan 生成成功]");
                    Console.ResetColor();

                    Console.WriteLine($"  Summary:  {plan.Summary}");
                    Console.WriteLine($"  Language: {plan.Language}");
                    Console.WriteLine($"  Files:    {plan.Files.Count} 个");
                    foreach (var f in plan.Files)
                    {
                        Console.WriteLine($"    - {f.Path}  ({f.Purpose})");
                        Console.WriteLine($"      要求：{f.Requirements}");
                    }
                    Console.WriteLine($"  Run:      {plan.RunCommand}");
                    Console.WriteLine($"  Expect:   {plan.ExpectedOutput}");
                }

                Console.WriteLine();
                continue;
            }
            // ═══════════ 临时测试结束 ═══════════

            // ═══════════ 【临时测试】Orchestrator 测试 ═══════════
            if (input.StartsWith("orch ", StringComparison.OrdinalIgnoreCase))
            {
                var orchestrator = provider.GetRequiredService<IOrchestrator>();
                var orchInput = input.Substring(5).Trim();

                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine($"[Orchestrator] 启动三阶段编排：{orchInput}");
                Console.ResetColor();

                await foreach (var evt in orchestrator.RunAsync(orchInput, workspace, CancellationToken.None))
                {
                    // Spectre P0：统一走渲染器（消灭此处与 RenderEvent 重复的事件 switch——双路径漂移实例；
                    // 原此处 ToolResult 截断 500 与 RenderEvent 300 不一致，统一为 300）。
                    await renderer.RenderEventAsync(evt);
                }

                continue;
            }
            // ═══════════ Orchestrator 测试结束 ═══════════

            // ═══════════ dashboard 命令（10-07 真 UI 层·本地 Web Dashboard） ═══════════
            // 后台启动 Kestrel（127.0.0.1 随机端口，仅本机回环无认证）→ 打印 URL → 不阻塞主循环，
            // 任务照常执行、浏览器实时看。SSE 只覆盖当前任务；历史走 /api/tasks；Diff 复用引擎模型。
            if (input.Equals("dashboard", StringComparison.OrdinalIgnoreCase))
            {
                // 无头守卫：管道/重定向模式下直接提示（不启动 Kestrel）——保护 E2E 输出逐字节兼容
                if (Console.IsInputRedirected || Console.IsOutputRedirected)
                {
                    Console.WriteLine("dashboard 需交互终端（当前为管道/重定向模式）。");
                    continue;
                }
                if (dashboardUrl is not null)
                {
                    Console.WriteLine($"Dashboard 已在运行：{dashboardUrl}（浏览器打开即可查看；Ctrl+C 退出 CLI 时停止）");
                    continue;
                }
                var (url, _) = await Agent.Dashboard.DashboardHost.StartAsync(
                    new Agent.Dashboard.DashboardOptions(workspace, options.ProjectRoot,
                        Path.Combine(Path.GetDirectoryName(dbPath) ?? ".", "symbol_index.db"),
                        options.LawUnifiedEpoch,
                        provider.GetRequiredService<IMetricsCollector>(), eventHub, eventStore),
                    CancellationToken.None);
                dashboardUrl = url;
                // 【10-07 UI 层 #4】交互态 [link] 超链接样式；无头态 dashboard 命令已被无头守卫拦截（不会到此处）
                renderer.Notice($"Dashboard 已启动（仅本机回环）：[link]{url}[/]\n  浏览器打开查看：任务实时流 / stats / 历史任务 / 备份 Diff / 任务详情回放");
                continue;
            }

            // ═══════════ stats 命令 ═══════════
            if (input.StartsWith("stats", StringComparison.OrdinalIgnoreCase))
            {
                var metrics = provider.GetRequiredService<IMetricsCollector>();

                var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var subCommand = parts.Length > 1 ? parts[1].ToLowerInvariant() : "summary";

                if (subCommand == "failures" || subCommand == "fail")
                {
                    await PrintRecentFailuresAsync(metrics);
                }
                else if (subCommand == "recent")
                {
                    await PrintRecentAsync(metrics, 10);
                }
                else
                {
                    // 默认：显示汇总 + 最近 5 条
                    var decisionStore = provider.GetRequiredService<Agent.Infrastructure.Routing.RoutingDecisionStore>();
                    var rejectStore = provider.GetRequiredService<Agent.Infrastructure.Metrics.ToolRejectionStore>();

                    // 【P2】交互态：stats 概览 BarChart（无头路径不变，仍走 PrintSummaryAsync 文本）
                    if (renderer is Agent.Infrastructure.Rendering.SpectreRenderer spec)
                    {
                        var s = await metrics.GetSummaryAsync(null, CancellationToken.None);
                        if (s.TotalCount > 0)
                        {
                            spec.StatsPanel("任务指标概览",
                                new List<(string, double)>
                                {
                                    ("总任务数", s.TotalCount),
                                    ("成功", s.SuccessCount),
                                    ("失败", s.FailureCount),
                                    ("成功率 %", Math.Round(s.SuccessRate * 100, 1)),
                                    ("可疑", s.SuspiciousCount),
                                });
                        }
                    }

                    await PrintSummaryAsync(metrics, options.LawUnifiedEpoch, options, featureAggregator, decisionStore, rejectStore);
                    await PrintRecentAsync(metrics, 5);
                }

                continue;
            }

            // ═══════════ memory 命令（记忆治理：list/stats/forget/prune/dedup） ═══════════
            if (input.StartsWith("memory", StringComparison.OrdinalIgnoreCase))
            {
                var mem = provider.GetRequiredService<Agent.Infrastructure.Memory.AgentMemoryStore>();
                var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "stats";
                var specRenderer = renderer as Agent.Infrastructure.Rendering.SpectreRenderer;

                switch (sub)
                {
                    case "list":
                    {
                        var n2 = parts.Length > 2 && int.TryParse(parts[2], out var take2) ? take2 : 20;
                        var rows = mem.List(n2);
                        // 【P2】交互态：Table；无头保持旧文本
                        if (specRenderer is not null)
                        {
                            specRenderer.MemoryTable($"记忆列表（{rows.Count} 条）",
                                rows.Select(m => (m.Id.ToString(), m.Kind, m.HitCount.ToString(), m.Count.ToString(), m.Topic)).ToList());
                            break;
                        }
                        foreach (var m in rows)
                            Console.WriteLine($"  [{m.Id}] {m.Kind} | hits={m.HitCount} count={m.Count} | {m.Topic} | {m.CreatedAt}");
                        break;
                    }
                    case "forget":
                    {
                        if (parts.Length < 3 || !long.TryParse(parts[2], out var fid))
                        {
                            Console.WriteLine("用法：memory forget <id>（id 见 memory list）");
                            break;
                        }
                        Console.WriteLine(mem.Forget(fid) ? $"已删除记忆 {fid}。" : $"未找到记忆 {fid}。");
                        break;
                    }
                    case "prune":
                    {
                        var days = parts.Length > 2 && int.TryParse(parts[2], out var d) ? d : 90;
                        var minHits = parts.Length > 3 && int.TryParse(parts[3], out var mh) ? mh : 2;
                        Console.WriteLine($"过期清理（{days} 天内未用且命中 < {minHits}）：已删除 {mem.PruneExpired(days, minHits)} 条。");
                        break;
                    }
                    case "dedup":
                        Console.WriteLine($"重复合并：已清理 {mem.Deduplicate()} 条冗余。");
                        break;
                    default:
                    {
                        var st = mem.GetStats();
                        var rows = mem.List(10);
                        // 【P2】交互态：统计 Table；无头保持旧文本
                        if (specRenderer is not null)
                        {
                            Console.WriteLine($"记忆统计：共 {st.Total} 条（model {st.ModelCount} / task {st.TaskCount}），总命中 {st.TotalHits} 次");
                            specRenderer.MemoryTable("最近记忆",
                                rows.Select(m => (m.Id.ToString(), m.Kind, m.HitCount.ToString(), m.Count.ToString(), m.Topic)).ToList());
                            break;
                        }
                        Console.WriteLine($"记忆统计：共 {st.Total} 条（model {st.ModelCount} / task {st.TaskCount}），总命中 {st.TotalHits} 次");
                        foreach (var m in rows)
                            Console.WriteLine($"  [{m.Id}] {m.Kind} | hits={m.HitCount} count={m.Count} | {m.Topic}");
                        break;
                    }
                }
                continue;
            }

            // ═══════════ use 命令（工程感知 P0/P1） ═══════════
            // E2E 基建：PS 5.1 -File 模式向子进程 stdin 注入 UTF-8 BOM（EF BB BF）到首行
            // → 剥离 BOM（U+FEFF），否则 StartsWith("use ") 不命中、use 行被当任务执行
            var trimmedInput = input.TrimStart('\uFEFF');
            if (trimmedInput.StartsWith("use ", StringComparison.OrdinalIgnoreCase))
            {
                var rest = trimmedInput.Substring(4).Trim();
                var writeFlag = rest.StartsWith("--write ", StringComparison.OrdinalIgnoreCase);
                if (writeFlag) rest = rest.Substring(8).Trim();
                ApplyUse(provider, options, rest, writeFlag, autoSource: null);
                continue;
            }

            // ═══════════ 双根保护·输入路径自动识别（给路径就干活） ═══════════
            // 输入含"存在的绝对目录路径 + 读/分析语境（非写任务）" → 自动只读 use。
            // 敏感区路径源头不 use（SensitivePathGuard.IsDenied，内容不进 chatHistory）；
            // 显式 use 优先（已设工程根不覆盖）；写任务不触发（路径是目标文件而非读目标）。
            if (options.ProjectRoot is null &&
                Agent.Infrastructure.Project.ProjectPathDetector.TryGetAutoUsePath(trimmedInput, out var autoProjectPath))
            {
                ApplyUse(provider, options, autoProjectPath, writeFlag: false, autoSource: trimmedInput);
            }

            // ═══════════ diff 命令（10-07，Diff 引擎终端通道） ═══════════
            // diff [taskId]：显示该任务在工程内的备份 vs 当前文件的行级差异（unified hunk 红绿渲染）。
            // 无 taskId → 最近任务；非工程模式 → 提示。Web Dashboard 的 /api/diff 复用同一引擎（无 DTO）。
            if (input.Equals("diff", StringComparison.OrdinalIgnoreCase)
                || input.StartsWith("diff ", StringComparison.OrdinalIgnoreCase))
            {
                if (options.ProjectRoot is null)
                {
                    Console.WriteLine("diff 需要工程模式（use <路径> 后可用，显示工程改动备份差异）。");
                    continue;
                }
                var diffTaskId = input.Length > 5 ? input[5..].Trim() : lastTaskId;
                if (string.IsNullOrEmpty(diffTaskId))
                {
                    Console.WriteLine("暂无最近任务，无法 diff。");
                    continue;
                }
                var diffBackups = BackupService.ListBackups(workspace)
                    .Where(b => string.Equals(b.TaskId, diffTaskId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (diffBackups.Count == 0)
                {
                    Console.WriteLine($"任务 {diffTaskId} 没有备份（未改工程文件，或备份已按 8 任务上限清理）。");
                    continue;
                }
                foreach (var b in diffBackups)
                {
                    var currentPath = Path.Combine(options.ProjectRoot, b.Rel);
                    var result = Agent.Infrastructure.Diff.FileDiffService.Compute(b.Full, currentPath);
                    Console.WriteLine($"[diff] {b.Rel}（task {b.TaskId} @{b.Stamp}）");
                    if (renderer is Agent.Infrastructure.Rendering.SpectreRenderer spr)
                        spr.DiffBlock(result);
                    else
                        Console.WriteLine(Agent.Infrastructure.Diff.PlainDiffFormatter.Format(result));
                }
                continue;
            }

            // ═══════════ rollback 命令（工程感知 P1） ═══════════
            if (input.StartsWith("rollback", StringComparison.OrdinalIgnoreCase))
            {
                if (options.ProjectRoot is null)
                {
                    Console.WriteLine("当前未设置工程根（先 use <路径>）。");
                    continue;
                }
                var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    Console.WriteLine("可用备份（taskId / 时间戳 / 文件）：");
                    var backups = BackupService.ListBackups(workspace);
                    if (backups.Count == 0)
                        Console.WriteLine("  (无备份)");
                    foreach (var b in backups.Take(20))
                        Console.WriteLine($"  [{b.TaskId}/{b.Stamp}] {b.Rel}");

                    // 【P2】交互态：备份 Table + 多选恢复（空格多选，回车执行；无头保持旧文本）
                    if (renderer is Agent.Infrastructure.Rendering.SpectreRenderer spec && backups.Count > 0)
                    {
                        spec.BackupTable($"可用备份（{backups.Count} 条）",
                            backups.Take(20).Select(b => (b.TaskId, b.Stamp, b.Rel)).ToList());
                        if (!string.IsNullOrEmpty(lastTaskId))
                        {
                            var relChoices = backups.Select(b => b.Rel).Distinct().ToList();
                            var selected = spec.RollbackPrompt(
                                "选择要恢复的文件（[bold]空格[/]多选，[bold]回车[/]执行；未选则取消）", relChoices);
                            var restored = 0;
                            foreach (var relFile in selected)
                            {
                                var rollbackDest = BackupService.Rollback(workspace, relFile, options.ProjectRoot, lastTaskId);
                                if (rollbackDest is not null) { Console.WriteLine($"已回滚：{rollbackDest}"); restored++; }
                            }
                            if (restored == 0)
                                Console.WriteLine("未执行回滚。");
                        }
                    }

                    Console.WriteLine("用法：rollback all 恢复最近任务全部备份；rollback --dry-run 预览（不执行）；rollback <相对路径> 恢复最近任务该文件最近备份。");
                    continue;
                }
                if (parts[1].Equals("all", StringComparison.OrdinalIgnoreCase) ||
                    parts[1].Equals("--dry-run", StringComparison.OrdinalIgnoreCase))
                {
                    var dryRun = parts[1].Equals("--dry-run", StringComparison.OrdinalIgnoreCase);
                    if (string.IsNullOrEmpty(lastTaskId))
                    {
                        Console.WriteLine("本会话尚无任务（无备份作用域）。");
                        continue;
                    }
                    if (dryRun)
                    {
                        var (restoreTargets, removeTargets) = BackupService.DryRunRollbackTask(workspace, options.ProjectRoot, lastTaskId);
                        Console.WriteLine($"[dry-run] 将恢复 {restoreTargets.Count} 个文件（最早备份=任务前状态）：");
                        foreach (var r in restoreTargets) Console.WriteLine($"  恢复 {r}");
                        Console.WriteLine($"[dry-run] 将删除 {removeTargets.Count} 个新建文件：");
                        foreach (var r in removeTargets) Console.WriteLine($"  删除 {r}");
                        Console.WriteLine("（未执行任何改动；确认后 rollback all 生效）");
                        continue;
                    }
                    var n = BackupService.RollbackTask(workspace, options.ProjectRoot, lastTaskId);
                    Console.WriteLine($"已回滚全部：{n} 个文件恢复到最早备份（任务前状态）。");
                    continue;
                }
                var rel = string.Join(' ', parts.Skip(1)).Trim();
                if (string.IsNullOrEmpty(lastTaskId))
                {
                    Console.WriteLine("本会话尚无任务（无备份作用域）。");
                    continue;
                }
                var dest = BackupService.Rollback(workspace, rel, options.ProjectRoot, lastTaskId);
                Console.WriteLine(dest is null
                    ? $"没有找到 {rel} 的备份（最近任务 {lastTaskId}）。"
                    : $"已回滚：{dest}");
                continue;
            }

            // ═══════════ 【自动分发】 ═══════════
            // 【只读工程组合边界】工程模式 + 未授权写（use 不带 --write）+ 写任务引用工程内文件
            // → 必然失败。在 Orchestrator/AgentLoop 启动前阻断（不建 DI 容器、不跑任务），
            // 避免空跑 10-15 分钟后才被写白名单拒绝。
            if (ReadOnlyGuard.ShouldBlock(input, options.ProjectRoot, options.ProjectWriteEnabled, out var hitFile))
            {
                renderer.Warn($"[只读工程] 任务引用了工程内文件 {hitFile}，但当前为只读模式（use 未带 --write），无法修改。");
                renderer.Info($"  如需修改工程：请先执行  use --write {options.ProjectRoot}");
                renderer.Info($"  如需在任务目录创建：请重述需求（不提及工程内文件）——注意工程上下文仍可能引导模型触碰工程文件，存在失败可能。");
                continue;
            }

            // 【方案 A·run 级工作目录】一次 CLI 运行 = 一个工作空间：
            // 工具根直接用 run 根（workspace），任务间共享产物——
            // "接着更改上一个脚本"可 read_file 到前一任务写入的文件。
            // 任务目录 task_<id> 不再创建（避免空壳目录误导 list_dir），
            // taskId 仅作为记忆归档溯源（source=task_<id>）。
            var taskId = Guid.NewGuid().ToString("N")[..8];
            lastTaskId = taskId; // 手动 rollback 命令作用域（恢复最近任务备份）

            renderer.Info($"[工作目录] {Path.GetFileName(workspace)}（任务归属 task_{taskId}）");

            // 【终端增强 10-07】q 停当前任务（仅交互态）：
            // 任务期间 ReadLine 不可用（主循环 await 事件流），后台轮询 KeyAvailable；
            // 按 q → 取消任务令牌（Orchestrator 在阶段间 ThrowIfCancellationRequested 响应）。
            // 监听随任务收尾（finally）停止；无头路径（sp==null）不启用。
            var sp = renderer as Agent.Infrastructure.Rendering.SpectreRenderer;
            var taskCts = new CancellationTokenSource();
            var listenCts = new CancellationTokenSource();
            if (sp is not null)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        while (!listenCts.IsCancellationRequested)
                        {
                            if (Console.KeyAvailable)
                            {
                                var k = Console.ReadKey(true);
                                if (k.Key == ConsoleKey.Q)
                                {
                                    sp.Notice("[取消请求] 正在停止当前任务（q）...");
                                    taskCts.Cancel();
                                }
                            }
                            Thread.Sleep(50);
                        }
                    }
                    catch { /* 无控制台/输入重定向时忽略 */ }
                });
                renderer.Info($"[任务] 开始 {DateTime.Now:HH:mm:ss}（按 q 可停止当前任务）");
            }
            try
            {
                // 【批次1·E】显式写目标授权：解析用户写目标（"X盘根目录 test.txt" → X:\test.txt），
                // 敏感区源头拒绝（授权入口即拦，敏感内容物理不进白名单/持久层）；本次任务临时白名单。
                string? writeTarget = null;
                var writeTargetIsFile = false;
                var explicitWriteRoots = new List<string>();
                if (Agent.Infrastructure.Project.ProjectPathDetector.TryGetWriteTarget(input, out var wt, out var wf)
                    && !Agent.Infrastructure.Project.SensitivePathGuard.IsDenied(wt))
                {
                    writeTarget = wt;
                    writeTargetIsFile = wf;
                    explicitWriteRoots.Add(wt);
                    renderer.Info($"[显式授权] 检测到写目标 {wt}，本次任务临时白名单。");
                }

                // 【关键修复】为每个任务创建独立 DI 容器（工具绑定 run 根）——P-B 工具层注入 taskId 隔离备份
                var taskServices = new ServiceCollection();
                taskServices.AddAgent(options, workspace, dbPath, options.ProjectRoot, options.ProjectWriteEnabled, taskId, explicitWriteRoots);
                var taskProvider = taskServices.BuildServiceProvider();

                if (TaskClassifier.ShouldOrchestrate(input))
                {
                    // 走三阶段编排
                    renderer.Notice($"[自动编排] 任务复杂，启用 Planner-Executor");

                    var orchestrator = taskProvider.GetRequiredService<IOrchestrator>();
                    var (orchErrors, orchAnswer, cancelled) = await ConsumeTaskAsync(
                        taskId, orchestrator.RunAsync(input, workspace, taskCts.Token), sp, taskCts.Token);
                    if (!cancelled)
                        ArchiveTask(taskProvider, input, taskId, "orchestrator", orchErrors, orchAnswer);
                    if (options.ProjectRoot is not null)
                    {
                        // 【P0-1】失败/取消 → 自动回滚工程改动（P-B：仅回滚本任务）；成功 → 清理本任务 .new 标记
                        if (cancelled || orchAnswer is null)
                            AutoRollbackOnFailure(workspace, options.ProjectRoot, taskId, renderer);
                        else
                            BackupService.ClearNewMarkers(workspace, taskId);
                    }
                }
                else
                {
                    // 走原有 AgentLoop
                    var taskLoop = taskProvider.GetRequiredService<IAgentLoop>();
                    var request = new AgentRequest(sessionId, input, workspace, writeTarget, writeTargetIsFile);

                    var (loopErrors, loopAnswer, cancelled) = await ConsumeTaskAsync(
                        taskId, taskLoop.RunAsync(request, taskCts.Token), sp, taskCts.Token);
                    if (!cancelled)
                        ArchiveTask(taskProvider, input, taskId, "agent", loopErrors, loopAnswer);
                    if (options.ProjectRoot is not null)
                    {
                        if (cancelled || loopAnswer is null)
                            AutoRollbackOnFailure(workspace, options.ProjectRoot, taskId, renderer);
                        else
                            BackupService.ClearNewMarkers(workspace, taskId);
                    }
                }
                // 【任务4 备份生命周期】每任务结束后保留最近 8 个任务备份，清理更早的（防 .backup 增长）
                if (options.ProjectRoot is not null)
                    BackupService.PruneBackups(workspace, 8);
            }
            finally
            {
                listenCts.Cancel();
            }
        }
    }

    /// <summary>
    /// use 命令与路径自动识别共用：设置工程根 + 工程扫描/缓存 + 可写提示 + 双根重叠自检。
    /// autoSource 非空 = 自动识别触发（打印 [自动识别] 行，只读）。
    /// </summary>
    static void ApplyUse(IServiceProvider provider, AgentOptions options,
        string rest, bool writeFlag, string? autoSource)
    {
        var proj = Path.GetFullPath(rest);
        if (!Directory.Exists(proj))
        {
            Console.WriteLine($"错误：目录不存在：{proj}");
            return;
        }
        if (autoSource is not null)
            Console.WriteLine($"[自动识别] 检测到工程路径 {proj}（只读），已设置工程根。");
        options.ProjectRoot = proj;
        options.ProjectWriteEnabled = writeFlag;
        var scan = ProjectScanner.Scan(proj);
        // 工程感知 P2：工程上下文缓存——工程未变化时命中缓存（跨 CLI 重启持久化）
        var cached = provider.GetRequiredService<ProjectContextStore>()
            .TryGet(proj, scan.FileCount, scan.DirCount, scan.TotalBytes, scan.MaxMtimeTicks);
        if (cached is not null)
        {
            Console.WriteLine(cached.Overview);
            Console.WriteLine("（工程上下文命中缓存，未重新扫描）");
        }
        else
        {
            provider.GetRequiredService<ProjectContextStore>().Put(proj, scan);
            Console.WriteLine(scan.Overview);
        }
        if (writeFlag)
            Console.WriteLine("工程可写模式已开启（改动前自动备份，可用 rollback 恢复）。");

        // 【双根保护】启动自检：工程根是否与敏感区重叠（用户手滑把工程加进 denylist → 读写双拒）
        var overlaps = Agent.Infrastructure.Project.SensitivePathGuard.FindWriteRootOverlaps(
            new[] { options.ProjectRoot! });
        if (overlaps.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"警告：工程根 {overlaps[0]} 命中敏感区 denylist——读写将全部被拒。" +
                              "如属误加，请从 workspace\\sensitive_denylist.txt 移除该路径。");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// 任务归档记忆（工程感知 P3 补足）：
    /// 任务完成/失败后确定性提炼写入 AgentMemoryStore（kind=task），
    /// 供后续任务检索——补足模型不自发 remember 时的记忆积累。
    /// </summary>
    static void ArchiveTask(ServiceProvider taskProvider, string input, string taskId,
        string mode, List<string> errors, string? finalAnswer)
    {
        try
        {
            var success = finalAnswer is not null;
            var (topic, content) = Agent.Infrastructure.Memory.TaskMemoryExtractor.Extract(
                input, success, errors, mode, finalAnswer);
            var store = taskProvider.GetRequiredService<Agent.Infrastructure.Memory.AgentMemoryStore>();
            // 【任务5】任务归档记忆打特征类标签（跨类注入时被过滤）
            store.Add("task", topic, content, $"task_{taskId}",
                Agent.Infrastructure.Routing.TaskFeatureClassifier.Classify(input).ToString());
        }
        catch
        {
            // 归档失败不阻塞任务
        }
    }

    /// <summary>
    /// 【P0-1】工程可写模式任务失败后的自动回滚：
    /// 恢复备份的已存在文件 + 删除本任务新建的工程文件（P-B：仅回滚本 taskId 作用域）。
    /// 纯确定性文件操作，无模型调用；失败不抛（回滚尽力而为）。
    /// </summary>
    static void AutoRollbackOnFailure(string workspace, string projectRoot, string taskId, IRenderer renderer)
    {
        try
        {
            var (restored, removed) = BackupService.RollbackTaskFull(workspace, projectRoot, taskId);
            if (restored > 0 || removed > 0)
            {
                renderer.Warn($"[回滚] 任务失败，工程改动已还原：{restored} 个文件恢复备份、{removed} 个新建文件已清理。");
            }
        }
        catch
        {
            // 回滚失败不阻塞（可手动 rollback）
        }
    }

    // ─────────── 辅助方法 ───────────

    // 【Spectre P0】Truncate / FormatToolArgs / RenderEvent 已下沉
    // src/Agent.Infrastructure/Rendering/RendererBase.cs（单一来源，tests 直接断言）——
    // CLI 不再持有独立实现，消灭事件渲染双路径漂移。

    /// <summary>
    /// 检查所有本地 Ollama 模型的可用性。
    /// 对 Provider == "ollama" 的端点逐个检查：
    ///   1. Ollama 服务是否在线
    ///   2. 模型是否已拉取
    /// </summary>
    static async Task CheckLocalModelsAsync(AgentOptions options)
    {
        Console.WriteLine("=== 本地模型健康检查 ===");

        // 判断目录是否含已安装模型（manifests 非空）
        static bool HasModels(string dir)
            => Directory.Exists(Path.Combine(dir, "manifests")) &&
               Directory.EnumerateFileSystemEntries(Path.Combine(dir, "manifests")).Any();

        // 收集所有本地端点（Provider == "ollama"）
        var localEndpoints = new List<(string Name, ModelEndpoint Endpoint)>
        {
            ("Classifier", options.Classifier),
            ("Small",      options.Small),
            ("AgentPro",   options.AgentPro),
        }
        .Where(x => x.Endpoint.Provider?.Equals("ollama", StringComparison.OrdinalIgnoreCase) == true)
        .ToList();

        // 没有本地端点就直接返回
        if (localEndpoints.Count == 0)
        {
            Console.WriteLine("  无本地模型，跳过检查。");
            Console.WriteLine();
            return;
        }

        // ── 检查 1：Ollama 服务是否在线（连接失败自动启动 + 轮询重试） ──
        var ollamaEndpoint = localEndpoints[0].Endpoint.Endpoint;

        // 探活：成功返回模型集 + 状态码；HTTP 非成功（服务在跑但异常）返回 (null, code)；连接失败抛异常
        async Task<(HashSet<string>? Models, int StatusCode)> ProbeAsync()
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var resp = await http.GetAsync($"{ollamaEndpoint.TrimEnd('/')}/api/tags");
            var statusCode = (int)resp.StatusCode;

            if (!resp.IsSuccessStatusCode)
            {
                return (null, statusCode);
            }

            // 解析返回的 JSON，提取模型名列表
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return (
                doc.RootElement
                    .GetProperty("models")
                    .EnumerateArray()
                    .Select(m => m.GetProperty("name").GetString() ?? "")
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                statusCode);
        }

        HashSet<string>? installedModels = null;
        var serviceError = false;
        var serviceErrorCode = 0;

        try
        {
            var (models, code) = await ProbeAsync();
            installedModels = models;
            serviceError = models == null;
            serviceErrorCode = code;
        }
        catch
        {
            // 无法连接（端口拒绝 / 超时）→ 走自动启动分支
        }

        // 仅"连接失败"触发自动启动；HTTP 非成功（服务在跑但异常）不触发
        if (!serviceError && installedModels == null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  Ollama 未启动，正在尝试自动启动：ollama serve ...");
            Console.ResetColor();

            try
            {
                // 后台启动长驻进程：不阻塞 CLI、不随 CLI 退出被杀
                var psi = new ProcessStartInfo
                {
                    FileName = "ollama",
                    Arguments = "serve",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                // 注入模型库根：进程环境已有 OLLAMA_MODELS 则继承（不覆盖）；
                // 默认库根（%USERPROFILE%\.ollama\models）为空时，回退到已知自定义库
                // X:\ollama\models（实测含 10 个模型，覆盖配置所需全部本地模型）。
                // 否则 serve 会读默认空目录，健康检查模型全 FAIL。
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OLLAMA_MODELS")))
                {
                    var defaultModelsDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".ollama", "models");
                    const string knownAltModelsDir = @"X:\ollama\models";

                    var modelsRoot = HasModels(defaultModelsDir) ? null
                        : HasModels(knownAltModelsDir) ? knownAltModelsDir : null;

                    if (modelsRoot != null)
                    {
                        psi.Environment["OLLAMA_MODELS"] = modelsRoot;
                    }
                }

                Process.Start(psi);
            }
            catch (Exception startEx)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  [FAIL] 无法自动启动 Ollama：{startEx.Message}");
                Console.WriteLine($"    请确认 Ollama 已安装并手动执行：ollama serve");
                Console.ResetColor();
                Console.WriteLine();
                return;
            }

            // 轮询等待服务就绪（每 1 秒一次，最多 15 秒；不用固定 sleep，启动耗时因机器而异）
            for (var i = 0; i < 15 && installedModels == null; i++)
            {
                await Task.Delay(1000);

                try
                {
                    var (m, _) = await ProbeAsync();
                    installedModels = m;
                }
                catch
                {
                    // 尚未就绪，继续轮询
                }
            }
        }

        if (installedModels != null)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  [OK] Ollama 在线（{installedModels.Count} 个模型已安装）");
            Console.ResetColor();
        }
        else if (serviceError)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  [FAIL] Ollama 服务异常（HTTP {serviceErrorCode}）");
            Console.ResetColor();
            Console.WriteLine();
            return;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  [FAIL] 无法连接 Ollama：自动启动后 15 秒内服务未就绪（localhost:11434）");
            Console.WriteLine("    请手动执行：ollama serve");
            Console.ResetColor();
            Console.WriteLine();
            return;
        }

        // ── 检查 2：逐个检查模型是否已拉取 ──
        var missing = new List<string>();

        foreach (var (name, endpoint) in localEndpoints)
        {
            // Ollama 的模型名可能带 ":latest" 或 ":tag"，兼容处理
            var modelName = endpoint.Model;
            var found = installedModels.Contains(modelName)
                || installedModels.Contains($"{modelName}:latest");

            if (found)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  [OK] {name,-10} {modelName}");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  [FAIL]  {name,-10} {modelName}  (未安装)");
                Console.ResetColor();
                missing.Add(modelName);
            }
        }

        // ── 汇总缺失模型，给出修复命令 ──
        if (missing.Count > 0)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  以下模型缺失，请执行：");
            foreach (var m in missing)
            {
                Console.WriteLine($"    ollama pull {m}");
            }
            Console.ResetColor();
        }

        Console.WriteLine();
    }

    /// <summary>
    /// 清理旧的 run 目录。
    /// 规则：
    ///   1. 删除 7 天前的
    ///   2. 剩余中超过 maxCount 个的，删最旧的
    /// </summary>
    static void CleanOldRuns(string runsRoot, int keepDays, int maxCount)
    {
        if (!Directory.Exists(runsRoot)) return;

        try
        {
            var cutoff = DateTime.Now.AddDays(-keepDays);
            var allDirs = new DirectoryInfo(runsRoot)
                .GetDirectories()
                .OrderByDescending(d => d.CreationTime)
                .ToList();

            var deletedCount = 0;

            // 规则 1：删超期的
            foreach (var dir in allDirs.Where(d => d.CreationTime < cutoff).ToList())
            {
                try
                {
                    dir.Delete(recursive: true);
                    deletedCount++;
                }
                catch
                {
                    // 忽略：可能被占用或无权限
                }
            }

            // 规则 2：删超数量的
            var remaining = new DirectoryInfo(runsRoot)
                .GetDirectories()
                .OrderByDescending(d => d.CreationTime)
                .Skip(maxCount)
                .ToList();

            foreach (var dir in remaining)
            {
                try
                {
                    dir.Delete(recursive: true);
                    deletedCount++;
                }
                catch
                {
                    // 忽略
                }
            }

            if (deletedCount > 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"[清理] 删除 {deletedCount} 个旧 run 目录");
                Console.ResetColor();
            }
        }
        catch
        {
            // 清理失败不影响主流程
        }
    }

    /// <summary>打印指标汇总。</summary>
    static async Task PrintSummaryAsync(IMetricsCollector metrics, DateTimeOffset? lawUnifiedEpoch, Agent.Core.Models.AgentOptions? options = null, Agent.Infrastructure.Routing.TaskMetricsAggregator? featureAggregator = null, Agent.Infrastructure.Routing.RoutingDecisionStore? decisionStore = null, Agent.Infrastructure.Metrics.ToolRejectionStore? rejectStore = null)
    {
        var summary = await metrics.GetSummaryAsync(null, CancellationToken.None);

        Console.WriteLine();
        Console.WriteLine("=== 任务指标汇总 ===");

        if (summary.TotalCount == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  暂无数据。跑几个任务后再看。");
            Console.ResetColor();
            Console.WriteLine();
            return;
        }

        // 成功率
        var rateColor = summary.SuccessRate >= 0.8 ? ConsoleColor.Green
                      : summary.SuccessRate >= 0.5 ? ConsoleColor.Yellow
                      : ConsoleColor.Red;

        Console.Write("  总任务数:  ");
        Console.WriteLine(summary.TotalCount);

        Console.Write("  成功 / 失败: ");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write(summary.SuccessCount);
        Console.ResetColor();
        Console.Write(" / ");
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write(summary.FailureCount);
        Console.ResetColor();
        Console.WriteLine();

        // 【新增】可疑数量 + 明细（让"表面成功但未真正实现需求"可审计追踪）
        if (summary.SuspiciousCount > 0)
        {
            Console.Write("  可疑成功:  ");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write(summary.SuspiciousCount);
            Console.ResetColor();
            Console.WriteLine("  （触发过模板检测，可能未真正实现需求）");

            // 明细：最近 5 条可疑成功（时间/模式/输入截断）
            try
            {
                var recent = await metrics.GetRecentAsync(50, CancellationToken.None);
                var suspicious = recent.Where(m => m.IsSuspicious && m.Success).OrderByDescending(m => m.StartedAt).Take(5).ToList();
                if (suspicious.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    foreach (var m in suspicious)
                    {
                        var input = m.UserInput.Length > 40 ? m.UserInput[..40] + "..." : m.UserInput;
                        Console.WriteLine($"    · {m.StartedAt:MM-dd HH:mm} {m.Mode}  {input}");
                    }
                    Console.ResetColor();
                    Console.WriteLine("  （可疑成功明细 · 可 rollback 复查或重跑验证）");
                }
            }
            catch
            {
                // 明细读取失败不影响主面板
            }
        }

        Console.Write("  成功率:    ");
        Console.ForegroundColor = rateColor;
        Console.WriteLine($"{summary.SuccessRate:P1}");
        Console.ResetColor();

        Console.WriteLine($"  平均修复:  {summary.AvgRetryCount:F2} 次");
        Console.WriteLine($"  平均总耗时: {summary.AvgTotalMs:F0} ms");

        // 【3】失败类别分布：失败任务按 failure_category 自动归类（词表打标，无模型）
        try
        {
            var fb = await metrics.GetFailureBreakdownAsync(CancellationToken.None);
            if (fb.Count > 0)
            {
                var line = string.Join("  ", fb.Select(x => $"{x.Category}:{x.Count}"));
                Console.WriteLine($"  失败类别:  {line}");
                Console.WriteLine("  （failure_category 自动归类 · 3B 按类别调权可用）");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [失败类别] 读取失败: {ex.Message}");
        }

        // 【时代隔离】agent_pro：当前时代（30B law-unified）vs 历史时代（4B），如实反映模型能力
        if (lawUnifiedEpoch is not null)
        {
            var (cur, legacy) = await metrics.GetEpochBreakdownAsync("agent_pro", lawUnifiedEpoch.Value, CancellationToken.None);
            if (cur.TotalCount > 0 || legacy.TotalCount > 0)
            {
                var curRate = cur.TotalCount > 0 ? (double)cur.SuccessCount / cur.TotalCount : 0;
                var legacyRate = legacy.TotalCount > 0 ? (double)legacy.SuccessCount / legacy.TotalCount : 0;
                Console.Write("  agent_pro 时代隔离: ");
                Console.ForegroundColor = cur.TotalCount > 0 && curRate >= 0.6 ? ConsoleColor.Green : ConsoleColor.Yellow;
                Console.Write($"30B 时代 {cur.TotalCount} 条 / {curRate:P0}");
                Console.ResetColor();
                Console.Write($"   |   4B 历史 {legacy.TotalCount} 条 / {legacyRate:P0}");
                Console.WriteLine();
            }
        }

        if (summary.AvgPlannerMs > 0 || summary.AvgExecutorMs > 0 || summary.AvgValidatorMs > 0)
        {
            Console.WriteLine($"    - Planner:   {summary.AvgPlannerMs:F0} ms");
            Console.WriteLine($"    - Executor:  {summary.AvgExecutorMs:F0} ms");
            Console.WriteLine($"    - Validator: {summary.AvgValidatorMs:F0} ms");
        }

        // 【批次3·A 展示标注】Executor 子指标 = 生成成功率（≠ 任务成功率）。
        // orchestrator_sub 的 Executor 记录衡量"模型生成能力"；顶层失败已回填（C），
        // 该数字反映验证门后的真实达成，与顶层"任务成功率"区分展示，消除 100% 误读。
        var execStats = await metrics.GetServiceStatsAsync("small", "OrchestratorExecutor", 50, CancellationToken.None);
        if (execStats is not null && execStats.TotalCount > 0)
        {
            Console.Write($"  Executor 生成成功率: ");
            var execRate = (double)execStats.SuccessCount / execStats.TotalCount;
            var erColor = execRate >= 0.8 ? ConsoleColor.Green
                       : execRate >= 0.5 ? ConsoleColor.Yellow : ConsoleColor.Red;
            Console.ForegroundColor = erColor;
            Console.Write($"{execRate:P0}");
            Console.ResetColor();
            Console.WriteLine($"  ({execStats.SuccessCount}/{execStats.TotalCount})  ← orchestrator_sub 子指标（父失败已回填），≠ 任务成功率");
        }

        // 【D2】特征类归因：按任务特征类展示历史成功率 + 失败主因（D1 报表系统化）
        if (featureAggregator is not null)
        {
            var features = new[]
            {
                Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.ProjectIncremental,
                Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.ExternalCredential,
                Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.MultiFile,
                Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.SingleFile,
                Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.ReadQuery,
                Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.Other,
            };
            var hasData = false;
            foreach (var f in features)
            {
                var s = featureAggregator.Get(f);
                if (s is null || s.TotalCount == 0) continue;
                hasData = true;
                var featureRate = (double)s.SuccessCount / s.TotalCount;
                var featureRateColor = featureRate >= 0.8 ? ConsoleColor.Green : (featureRate >= 0.5 ? ConsoleColor.Yellow : ConsoleColor.Red);
                Console.Write($"  {Agent.Infrastructure.Routing.TaskFeatureClassifier.FeatureName(f),6}: ");
                Console.ForegroundColor = featureRateColor;
                Console.Write($"{featureRate:P0}");
                Console.ResetColor();
                Console.WriteLine($"  ({s.SuccessCount}/{s.TotalCount})  avg {s.AvgTotalMs / 1000:N0}s  失败主因: {s.TopFailureCause}");
            }
            if (hasData) Console.WriteLine("  （特征类归因 · D2 路由数据校准）");
        }

        // 【路由调权收尾】调权状态可观测：三处调权当前判定（样本/成功率/阈值/触发与否）
        if (options is not null)
        {
            try
            {
                // 与 ModelRouter 同一时代过滤语义：epoch 已配置 → 只统计 30B 时代样本
                async Task<Agent.Infrastructure.Routing.ServiceStats> TuningAsync(string svc, string tier) =>
                    lawUnifiedEpoch is { } e
                        ? await metrics.GetServiceStatsSinceAsync(svc, tier, e, 10, CancellationToken.None)
                        : await metrics.GetServiceStatsAsync(svc, tier, 10, CancellationToken.None);

                var smallRule = await TuningAsync(Agent.Infrastructure.Routing.RoutingDecision.Small, "Rule");
                var largeRule = await TuningAsync(Agent.Infrastructure.Routing.RoutingDecision.Large, "Rule");
                var proRule = await TuningAsync(Agent.Infrastructure.Routing.RoutingDecision.AgentPro, "Rule");
                var proL3 = await TuningAsync(Agent.Infrastructure.Routing.RoutingDecision.AgentPro, "SmallModel");

                var downgrade = Agent.Infrastructure.Routing.RoutingIntelligence.TrySuggestDowngrade(
                    smallRule, largeRule, options.RoutingTuningMinSamples, options.RoutingDowngradeMinSuccessRate, out var dowReason);
                var readUp = Agent.Infrastructure.Routing.RoutingIntelligence.TrySuggestReadTier(
                    proRule, smallRule, options.RoutingTuningMinSamples, options.RoutingReadUpgradeBadRate, options.RoutingReadUpgradeMinSuccessRate, out var readReason);
                var dropPro = Agent.Infrastructure.Routing.RoutingIntelligence.TrySuggestDropAgentPro(
                    proL3, options.RoutingDropAgentProMinSamples, options.RoutingDropAgentProMinSuccessRate, out var dropReason);

                Console.WriteLine("=== 路由调权状态 ===");
                static string RateOf(Agent.Infrastructure.Routing.ServiceStats s) =>
                    s.TotalCount > 0 ? $"{(double)s.SuccessCount / s.TotalCount:P0}" : "无样本";
                Console.WriteLine($"  L0.5 写降级 small:  small 样本 {smallRule.TotalCount} 成功率 {RateOf(smallRule)}（阈值 {options.RoutingDowngradeMinSuccessRate:P0}）→ {(downgrade ? "触发：写任务降级 small" : "保持 large")}（{dowReason}）");
                Console.WriteLine($"  L0.5b 读升级 pro:   small 成功率 {RateOf(smallRule)}（坏率阈值 {options.RoutingReadUpgradeBadRate:P0}）/ pro 成功率 {RateOf(proRule)}（阈值 {options.RoutingReadUpgradeMinSuccessRate:P0}）→ {(readUp ? "触发：读任务升级 agent_pro" : "保持 small")}（{readReason}）");
                Console.WriteLine($"  P2 Complex 降级 large: pro 样本 {proL3.TotalCount} 成功率 {RateOf(proL3)}（阈值 {options.RoutingDropAgentProMinSuccessRate:P0}）→ {(dropPro ? "触发：ComplexReason 降级 large" : "保持 agent_pro")}（{dropReason}）");
                Console.WriteLine("  （阈值来自 AgentOptions.Routing* 配置；样本仅统计 law-unified 30B 时代）");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [调权状态] 读取失败: {ex.Message}");
            }
        }

        // 【3A】路由决策落库：决策分布（调权数据地基的可见面）
        if (decisionStore is not null)
        {
            try
            {
                var rd = await decisionStore.GetSummaryAsync(CancellationToken.None);
                if (rd.Total > 0)
                {
                    Console.WriteLine("=== 路由决策（3A 落库） ===");
                    Console.Write($"  决策总数:  ");
                    Console.WriteLine(rd.Total);
                    var svcLine = string.Join("  ", rd.ByService.Select(x => $"{x.Service}:{x.Count}"));
                    Console.WriteLine($"  按服务:    {svcLine}");
                    var featLine = string.Join("  ", rd.ByFeature.Take(5).Select(x => $"{x.Item1}:{x.Item2}"));
                    Console.WriteLine($"  按特征类:  {featLine}");
                    Console.WriteLine("  （routing_decisions 表 · 3B 自动调权数据地基）");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [路由决策] 读取失败: {ex.Message}");
            }
        }

        // 【C】工具拒绝事件：拒绝类别分布（补工具层拦截不可观测盲区）
        if (rejectStore is not null)
        {
            try
            {
                var tr = await rejectStore.GetSummaryAsync(CancellationToken.None);
                if (tr.Total > 0)
                {
                    Console.WriteLine("=== 工具拒绝（C 事件落库） ===");
                    Console.Write($"  拒绝总数:  ");
                    Console.WriteLine(tr.Total);
                    var catLine = string.Join("  ", tr.ByCategory.Select(x => $"{x.Category}:{x.Count}"));
                    Console.WriteLine($"  按类别:    {catLine}");
                    Console.WriteLine("  （tool_rejections 表 · 拦截影响面可观测）");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [工具拒绝] 读取失败: {ex.Message}");
            }
        }

        Console.WriteLine();
    }

    /// <summary>打印最近 N 条记录。</summary>
    static async Task PrintRecentAsync(IMetricsCollector metrics, int take)
    {
        var recent = await metrics.GetRecentAsync(take, CancellationToken.None);

        if (recent.Count == 0)
        {
            return;
        }

        Console.WriteLine($"=== 最近 {recent.Count} 条任务 ===");
        Console.WriteLine($"  {"时间",-20} {"模式",-14} {"结果",-8} {"耗时",-10} {"输入",-40}");
        Console.WriteLine($"  {new string('-', 20)} {new string('-', 14)} {new string('-', 8)} {new string('-', 10)} {new string('-', 40)}");

        foreach (var m in recent)
        {
            var time = m.StartedAt.ToString("MM-dd HH:mm:ss");
            var mode = m.Mode;
            var result = m.Success ? "[OK]" : "[FAIL]";
            var duration = $"{m.TotalMs} ms";
            var input = m.UserInput.Length > 38
                ? m.UserInput[..38] + ".."
                : m.UserInput;

            Console.Write($"  {time,-20} {mode,-14} ");
            Console.ForegroundColor = m.Success ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write($"{result,-8}");
            Console.ResetColor();
            Console.WriteLine($" {duration,-10} {input}");
        }

        Console.WriteLine();
    }

    /// <summary>打印最近失败的记录。</summary>
    static async Task PrintRecentFailuresAsync(IMetricsCollector metrics)
    {
        var failures = await metrics.GetRecentFailuresAsync(10, CancellationToken.None);

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("=== 最近没有失败记录 ===");
            Console.ResetColor();
            Console.WriteLine();
            return;
        }

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"=== 最近 {failures.Count} 条失败 ===");
        Console.ResetColor();

        foreach (var m in failures)
        {
            Console.WriteLine();
            Console.Write($"  [{m.StartedAt:MM-dd HH:mm:ss}] {m.Mode}  ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(m.UserInput.Length > 60 ? m.UserInput[..60] + "..." : m.UserInput);
            Console.ResetColor();

            if (!string.IsNullOrWhiteSpace(m.ErrorSummary))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                var err = m.ErrorSummary.Length > 200
                    ? m.ErrorSummary[..200] + "..."
                    : m.ErrorSummary;
                Console.WriteLine($"    错误: {err}");
                Console.ResetColor();
            }
        }

        Console.WriteLine();
    }
}