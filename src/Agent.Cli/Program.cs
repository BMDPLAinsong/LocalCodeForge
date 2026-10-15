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
internal partial class Program
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

        // ─────────── 数据源头：无头管道自动标记 E2E（10-10 可疑成功治理） ───────────
        // IsE2E 原仅由显式环境变量 LAW_E2E=1 标记——E2E 复测（type e2e_input.txt | dotnet run）
        // 一旦忘记设该变量，E2E 用例即混入真实统计（10-01/10-08 已实证漏标 2 条）。
        // 结构性修复：输入/输出任一重定向（无头管道）即 E2E 语义（与 RendererFactory 交互判定一致），
        // 自动补标 LAW_E2E=1；显式设置过 LAW_E2E（含 =0 的真实自动化管道）则尊重显式值。
        if ((Console.IsInputRedirected || Console.IsOutputRedirected)
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LAW_E2E")))
        {
            Environment.SetEnvironmentVariable("LAW_E2E", "1");
        }

        // ─────────── 配置区 ───────────
        // 读环境变量
        var dashscopeKey = Environment.GetEnvironmentVariable("DASHSCOPE_API_KEY") ?? "";

        var options = new AgentOptions
        {
            // 【10-14 A1 模型 A/B】Executor 模型覆盖（环境变量 LAW_EXECUTOR_MODEL；null=沿用 Small）。
            // A/B 实验用：设 LAW_EXECUTOR_MODEL=<已拉取 tag> 即让逐文件生成/修复改用该模型，不改代码、不改路由。
            ExecutorModel = Environment.GetEnvironmentVariable("LAW_EXECUTOR_MODEL"),
            // 意图分类器
            Classifier = new ModelEndpoint
            {
                Provider = "ollama",
                Model = "qwen2.5:1.5b",
                Endpoint = "http://localhost:11434",
                ApiKey = "ollama"
            },

            // 单模型三角色：small/large/agent_pro 共用 law-unified（Ollama 本地 30B Q4）
            // （历史：strata q2_0/iq1_m 实验已回退 → qwen3.8-27b（10-10）→ glm-4.7-flash:q4_K_M（10-11
            //   实测工具调用 XML 格式不稳全线失败，回退 law-unified——见 HANDOVER 10-11 段）
            // 角色差异由请求级 options 运行时注入（Temperature：small 0.3 / large 0.7 / agent_pro 0.5）。
            // Provider 保持 "ollama"：KernelFactory 拼接 {Endpoint}/v1 → http://localhost:11434/v1。
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
        // 【10-13 批次1】配置校验：矛盾/无效配置启动即报错退出（消"无效配置静默生效"缺陷）
        var cfgErrors = Agent.Core.Config.AgentConfigValidator.Validate(options);
        if (cfgErrors.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("=== 配置校验失败（请修正后重启） ===");
            foreach (var err in cfgErrors)
                Console.WriteLine($"  ✗ {err}");
            Console.ResetColor();
            Environment.Exit(1);
            return;
        }
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
        // 【C】路由决策表生命周期清理：启动时删除 N 天前明细（防无限增长；调权仅用近 10 条）
        provider.GetRequiredService<Agent.Infrastructure.Routing.RoutingDecisionStore>().PruneOlderThan(options.RoutingDecisionRetentionDays);
        // 【C】显式解析 ToolRejectionStore，触发 tool_rejections 表建表
        _ = provider.GetRequiredService<Agent.Infrastructure.Metrics.ToolRejectionStore>();
        // 【10-07 Dashboard】事件总线显式解析（主容器单例，任务循环旁路发布 + SSE 订阅）
        var eventHub = provider.GetRequiredService<Agent.Infrastructure.Events.TaskEventHub>();
        // 【10-07 UI 层 #1】事件持久化显式解析（触发 task_events 建表）+ 保留期清理（配置化）
        var eventStore = provider.GetRequiredService<Agent.Infrastructure.Events.TaskEventStore>();
        eventStore.PruneOlderThan(options.EventRetentionDays);
        // 【10-13 批次1】会话历史（turns 表）生命周期：此前只增不清（最大明文表），现启动按保留期清理
        provider.GetRequiredService<Agent.Core.Abstractions.ISessionStore>()
            .PruneOlderThan(options.ChatTurnRetentionDays);

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
        async Task<(List<string> errors, string? answer, bool cancelled, int writeCount)> ConsumeTaskAsync(
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
            var writeCount = 0; // 【10-13 阶段2】任务事实：write_file 成功次数（归档记忆用）
            try
            {
                await foreach (var evt in events.WithCancellation(ct))
                {
                    if (evt is AgentEvent.Error e) errors.Add(e.Message);
                    if (evt is AgentEvent.FinalAnswer fa) answer = fa.Text;
                    if (evt is AgentEvent.Routing r) svc = r.ServiceId;
                    if (evt is AgentEvent.ToolCall) step++;
                    if (evt is AgentEvent.ToolResultEvent tr && tr.Name == "write_file" && tr.Success) writeCount++;
                    if (sp is not null) sp.TaskPrefix = $"[{step}步|{svc}|{sw.Elapsed.TotalSeconds:0}s]";
                    await renderer.RenderEventAsync(evt);
                    // 【10-07 Dashboard】旁路多播：渲染器直接消费不变，hub 是第二消费者
                    // （SSE 订阅者浏览器后台/JS 卡住时 DropOldest 丢最旧，绝不阻塞任务）
                    eventHub.Publish(evt);
                    // 【10-07 UI 层 #1】事件持久化（第三消费者，fail-safe）：Dashboard 任务详情回放
                    eventStore.Record(taskId, seq++, evt);
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
            return (errors, answer, cancelled, writeCount);
        }

        // ─────────── 主循环 ───────────
        // 【10-07 Dashboard】已启动的 dashboard URL（幂等：重复 dashboard 命令只提示不重启）
        string? dashboardUrl = null;
        // 【10-07 webUI 交互】命令通道（提前声明：StartDashboardAsync 与自动启动在 397 行通道化分支前使用）
        var commandHub = new Agent.Core.CommandHub();

        // 【10-10 自动启动】交互终端启动 CLI 即自动起 Web Dashboard（浏览器自动打开）——不用手动敲 dashboard；
        // 无头态（管道/重定向）跳过（E2E 输出逐字节兼容）；LAW_NO_AUTODASH=1 可关。
        // 局部函数与 dashboard 命令共用同一启动逻辑（消双路径漂移——启动行为只有一份）。
        async Task<string?> StartDashboardAsync(bool silentIfRunning)
        {
            if (dashboardUrl is not null)
            {
                if (!silentIfRunning)
                    renderer.Info($"Dashboard 已在运行：{dashboardUrl}");
                return dashboardUrl;
            }
            // 【10-08 ③ 安全 token】随机 token 拼入 URL——CLI 打印的完整 URL 即访问凭证；
            // 前端 JS 从 location.search 取 token，所有请求自动附带（fetch 包装 + SSE query）。
            var dashToken = Guid.NewGuid().ToString("N");
            var (url, _) = await Agent.Dashboard.DashboardHost.StartAsync(
                new Agent.Dashboard.DashboardOptions(workspace, options.ProjectRoot,
                    options.LawUnifiedEpoch,
                    provider.GetRequiredService<IMetricsCollector>(), eventHub, eventStore,
                    options,
                    commandHub,
                    // 【10-14 P2-3/F5】store 一律实例注入（进程内单例；路径字段已删，F5）
                    provider.GetRequiredService<Agent.Infrastructure.Memory.AgentMemoryStore>(),
                    provider.GetRequiredService<Agent.Infrastructure.Project.SymbolIndexStore>(),
                    dashToken,
                    // 【10-14 P5-24】stats 全量对齐：特征类归因 / 路由决策 / 工具拒绝三数据源注入（可选字段）
                    provider.GetRequiredService<Agent.Infrastructure.Routing.TaskMetricsAggregator>(),
                    provider.GetRequiredService<Agent.Infrastructure.Routing.RoutingDecisionStore>(),
                    provider.GetRequiredService<Agent.Infrastructure.Metrics.ToolRejectionStore>()),
                CancellationToken.None);
            url = url + "?token=" + dashToken;
            dashboardUrl = url;
            // 交互态自动打开默认浏览器（失败静默——打不开不影响 dashboard 使用）
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
                { UseShellExecute = true });
            }
            catch { /* 浏览器打开失败忽略 */ }
            return url;
        }

        // 【10-10 自动启动入口】主循环前：交互终端 → 自动起 dashboard + 开浏览器
        // （无头态由上方守卫跳过；E2E 管道运行完全不触发）
        if (!Console.IsInputRedirected && !Console.IsOutputRedirected
            && Environment.GetEnvironmentVariable("LAW_NO_AUTODASH") != "1")
        {
            var autoUrl = await StartDashboardAsync(silentIfRunning: true);
            if (autoUrl is not null)
                renderer.Notice($"主交互入口：Web Dashboard（已自动启动，仅本机回环 + token 鉴权）：[link]{autoUrl}[/]\n  浏览器已自动打开（失败可手动复制上方 URL）；任务实时流 / stats / 历史任务 / 备份 Diff / 回滚 / 任务详情回放\n  CLI 保留兜底：use/stats/memory/rollback/plan/orch/new/exit/help 照常可用");
        }

        // 【10-12 IME 兼容修复】交互态读行改回 Console.ReadLine()（原生行编辑）。
        // 根因：自实现 ReadKey 逐键循环与中文 IME 组合/选字竞争，任务输入随机丢字——
        // "读取这个文件修复里面的语法错误" → "读取这个修复里面的语法错"（sessions.db turns 实证 2 次）。
        // 管道输入（Console.ReadLine）3 次完整、交互态中文输入 2 次损坏 → 输入管线差异即根因。
        // 取舍：Tab 命令补全 / ↑↓历史随之降级（IME 正确性优先，防打地鼠）；web 端输入不受影响（CommandHub 无 ReadKey）。
        string? ReadLineWithTabCompletion()
        {
            return Console.ReadLine();
        }

        // 输入通道化（10-07 webUI 交互）：终端输入与 Dashboard POST /api/commands
        // 统一汇入 CommandHub，主循环一次消费一条（一次一任务串行语义不变）。
        // 交互态：控制台后台线程负责行编辑（Tab/↑↓/历史）并写入通道；web 提交也写入同一通道——
        // 主循环阻塞在 NextAsync 时 web 命令即时响应（用户无需碰终端）。
        // 【10-08】提示符统一由主循环打印（空闲等待 NextAsync 前）：任务完成后必然出现新 `> `；
        //   后台线程不再自打 prompt（终端输入回显由控制台层负责，ReadLine 会回显键入字符）。
        //   修复：web 提交任务完成后无提示符（旧实现后台线程阻塞在 ReadLine，无人补 prompt）。
        // 无头（管道/重定向）：保持原同步读-执行循环（逐字节兼容 E2E）。
        var consoleReaderStopped = false;
        if (!Console.IsInputRedirected && !Console.IsOutputRedirected)
        {
            _ = Task.Run(() =>
            {
                while (!consoleReaderStopped)
                {
                    var line = ReadLineWithTabCompletion();
                    if (line is null) { commandHub.TrySubmit(null); break; }   // stdin EOF
                    commandHub.TrySubmit(line);
                }
            });
        }

        int currentSeq = 0;   // 当前命令的提交序号（web 归属标记：任务执行期间 /api/current-task 可见）
        string? pendingTask = null;   // 【10-08 C】use <path> <任务> 一步到位：use 后的后续任务自动执行
        // 【10-08 会话写目标延续】会话内最近文件级写目标（去重、最近 3 条）。
        // 清理时机：new 开新会话 / use 切换工程根 → 清空；exit/重启 → 进程级自然清空；任务失败 → 保留。
        var sessionWriteTargets = new List<(string Path, bool IsFile)>();
        while (true)
        {
            string? input;
            if (pendingTask is not null)
            {
                input = pendingTask;
                pendingTask = null;
            }
            else if (Console.IsInputRedirected || Console.IsOutputRedirected)
            {
                // 无头路径：原同步读-执行（管道输出逐字节兼容）
                renderer.Prompt();
                input = ReadLineWithTabCompletion();
            }
            else
            {
                // 交互态：空闲等待前打提示符（统一来源——任务完成后总显示 `> `，可继续输入）；
                // 终端输入回显由控制台层负责。null/EOF = 退出
                renderer.Prompt();
                var next = await commandHub.NextAsync(CancellationToken.None);
                if (next is null || next.Value.Input is null) { consoleReaderStopped = true; break; }
                input = next.Value.Input;
                currentSeq = next.Value.Seq;
                // 【10-08】web 提交命令终端回显：终端输入由 ReadLine 天然回显（控制台回显键入）；
                // web POST /api/commands 无回显 → 消费后补打一行（提示符 `> ` 已由主循环打印，
                // 正好连成 `> {input}`，CLI 端能看到任务内容，与交互态视觉一致）。
                if (next.Value.Source == "web") Console.WriteLine(input);
            }
            // 【A-补强】统一剥离 BOM（U+FEFF）——PS 5.1 管道注入 UTF-8 BOM 到首行时
            // StartsWith("use ")/StartsWith("plan ") 等全部不命中、命令被当任务执行。
            // 原 370 行仅 use 分支 trim；此处提到主循环入口，覆盖所有命令分支。
            // 【批次1·补充】Trim() 防 "exit "（管道/尾随空白）漏判被当任务执行（实测现象）。
            input = input?.TrimStart('\uFEFF')?.Trim();

            // 空/退出守卫：显式保留 `input is null` 于调用点——原内联写法顺带为后续全部分派
            // 提供「input 非空」的类型收窄（抽取到 IsExitCommand 后编译器不再知晓该事实）。
            if (input is null || IsExitCommand(input))
            {
                consoleReaderStopped = true;
                break;
            }

            if (input.Equals("new", StringComparison.OrdinalIgnoreCase))
            {
                HandleNewCommand(ref sessionId, sessionWriteTargets, renderer);
                continue;
            }

            // ═══════════ help 命令（命令表速查） ═══════════
            if (input.Equals("help", StringComparison.OrdinalIgnoreCase) || input == "?")
            {
                HandleHelpCommand(renderer);
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

            // ═══════════ dashboard 命令（10-07 真 UI 层·本地 Web Dashboard；10-10 与自动启动共用 StartDashboardAsync） ═══════════
            if (input.Equals("dashboard", StringComparison.OrdinalIgnoreCase))
            {
                await HandleDashboardCommandAsync(renderer, StartDashboardAsync);
                continue;
            }

            // ═══════════ stats 命令 ═══════════
            if (input.StartsWith("stats", StringComparison.OrdinalIgnoreCase))
            {
                await HandleStatsCommandAsync(input, provider, options, featureAggregator, renderer);
                continue;
            }

            // ═══════════ memory 命令（记忆治理：list/stats/forget/prune/dedup） ═══════════
            if (input.StartsWith("memory", StringComparison.OrdinalIgnoreCase))
            {
                HandleMemoryCommand(input, provider, renderer);
                continue;
            }

            // ═══════════ use 命令（工程感知 P0/P1；10-08 A：裸 use 缺路径命令层拦截） ═══════════
            // E2E 基建：PS 5.1 -File 模式向子进程 stdin 注入 UTF-8 BOM（EF BB BF）到首行
            // → 剥离 BOM（U+FEFF），否则 use 行被当任务执行
            var trimmedInput = input.TrimStart('\uFEFF');
            // 【10-14 P3-3】use 分派块 + 裸 use 拦截搬移至 HandleUseCommand（continue → return）；
            // trimmedInput 仍留在主循环（自动识别块共用同一变量）。
            // 门控合并（本次唯一超出"纯搬移"的改动，已验证等价）：原为两段——TryParse 真 →走use 块；
            // 否则 IsUseCommand 真 → 打印用法。合并为单一 IsUseCommand 门，因 TryParse 真 ⟹ 输入匹配
            // ^use(\s|$) ⟹ IsUseCommand 真（UseCommandParser.cs:19-21 与 44-45），两分支并集 == IsUseCommand；
            // 处理器内仍先 TryParse 后 IsUseCommand，判定顺序与输出逐字不变。
            if (Agent.Infrastructure.Project.UseCommandParser.IsUseCommand(trimmedInput))
            {
                HandleUseCommand(trimmedInput, provider, options, sessionWriteTargets, renderer, ref pendingTask);
                continue;
            }

            // ═══════════ 双根保护·输入路径自动识别（给路径就干活） ═══════════
            // 输入含"存在的绝对目录路径 + 读/分析语境（非写任务）" → 自动只读 use。
            // 敏感区路径源头不 use（SensitivePathGuard.IsDenied，内容不进 chatHistory）；
            // 显式 use 优先（已设工程根不覆盖）；写任务不触发（路径是目标文件而非读目标）。
            AutoDetectUseFromInput(trimmedInput, provider, options);

            // ═══════════ diff 命令（10-07，Diff 引擎终端通道） ═══════════
            // diff [taskId]：显示该任务在工程内的备份 vs 当前文件的行级差异（unified hunk 红绿渲染）。
            // 无 taskId → 最近任务；非工程模式 → 提示。Web Dashboard 的 /api/diff 复用同一引擎（无 DTO）。
            if (input.Equals("diff", StringComparison.OrdinalIgnoreCase)
                || input.StartsWith("diff ", StringComparison.OrdinalIgnoreCase))
            {
                HandleDiffCommand(input, workspace, options, lastTaskId, renderer);
                continue;
            }

            // ═══════════ rollback 命令（工程感知 P1） ═══════════
            if (input.StartsWith("rollback", StringComparison.OrdinalIgnoreCase))
            {
                HandleRollbackCommand(input, workspace, options, lastTaskId, renderer);
                continue;
            }

            // ═══════════ 【自动分发】 ═══════════
            // 【10-14 P5-27 双端统一】输入长度上限与 web /api/commands 同源（AgentOptions.MaxCommandInputLength）——
            // 原终端无限制、web 硬编码 2000（同一产品两个入口行为不同）。超限打印提示跳过，不进任务流。
            if (input.Length > options.MaxCommandInputLength)
            {
                renderer.Warn($"[输入超限] 任务输入 {input.Length} 字符，超过上限 {options.MaxCommandInputLength} 字符。请缩短后重试。");
                continue;
            }

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
            // 【10-08 ② 任务级超时】Timer 自动触发 taskCts.Cancel——与 q/web 取消同一令牌；
            // timeoutFired 区分来源（只有超时触发才打印提示，q/web 取消不误报）；
            // 超时后 Orchestrator 阶段间响应取消 → cancelled → 照常走失败回滚承接。
            var timeoutFired = false;
            System.Threading.Timer? timeoutTimer = null;
            if (options.TaskTimeoutMinutes > 0)
            {
                var timeout = TimeSpan.FromMinutes(options.TaskTimeoutMinutes);
                timeoutTimer = new System.Threading.Timer(
                    _ => { timeoutFired = true; taskCts.Cancel(); },
                    null, timeout, System.Threading.Timeout.InfiniteTimeSpan);
            }
            // 【P0·web 取消】挂载当前任务取消源——Dashboard POST /api/cancel 经 CommandHub 触发同一令牌
            // （与终端 q 键同一取消机制；任务结束 finally 摘除）。
            commandHub.AttachTaskCancellation(taskCts);
            // 【P1·归属】挂载当前任务提交序号与输入——GET /api/current-task 显示"当前任务 = 哪条提交"。
            commandHub.AttachTaskInfo(currentSeq, input);
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

                // 【10-08 会话写目标延续】"这个文件/该文件/刚建的文件…"指代词命中 → 继承上一轮文件级
                // 写目标（白名单 + 达成门 + 上下文注入三处同开关）；未命中保持任务隔离（显式优于隐式）。
                var referPronoun = Agent.Infrastructure.Project.ProjectPathDetector.HasReferentialPronoun(input);
                if (referPronoun && writeTarget is null)
                {
                    var inherited = sessionWriteTargets.LastOrDefault();
                    if (inherited.IsFile
                        && !Agent.Infrastructure.Project.SensitivePathGuard.IsDenied(inherited.Path))
                    {
                        writeTarget = inherited.Path;
                        writeTargetIsFile = true;
                        explicitWriteRoots.Add(inherited.Path);
                        renderer.Info($"[会话写目标] 检测到指代（\"这个文件\"），继承上轮写目标 {inherited.Path}。");
                    }
                }
                // 本轮解析出的文件级写目标记入会话（去重，最近 3 条）——任务失败也保留（用户可能重试）。
                if (writeTarget is not null && writeTargetIsFile)
                {
                    sessionWriteTargets.RemoveAll(s => string.Equals(s.Path, writeTarget, StringComparison.OrdinalIgnoreCase));
                    sessionWriteTargets.Add((writeTarget, true));
                    while (sessionWriteTargets.Count > 3) sessionWriteTargets.RemoveAt(0);
                }

                // 【关键修复】为每个任务创建独立 DI 容器（工具绑定 run 根）——P-B 工具层注入 taskId 隔离备份
                var taskServices = new ServiceCollection();
                taskServices.AddAgent(options, workspace, dbPath, options.ProjectRoot, options.ProjectWriteEnabled, taskId, explicitWriteRoots);
                var taskProvider = taskServices.BuildServiceProvider();

                if (TaskClassifier.ShouldOrchestrate(input,
                        options.ProjectRoot is not null && options.ProjectWriteEnabled))
                {
                    // 走三阶段编排
                    renderer.Notice($"[自动编排] 任务复杂，启用 Planner-Executor");

                    var orchestrator = taskProvider.GetRequiredService<IOrchestrator>();
                    var (orchErrors, orchAnswer, cancelled, orchWrites) = await ConsumeTaskAsync(
                        taskId, orchestrator.RunAsync(input, workspace, taskCts.Token), sp, taskCts.Token);
                    if (!cancelled)
                        ArchiveTask(taskProvider, input, taskId, "orchestrator", orchErrors, orchAnswer, orchWrites);
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

                    var (loopErrors, loopAnswer, cancelled, loopWrites) = await ConsumeTaskAsync(
                        taskId, taskLoop.RunAsync(request, taskCts.Token), sp, taskCts.Token);
                    if (!cancelled)
                        ArchiveTask(taskProvider, input, taskId, "agent", loopErrors, loopAnswer, loopWrites);
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
                    BackupService.PruneBackups(workspace, options.BackupKeepLastN);
            }
            finally
            {
                timeoutTimer?.Dispose();
                listenCts.Cancel();
                commandHub.DetachTaskCancellation();
                commandHub.DetachTaskInfo();
                if (timeoutFired)
                {
                    try { sp?.Notice($"[任务] 超过 {options.TaskTimeoutMinutes} 分钟未完成，已自动取消。"); }
                    catch { /* 渲染失败忽略 */ }
                }
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
        // 【10-08 盘符防御】盘符形态 "X:" → "X:\"：.NET Path.GetFullPath("X:") 按盘符相对语义
        // 解析成"X 盘上的当前目录"（cwd），而非盘根——Directory.Exists("X:") 为 true 会掩盖静默错位。
        var raw = rest;
        if (raw.Length == 2 && raw[1] == ':') raw += "\\";
        var proj = Path.GetFullPath(raw);
        // 防静默错位：解析结果与输入首段盘符不一致（如 cwd 在别的盘时 "X:" 解析成 C:\...）→ 判失败。
        if (proj.Length >= 2 && proj[1] == ':' && char.ToUpperInvariant(proj[0]) != char.ToUpperInvariant(raw[0]))
        {
            Console.WriteLine($"错误：路径解析异常（盘符不一致）：{rest}");
            return;
        }
        if (!Directory.Exists(proj))
        {
            Console.WriteLine($"错误：目录不存在：{proj}");
            return;
        }
        if (autoSource is not null)
            Console.WriteLine($"[自动识别] 检测到工程路径 {proj}（只读），已设置工程根。");
        options.ProjectRoot = proj;
        options.ProjectWriteEnabled = writeFlag;
        // 【10-14 P3-1 门禁修正】use 路径改走 GetOrCompute：原实现先无条件全量 Scan（含目录树/上下文块
        // 构建，最贵的一步）**之后**才查缓存——缓存命中也照付扫描成本，且日志"未重新扫描"与事实不符。
        // 现由签名校验决定是否真扫；日志按实际 cacheHit 输出。
        var scan = provider.GetRequiredService<ProjectContextStore>().GetOrCompute(proj, out var ctxCacheHit);
        Console.WriteLine(scan.Overview);
        Console.WriteLine(ctxCacheHit
            ? "（工程上下文命中缓存，未重新扫描）"
            : "（工程上下文已重新扫描并写入缓存）");
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
        string mode, List<string> errors, string? finalAnswer, int writtenFileCount = 0)
    {
        try
        {
            var success = finalAnswer is not null;
            var (topic, content) = Agent.Infrastructure.Memory.TaskMemoryExtractor.Extract(
                input, success, errors, mode, finalAnswer, writtenFileCount);
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

        // 收集所有本地端点（Provider == "ollama"）——【10-08 ⑤】补 Large：此前只查三角色，Large 配置错误不会被发现
        var localEndpoints = new List<(string Name, ModelEndpoint Endpoint)>
        {
            ("Classifier", options.Classifier),
            ("Small",      options.Small),
            ("AgentPro",   options.AgentPro),
            ("Large",      options.Large),
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

        // 探活：成功返回模型集 + 状态码 + 服务类型；HTTP 非成功（服务在跑但异常）返回 (null, code)；连接失败抛异常
        // 【10-09 strata 兼容】双协议探活：先试 OpenAI 兼容 /v1/models（strata 等），失败/404 回退 Ollama /api/tags。
        async Task<(HashSet<string>? Models, int StatusCode, string ServiceKind)> ProbeEndpointAsync(string ep)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var baseUrl = ep.TrimEnd('/');

            // 1) OpenAI 兼容 /v1/models（strata）
            try
            {
                var resp1 = await http.GetAsync($"{baseUrl}/v1/models");
                if (resp1.IsSuccessStatusCode)
                {
                    var json = await resp1.Content.ReadAsStringAsync();
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    return (
                        doc.RootElement
                            .GetProperty("data")
                            .EnumerateArray()
                            .Select(m => m.GetProperty("id").GetString() ?? "")
                            .ToHashSet(StringComparer.OrdinalIgnoreCase),
                        (int)resp1.StatusCode,
                        "strata/OpenAI 兼容服务");
                }
                // 服务在跑但无此端点（Ollama 无 /v1/models）→ 回退 /api/tags
            }
            catch
            {
                // 连接失败 → 继续走 /api/tags（让原始连接异常向上抛出）
            }

            // 2) Ollama /api/tags
            var resp = await http.GetAsync($"{baseUrl}/api/tags");
            var statusCode = (int)resp.StatusCode;

            if (!resp.IsSuccessStatusCode)
            {
                return (null, statusCode, "Ollama");
            }

            // 解析返回的 JSON，提取模型名列表
            var json2 = await resp.Content.ReadAsStringAsync();
            using var doc2 = System.Text.Json.JsonDocument.Parse(json2);
            return (
                doc2.RootElement
                    .GetProperty("models")
                    .EnumerateArray()
                    .Select(m => m.GetProperty("name").GetString() ?? "")
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                statusCode,
                "Ollama");
        }

        // 【10-09 strata 兼容】按端点分组探活：Classifier 仍在 11434（Ollama），三角色在 8080（strata）。
        // 单一端点探活会让跨端配置误报"未安装"，故每个唯一端点独立探活，模型集按端点映射。
        var uniqueEndpoints = localEndpoints
            .Select(e => e.Endpoint.Endpoint.TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var endpointModels = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var endpointKinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var anyOnline = false;
        var anyError = false;
        var firstErrorCode = 0;
        var firstProbeEndpoint = localEndpoints[0].Endpoint.Endpoint.TrimEnd('/');
        var firstProbeEx = (Exception?)null;

        foreach (var ep in uniqueEndpoints)
        {
            try
            {
                var (models, code, kind) = await ProbeEndpointAsync(ep);
                if (models != null)
                {
                    endpointModels[ep] = models;
                    endpointKinds[ep] = kind;
                    anyOnline = true;
                }
                else
                {
                    anyError = true;
                    firstErrorCode = code;
                    endpointKinds[ep] = kind;
                }
            }
            catch (Exception ex)
            {
                anyError = true;
                firstErrorCode = 0;
                endpointKinds[ep] = "Ollama";
                if (string.Equals(ep, firstProbeEndpoint, StringComparison.OrdinalIgnoreCase))
                {
                    firstProbeEx = ex;
                }
            }
        }

        // 仅"连接失败"触发自动启动；HTTP 非成功（服务在跑但异常）不触发
        // 【10-09 strata 兼容】仅默认 Ollama 端口（11434）自动拉起 ollama serve；
        // strata 等 OpenAI 兼容服务（8080）连接失败不自动拉起（它没有 serve 子命令）。
        var isDefaultOllamaPort = Uri.TryCreate(firstProbeEndpoint, UriKind.Absolute, out var probeUri)
            && probeUri.Port == 11434;

        if (!anyOnline && !anyError && firstProbeEx != null && !isDefaultOllamaPort)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  无法连接模型服务 {firstProbeEndpoint}（strata/OpenAI 兼容端点无 serve 自动拉起）。");
            Console.WriteLine($"    请确认 strata 已启动并已加载模型：{string.Join("、", localEndpoints.Select(e => e.Endpoint.Model).Distinct())}");
            Console.ResetColor();
            Console.WriteLine();
            return;
        }

        if (!anyOnline && firstProbeEx != null && isDefaultOllamaPort)
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
            for (var i = 0; i < 15 && !endpointModels.ContainsKey(firstProbeEndpoint); i++)
            {
                await Task.Delay(1000);

                try
                {
                    var (m, _, kind) = await ProbeEndpointAsync(firstProbeEndpoint);
                    if (m != null)
                    {
                        endpointModels[firstProbeEndpoint] = m;
                        endpointKinds[firstProbeEndpoint] = kind;
                        anyOnline = true;
                    }
                }
                catch
                {
                    // 尚未就绪，继续轮询
                }
            }
        }

        if (anyOnline)
        {
            var totalModels = endpointModels.Values
                .SelectMany(x => x)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var kindText = string.Join(" + ", endpointKinds.Values.Distinct());
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  [OK] 模型服务在线（{endpointModels.Count} 个端点 / {totalModels} 个模型 / {kindText}）");
            Console.ResetColor();
        }
        else if (anyError)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  [FAIL] 模型服务异常（HTTP {firstErrorCode}）");
            Console.ResetColor();
            Console.WriteLine();
            return;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  [FAIL] 无法连接模型服务：自动启动后 15 秒内服务未就绪。");
            Console.WriteLine("    请确认服务已启动（strata 8080 / ollama serve 11434）。");
            Console.ResetColor();
            Console.WriteLine();
            return;
        }

        // ── 检查 2：按端点逐个检查模型是否已加载/拉取 ──
        var missing = new List<string>();

        foreach (var (name, endpoint) in localEndpoints)
        {
            var ep = endpoint.Endpoint.TrimEnd('/');
            var modelName = endpoint.Model;

            if (!endpointModels.TryGetValue(ep, out var models))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  [FAIL]  {name,-10} {modelName}  (端点 {ep} 不可达)");
                Console.ResetColor();
                missing.Add(modelName);
                continue;
            }

            // Ollama 的模型名可能带 ":latest" 或 ":tag"，兼容处理
            var found = models.Contains(modelName)
                || models.Contains($"{modelName}:latest");

            if (found)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  [OK] {name,-10} {modelName}");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  [FAIL]  {name,-10} {modelName}  (未安装/未加载)");
                Console.ResetColor();
                missing.Add(modelName);
            }
        }

        // ── 汇总缺失模型，给出修复命令 ──
        if (missing.Count > 0)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            var strataMissing = localEndpoints.Any(e =>
                endpointKinds.TryGetValue(e.Endpoint.Endpoint.TrimEnd('/'), out var k)
                && k.StartsWith("strata", StringComparison.OrdinalIgnoreCase));

            if (strataMissing)
            {
                Console.WriteLine($"  以下模型在 strata 中未加载，请确认部署服务已包含：");
                foreach (var m in missing)
                {
                    Console.WriteLine($"    {m}");
                }
            }
            else
            {
                Console.WriteLine($"  以下模型缺失，请执行：");
                foreach (var m in missing)
                {
                    Console.WriteLine($"    ollama pull {m}");
                }
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

        // 【10-08 metrics 数据治理】E2E 用例数单列（真实统计已剔除）
        try
        {
            var e2e = await metrics.GetE2ECountAsync(CancellationToken.None);
            if (e2e > 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  E2E 用例:  {e2e} 条（故意构造的验证用例，已从真实统计剔除）");
                Console.ResetColor();
            }
        }
        catch
        {
            // 旧库无 is_e2e 时忽略
        }

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
        // 【10-14 P2-1 单源】样本查询 + 三判定改调 RoutingTuningReporter（与 Dashboard /api/tuning 同源）：
        // 原此处与 Dashboard 各写一份查询 + 判定（10-11 的 P2 层级错配正是从这类双出口漏改流出）。
        // 时代过滤语义在 reporter 内（读 options.LawUnifiedEpoch——本方法唯一调用点传入同值，见 stats 命令）。
        if (options is not null)
        {
            try
            {
                var t = await Agent.Infrastructure.Routing.RoutingTuningReporter.BuildAsync(metrics, options, CancellationToken.None);
                static string RateOf(Agent.Infrastructure.Routing.ServiceStats s) =>
                    s.TotalCount > 0 ? $"{(double)s.SuccessCount / s.TotalCount:P0}" : "无样本";
                Console.WriteLine("=== 路由调权状态 ===");
                Console.WriteLine($"  L0.5 写降级 small:  small 样本 {t.Small.TotalCount} 成功率 {RateOf(t.Small)}（阈值 {options.RoutingDowngradeMinSuccessRate:P0}）→ {(t.WriteDowngrade.Triggered ? "触发：写任务降级 small" : "保持 large")}（{t.WriteDowngrade.Reason}）");
                Console.WriteLine($"  L0.5b 读升级 pro:   small 成功率 {RateOf(t.Small)}（坏率阈值 {options.RoutingReadUpgradeBadRate:P0}）/ pro 成功率 {RateOf(t.Pro)}（阈值 {options.RoutingReadUpgradeMinSuccessRate:P0}）→ {(t.ReadUpgrade.Triggered ? "触发：读任务升级 agent_pro" : "保持 small")}（{t.ReadUpgrade.Reason}）");
                Console.WriteLine($"  P2 Complex 降级 large: pro 样本 {t.Pro.TotalCount} 成功率 {RateOf(t.Pro)}（阈值 {options.RoutingDropAgentProMinSuccessRate:P0}）→ {(t.ComplexDrop.Triggered ? "触发：ComplexReason 降级 large" : "保持 agent_pro")}（{t.ComplexDrop.Reason}）");
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
