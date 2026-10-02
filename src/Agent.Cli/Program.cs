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

            // 小模型：law-small（30B Custom Model，temp 0.3，单文件/轻任务确定性优先）
            // 单模型三角色：small/large/agent_pro 共用同一底层 30B，由 Modelfile 各自定制参数；
            // 运行时靠任务粒度清场（OllamaStopService）保证单驻，磁盘 blob 共享不额外拉取。
            Small = new ModelEndpoint
            {
                Provider = "ollama",
                Model = "law-small",
                Endpoint = "http://localhost:11434",
                ApiKey = "ollama"
            },

            // 大模型：law-large（30B Custom Model，temp 0.7，复杂推理/多文件/规划）
            // 云端 DashScope 仅预留兜底（未来可选），当前不使用云端。
            Large = new ModelEndpoint
            {
                Provider = "ollama",
                Model = "law-large",
                Endpoint = "http://localhost:11434",
                ApiKey = "ollama"
            },

            // AgentPro：law-agentpro（30B Custom Model，temp 0.5，工程理解中介）
            // 由 4B 升级为 30B：补齐工程理解能力短板（此前 4B 成功率 35%）
            AgentPro = new ModelEndpoint
            {
                Provider = "ollama",
                Model = "law-agentpro",
                Endpoint = "http://localhost:11434",
                ApiKey = "ollama"
            },

            EnableRouting = true,
            MaxSteps = 20,
            Temperature = 0.2
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
        // 【新增】清理旧 run 目录
        CleanOldRuns(runsRoot, keepDays: 7, maxCount: 50);
        Directory.CreateDirectory(runsRoot);

        // 用启动时间 + 4 位随机数生成 run 名字
        var runName = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
        var workspace = Path.Combine(runsRoot, runName);
        Directory.CreateDirectory(workspace);

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

        // ─────────── 构建 DI 容器 ───────────
        var services = new ServiceCollection();
        services.AddAgent(options, workspace, dbPath, options.ProjectRoot);
        var provider = services.BuildServiceProvider();
        // 【关键】启动时显式解析 IMetricsCollector，触发 SQLite 建表
        // 否则 Singleton 工厂不会被调用，metrics.db 不会创建
        _ = provider.GetRequiredService<IMetricsCollector>();

        // 取出 Agent 主循环
        var loop = provider.GetRequiredService<IAgentLoop>();

        // 会话 ID
        var sessionId = Guid.NewGuid().ToString("N");

        // ─────────── 打印欢迎 ───────────
        Console.WriteLine("=== Local AI Workbench ===");
        Console.WriteLine($"运行目录: {workspace}");
        Console.WriteLine($"会话库:   {dbPath}");
        Console.WriteLine("输入需求，回车执行。输入 exit 退出。输入 new 开启新会话。");
        Console.WriteLine();

        // ─────────── 打印配置 ───────────
        Console.WriteLine("=== 实际生效配置 ===");
        Console.WriteLine($"Classifier: {options.Classifier.Model}");
        Console.WriteLine($"Small:      {options.Small.Model}");
        Console.WriteLine($"AgentPro:   {options.AgentPro.Model}   (ComplexToPro={options.ComplexToPro})");
        Console.WriteLine($"Large:      {options.Large.Model}");
        Console.WriteLine();

        // ─────────── 本地模型健康检查 ───────────
        await CheckLocalModelsAsync(options);

        // ─────────── 主循环 ───────────
        while (true)
        {
            Console.Write("> ");
            var input = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(input) || input.Equals("exit", StringComparison.OrdinalIgnoreCase))
                break;

            if (input.Equals("new", StringComparison.OrdinalIgnoreCase))
            {
                sessionId = Guid.NewGuid().ToString("N");
                Console.WriteLine("[系统] 已开启新会话。");
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
                    switch (evt)
                    {
                        case AgentEvent.PlanGenerated pg:
                            Console.ForegroundColor = ConsoleColor.DarkYellow;
                            Console.WriteLine($"[规划] {pg.Summary}（{pg.Language}，{pg.FilePaths.Count} 个文件）");
                            foreach (var p in pg.FilePaths)
                                Console.WriteLine($"  - {p}");
                            Console.ResetColor();
                            break;

                        case AgentEvent.FileWritten fw:
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine($"[写入] {fw.Path}（{fw.Size} 字符）");
                            Console.ResetColor();
                            break;

                        case AgentEvent.ToolCall tc:
                            Console.ForegroundColor = ConsoleColor.DarkCyan;
                            Console.WriteLine($"[工具调用] {tc.Name}  {FormatToolArgs(tc.Name, tc.Args)}");
                            Console.ResetColor();
                            break;

                        case AgentEvent.ToolResultEvent tr:
                            Console.ForegroundColor = tr.Success ? ConsoleColor.DarkGray : ConsoleColor.Red;
                            Console.WriteLine($"[工具结果] {tr.Name}: {Truncate(tr.Output, 500)}");
                            Console.ResetColor();
                            break;

                        case AgentEvent.FinalAnswer fa:
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine($"[完成] {fa.Text}");
                            Console.ResetColor();
                            Console.WriteLine();
                            break;

                        case AgentEvent.Error e:
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"[错误] {e.Message}");
                            Console.ResetColor();
                            break;
                    }
                }

                continue;
            }
            // ═══════════ Orchestrator 测试结束 ═══════════

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
                    await PrintSummaryAsync(metrics);
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

                switch (sub)
                {
                    case "list":
                    {
                        var n2 = parts.Length > 2 && int.TryParse(parts[2], out var take2) ? take2 : 20;
                        foreach (var m in mem.List(n2))
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
                        Console.WriteLine($"记忆统计：共 {st.Total} 条（model {st.ModelCount} / task {st.TaskCount}），总命中 {st.TotalHits} 次");
                        foreach (var m in mem.List(10))
                            Console.WriteLine($"  [{m.Id}] {m.Kind} | hits={m.HitCount} count={m.Count} | {m.Topic}");
                        break;
                    }
                }
                continue;
            }

            // ═══════════ use 命令（工程感知 P0/P1） ═══════════
            if (input.StartsWith("use ", StringComparison.OrdinalIgnoreCase))
            {
                var rest = input.Substring(4).Trim();
                var writeFlag = rest.StartsWith("--write ", StringComparison.OrdinalIgnoreCase);
                if (writeFlag) rest = rest.Substring(8).Trim();
                var proj = Path.GetFullPath(rest);
                if (!Directory.Exists(proj))
                {
                    Console.WriteLine($"错误：目录不存在：{proj}");
                    continue;
                }
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
                    Console.WriteLine("可用备份：");
                    var backups = BackupService.ListBackups(workspace);
                    if (backups.Count == 0)
                        Console.WriteLine("  (无备份)");
                    foreach (var b in backups.Take(20))
                        Console.WriteLine($"  [{b.Stamp}] {b.Rel}");
                    Console.WriteLine("用法：rollback 恢复本任务全部备份；rollback <相对路径> 恢复单个文件最近一次备份。");
                    continue;
                }
                if (parts[1].Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    var n = BackupService.RollbackTask(workspace, options.ProjectRoot);
                    Console.WriteLine($"已回滚全部：{n} 个文件恢复到最近备份。");
                    continue;
                }
                var rel = string.Join(' ', parts.Skip(1)).Trim();
                var dest = BackupService.Rollback(workspace, rel, options.ProjectRoot);
                Console.WriteLine(dest is null
                    ? $"没有找到 {rel} 的备份。"
                    : $"已回滚：{dest}");
                continue;
            }

            // ═══════════ 【自动分发】 ═══════════
            // 【方案 A·run 级工作目录】一次 CLI 运行 = 一个工作空间：
            // 工具根直接用 run 根（workspace），任务间共享产物——
            // "接着更改上一个脚本"可 read_file 到前一任务写入的文件。
            // 任务目录 task_<id> 不再创建（避免空壳目录误导 list_dir），
            // taskId 仅作为记忆归档溯源（source=task_<id>）。
            var taskId = Guid.NewGuid().ToString("N")[..8];

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[工作目录] {Path.GetFileName(workspace)}（任务归属 task_{taskId}）");
            Console.ResetColor();

            // 【关键修复】为每个任务创建独立 DI 容器（工具绑定 run 根）
            var taskServices = new ServiceCollection();
            taskServices.AddAgent(options, workspace, dbPath, options.ProjectRoot, options.ProjectWriteEnabled);
            var taskProvider = taskServices.BuildServiceProvider();

            if (TaskClassifier.ShouldOrchestrate(input))
            {
                // 走三阶段编排
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine($"[自动编排] 任务复杂，启用 Planner-Executor");
                Console.ResetColor();

                var orchestrator = taskProvider.GetRequiredService<IOrchestrator>();
                var orchErrors = new List<string>();
                string? orchAnswer = null;
                await foreach (var evt in orchestrator.RunAsync(input, workspace, CancellationToken.None))
                {
                    if (evt is AgentEvent.Error e) orchErrors.Add(e.Message);
                    if (evt is AgentEvent.FinalAnswer fa) orchAnswer = fa.Text;
                    RenderEvent(evt);
                }
                ArchiveTask(taskProvider, input, taskId, "orchestrator", orchErrors, orchAnswer);
            }
            else
            {
                // 走原有 AgentLoop
                var taskLoop = taskProvider.GetRequiredService<IAgentLoop>();
                var request = new AgentRequest(sessionId, input, workspace);

                var loopErrors = new List<string>();
                string? loopAnswer = null;
                await foreach (var evt in taskLoop.RunAsync(request, CancellationToken.None))
                {
                    if (evt is AgentEvent.Error e) loopErrors.Add(e.Message);
                    if (evt is AgentEvent.FinalAnswer fa) loopAnswer = fa.Text;
                    RenderEvent(evt);
                }
                ArchiveTask(taskProvider, input, taskId, "agent", loopErrors, loopAnswer);
            }
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
            store.Add("task", topic, content, $"task_{taskId}");
        }
        catch
        {
            // 归档失败不阻塞任务
        }
    }

    // ─────────── 辅助方法 ───────────

    /// <summary>字符串截断</summary>
    static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...";

    /// <summary>
    /// 根据工具类型，智能格式化参数显示。
    /// write_file 只显示 path 和 content 长度，不显示全文。
    /// </summary>
    static string FormatToolArgs(string toolName, System.Text.Json.JsonElement args)
    {
        // 【新增】剥离工具名前缀
        // SK 会把 pluginName 拼到工具名前，形如 "agent_tools-list_dir" 或 "_-list_dir"
        var dashIndex = toolName.IndexOf('-');
        if (dashIndex > 0 && dashIndex < toolName.Length - 1)
        {
            toolName = toolName[(dashIndex + 1)..];
        }

        try
        {
            // write_file：显示 path + content 长度
            if (toolName.Contains("write_file"))
            {
                var path = args.TryGetProperty("path", out var p) ? p.GetString() : "?";
                var content = args.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                return $"path=\"{path}\", content={content.Length} 字符";
            }

            // run_command：显示 command
            if (toolName.Contains("run_command"))
            {
                var cmd = args.TryGetProperty("command", out var c) ? c.GetString() : "?";
                return $"command=\"{cmd}\"";
            }

            // list_dir / read_file：显示 path
            if (toolName.Contains("list_dir") || toolName.Contains("read_file"))
            {
                var path = args.TryGetProperty("path", out var p) ? p.GetString() : ".";
                return $"path=\"{path}\"";
            }

            // 其它工具：原样输出，超长截断
            var raw = args.ToString();
            return raw.Length > 150 ? raw[..150] + "..." : raw;
        }
        catch
        {
            var raw = args.ToString();
            return raw.Length > 150 ? raw[..150] + "..." : raw;
        }
    }

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
    /// 统一渲染 Agent 事件。
    /// </summary>
    static void RenderEvent(AgentEvent evt)
    {
        switch (evt)
        {
            case AgentEvent.Routing rt:
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                var intentText = string.IsNullOrEmpty(rt.Intent) ? "" : $" [{rt.Intent}]";
                Console.WriteLine($"[路由] {rt.Tier}{intentText} → {rt.ServiceId}");
                Console.ResetColor();
                break;

            case AgentEvent.PlanGenerated pg:
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"[规划] {pg.Summary}（{pg.Language}，{pg.FilePaths.Count} 个文件）");
                foreach (var p in pg.FilePaths)
                    Console.WriteLine($"  - {p}");
                Console.ResetColor();
                break;

            case AgentEvent.FileWritten fw:
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[写入] {fw.Path}（{fw.Size} 字符）");
                Console.ResetColor();
                break;

            case AgentEvent.ToolCall tc:
                // 剥前缀：_-run_command → run_command
                var displayName = tc.Name;
                var di = displayName.IndexOf('-');
                if (di > 0 && di < displayName.Length - 1)
                    displayName = displayName[(di + 1)..];
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[工具调用] {tc.Name}  {FormatToolArgs(tc.Name, tc.Args)}");
                Console.ResetColor();
                break;

            case AgentEvent.ToolResultEvent tr:
                // 剥前缀
                var resultName = tr.Name;
                var rdi = resultName.IndexOf('-');
                if (rdi > 0 && rdi < resultName.Length - 1)
                    resultName = resultName[(rdi + 1)..];
                Console.ForegroundColor = tr.Success ? ConsoleColor.DarkGray : ConsoleColor.Red;
                Console.WriteLine($"[工具结果] {tr.Name}: {Truncate(tr.Output, 300)}");
                Console.ResetColor();
                break;

            case AgentEvent.FinalAnswer fa:
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[完成] {fa.Text}");
                Console.ResetColor();
                Console.WriteLine();
                break;

            case AgentEvent.Error e:
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[错误] {e.Message}");
                Console.ResetColor();
                break;
        }
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
    /// <summary>打印指标汇总。</summary>
    static async Task PrintSummaryAsync(IMetricsCollector metrics)
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

        // 【新增】可疑数量
        if (summary.SuspiciousCount > 0)
        {
            Console.Write("  可疑成功:  ");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write(summary.SuspiciousCount);
            Console.ResetColor();
            Console.WriteLine("  （触发过模板检测，可能未真正实现需求）");
        }

        Console.Write("  成功率:    ");
        Console.ForegroundColor = rateColor;
        Console.WriteLine($"{summary.SuccessRate:P1}");
        Console.ResetColor();

        Console.WriteLine($"  平均修复:  {summary.AvgRetryCount:F2} 次");
        Console.WriteLine($"  平均总耗时: {summary.AvgTotalMs:F0} ms");

        if (summary.AvgPlannerMs > 0 || summary.AvgExecutorMs > 0 || summary.AvgValidatorMs > 0)
        {
            Console.WriteLine($"    - Planner:   {summary.AvgPlannerMs:F0} ms");
            Console.WriteLine($"    - Executor:  {summary.AvgExecutorMs:F0} ms");
            Console.WriteLine($"    - Validator: {summary.AvgValidatorMs:F0} ms");
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