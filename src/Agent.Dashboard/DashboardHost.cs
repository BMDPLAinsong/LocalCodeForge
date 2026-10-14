// ============================================================
// Agent.Dashboard / DashboardHost.cs
// 真 UI 层：本地 Web Dashboard（10-07）。
//   · Kestrel 127.0.0.1 随机端口（仅本机回环，无认证）；随 CLI 进程生命周期。
//   · 旁路消费：TaskEventHub 多播（渲染器直接消费不变，本宿主是第二消费者）。
//   · SSE /events 只覆盖当前任务（事件流实时）；历史任务走 /api/tasks 轮询，前端按 taskId 分组。
//   · /api/diff 直接序列化 FileDiffService.DiffResult（Hunks+Lines），不设 DTO——diff 语义单一来源在引擎。
//   · index.html 为 EmbeddedResource，GET / 直接读流返回（零静态文件根配置）。
//   · 无头守卫在 CLI 侧（重定向输入/输出时不启动本宿主）——保护 E2E 输出逐字节兼容。
// ============================================================
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Agent.Core;
using Agent.Core.Models;
using Agent.Infrastructure.Diff;
using Agent.Infrastructure.Events;
using Agent.Infrastructure.Metrics;
using Agent.Infrastructure.Project;
using Microsoft.Extensions.FileProviders;

namespace Agent.Dashboard;

/// <summary>Dashboard 宿主依赖（由 CLI 组装传入，宿主不碰 Console——可测）。</summary>
public sealed record DashboardOptions(
    string WorkspaceRoot,
    string? ProjectRoot,
    string SymbolDbPath,               // ← 10-07 UI 层 #3：工程依赖图谱（symbol_index.db 路径；【10-14 P2-3】SymbolStore 未注入时的回退建实例路径）
    DateTimeOffset? LawUnifiedEpoch,
    IMetricsCollector Metrics,
    TaskEventHub Hub,
    TaskEventStore EventStore,         // ← 10-07 UI 层 #1：任务事件持久化（详情回放数据源）
    AgentOptions Options,              // ← 10-07 UI 层：调权阈值配置（/api/tuning）
    string MemoryDbPath,               // ← 10-07 UI 层：agent_memory.db 路径（【10-14 P2-3】MemoryStore 未注入时的回退建实例路径）
    CommandHub CommandHub,             // ← 10-07 webUI 交互：web 提交汇入 CLI 主循环命令通道
    string? AuthToken = null,          // ← 10-08 ③ 安全 token：非空时所有端点校验 query token / X-Dashboard-Token（401 拒绝）
    // 【10-14 P2-3】store 实例注入（可选）：非空直接复用（CLI 传 DI 单例——消除"每请求 new + 建表"，
    // 原实现每次 /api/memory、/api/deps 请求都跑一遍 CREATE TABLE + PRAGMA 列迁移）；
    // 为 null 时 StartAsync 按上述路径建一次实例并全程复用（tests 直构路径的默认行为）。
    Agent.Infrastructure.Memory.AgentMemoryStore? MemoryStore = null,
    Agent.Infrastructure.Project.SymbolIndexStore? SymbolStore = null);

/// <summary>POST /api/commands 请求体（最小输入 DTO，非业务模型）。</summary>
public sealed record CommandRequest(string? Input);

/// <summary>POST /api/rollback[/dry-run] 请求体（最小输入 DTO）。</summary>
public sealed record RollbackRequest(string? TaskId);

public static class DashboardHost
{
    // 【10-07 UI 层 #1】序列化单一来源在 TaskEventStore.JsonOpts（存储/SSE/回放共用同一格式，
    // 前端一个渲染函数吃三种数据源——消双路径漂移）
    private static readonly JsonSerializerOptions JsonOpts = TaskEventStore.JsonOpts;

    // 持有当前宿主引用：StartAsync 返回后 Kestrel 依赖 app 对象存活（防 GC 停止服务）
    private static WebApplication? _running;

    // ── 任务产物浏览辅助（10-08 ①）：文本判定 + 预览截断 ──
    private static readonly System.Collections.Generic.HashSet<string> TextExts = new(
        new[] { ".cs", ".py", ".txt", ".md", ".json", ".xml", ".html", ".htm", ".js", ".ts", ".csproj",
                ".sln", ".slnx", ".yml", ".yaml", ".toml", ".ini", ".cfg", ".conf", ".sh", ".ps1",
                ".sql", ".csv", ".tsv", ".log", ".gitignore", ".env", ".editorconfig", ".props",
                ".targets", ".css", ".scss", ".proto", ".bat", ".cmd" },
        StringComparer.OrdinalIgnoreCase);

    private static bool IsTextFile(string name)
    {
        var ext = Path.GetExtension(name);
        if (string.IsNullOrEmpty(ext)) return true;          // 无扩展名默认按文本预览（失败自会截断）
        return TextExts.Contains(ext);
    }

    private const int PreviewMaxChars = 50_000;              // 预览上限 50KB 字符（超大文件不整读）
    private static string PreviewText(string full)
    {
        using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buf = new char[PreviewMaxChars + 1];
        var n = sr.Read(buf, 0, buf.Length);
        var text = new string(buf, 0, n);
        return n > PreviewMaxChars ? text + "\n…（预览截断，下载查看完整内容）" : text;
    }

    /// <summary>启动 Kestrel（127.0.0.1:0 随机端口）。返回 (实际 URL, 停止委托)——Stop() 调用时才停服
    /// （app.StopAsync() 调用即开始执行，若直接返回其 Task 会在 StartAsync 返回后立刻关闭服务器）。</summary>
    public static async Task<(string Url, Func<Task> Stop)> StartAsync(DashboardOptions options, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        // 【10-10 P0 日志治理】内嵌服务显式声明日志级别，不吃脚手架默认：
        // Kestrel/Hosting 是 CLI 进程内"关注点 B"，其默认 Information 启动日志
        // （Microsoft.Hosting.Lifetime "Now listening" 等 4~5 行）会与 CLI 交互渲染
        // 共用 stdout 造成错乱——显式 ClearProviders + 压到 Warning（正常静默，
        // Warn/Error 仍可排障）。CLI 自身日志（关注点 A）不受影响（独立 LoggerFactory）。
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.FormatterName = "simple");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();
        _running = app;
        // 【10-14 P2-3】store 解析一次全程复用：注入优先（CLI 传 DI 单例）；未注入则按路径建实例
        // （tests 直构路径的默认）——原实现每请求 new store + 建表 + PRAGMA 列迁移，是同源债根源。
        var memoryStore = options.MemoryStore
            ?? new Agent.Infrastructure.Memory.AgentMemoryStore(options.MemoryDbPath);
        var symbolStore = options.SymbolStore
            ?? new Agent.Infrastructure.Project.SymbolIndexStore(options.SymbolDbPath);
        MapEndpoints(app, options, memoryStore, symbolStore);
        await app.StartAsync(ct);
        var addr = app.Urls.FirstOrDefault() ?? "http://127.0.0.1:0";
        return (addr, () => app.StopAsync());
    }

    private static void MapEndpoints(WebApplication app, DashboardOptions o,
        Agent.Infrastructure.Memory.AgentMemoryStore memoryStore,
        Agent.Infrastructure.Project.SymbolIndexStore symbolStore)
    {
        // ── 10-08 Monaco vendor 静态文件：/vendor → bin\vendor（csproj Content 构建复制，自包含）。
        // 置于鉴权中间件之前：Monaco loader 内部按相对路径 fetch worker/language 资源，无法带 query token；
        // vendor 是公开编辑器代码（本地回环 + 无用户数据），鉴权保护无意义。
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(Path.Combine(AppContext.BaseDirectory, "vendor")),
            RequestPath = "/vendor"
        });

        // ── ③ 安全 token 加固（10-08）：AuthToken 非空时所有端点鉴权 ──
        // query token / header X-Dashboard-Token 任一匹配即放行；SSE（EventSource 只能带 query）与 fetch 统一兼容。
        // 无 token 配置（tests / 无鉴权场景）→ 跳过中间件，行为与历史完全一致。
        if (!string.IsNullOrEmpty(o.AuthToken))
        {
            var expected = o.AuthToken;
            app.Use(async (ctx, next) =>
            {
                var q = ctx.Request.Query["token"].ToString();
                var h = ctx.Request.Headers["X-Dashboard-Token"].ToString();
                if (!string.Equals(q, expected, StringComparison.Ordinal)
                    && !string.Equals(h, expected, StringComparison.Ordinal))
                {
                    ctx.Response.StatusCode = 401;
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    await ctx.Response.WriteAsync("{\"error\":\"未授权：缺少或错误的 dashboard token（从 CLI 打印的完整 URL 打开）。\"}");
                    return;
                }
                await next();
            });
        }

        // ── 单页 ──
        app.MapGet("/", () => Results.Text(ReadEmbeddedPage(), "text/html; charset=utf-8"));

        // ── 统计面板（成功率/耗时/时代隔离/失败类别）──
        app.MapGet("/api/stats", async (CancellationToken ct) =>
        {
            var summary = await o.Metrics.GetSummaryAsync(null, ct);
            var failures = await o.Metrics.GetFailureBreakdownAsync(ct);
            EpochBreakdown? epoch = null;
            if (o.LawUnifiedEpoch is { } e)
            {
                var (cur, legacy) = await o.Metrics.GetEpochBreakdownAsync("agent_pro", e, ct);
                epoch = new EpochBreakdown(cur, legacy);
            }
            return Results.Json(new { summary, failures, epoch }, JsonOpts);
        });

        // ── 历史任务（轮询；SSE 只推当前任务）──
        app.MapGet("/api/tasks", async (int take, CancellationToken ct) =>
        {
            take = take is >= 1 and <= 200 ? take : 50;
            var tasks = await o.Metrics.GetRecentAsync(take, ct);
            return Results.Json(tasks, JsonOpts);
        });

        // ── 备份列表（Diff 数据源定位）──
        app.MapGet("/api/backups", () => Results.Json(BackupService.ListBackups(o.WorkspaceRoot), JsonOpts));

        // ── Web 端回滚（10-10 补齐"看-改-回"闭环）：dry-run 预览（不执行）+ 执行恢复 ──
        // 复用 CLI rollback 同一套 BackupService（taskId 隔离 / 最早备份 / 删新建标记），无并行实现。
        // 有风险操作：前端两步确认（预览 → 确认）；鉴权走全局 AuthToken 中间件。
        app.MapPost("/api/rollback/dry-run", (RollbackRequest? req) =>
        {
            if (o.ProjectRoot is null)
                return Results.BadRequest(new { error = "当前未设置工程根（CLI 执行 use <路径> 后才有回滚作用域）。" });
            var taskId = req?.TaskId?.Trim();
            if (string.IsNullOrWhiteSpace(taskId))
                return Results.BadRequest(new { error = "taskId 为空。" });
            var (restoreTargets, removeTargets) =
                BackupService.DryRunRollbackTask(o.WorkspaceRoot, o.ProjectRoot, taskId);
            return Results.Json(new { taskId, restoreTargets, removeTargets }, JsonOpts);
        });

        app.MapPost("/api/rollback", (RollbackRequest? req) =>
        {
            if (o.ProjectRoot is null)
                return Results.BadRequest(new { error = "当前未设置工程根（CLI 执行 use <路径> 后才有回滚作用域）。" });
            var taskId = req?.TaskId?.Trim();
            if (string.IsNullOrWhiteSpace(taskId))
                return Results.BadRequest(new { error = "taskId 为空。" });
            var (restored, removedNew) =
                BackupService.RollbackTaskFull(o.WorkspaceRoot, o.ProjectRoot, taskId);
            return Results.Json(new { taskId, restored, removedNew }, JsonOpts);
        });

        // ── 任务产物浏览（10-08 ①）：run 工作目录文件浏览 + 文本预览 + 下载 ──
        // 限定在 WorkspaceRoot 内（PathBoundary 逐段判定，杜绝 .. 跳脱/前缀误判）。
        // path 为空 → 根 children；path 为目录 → children；path 为文本文件 → {file:true, content 前 50KB}；
        // 二进制 → {file:true, text:false}；raw=true → 文件下载（octet-stream + 下载名）。
        // 越界 → 400；不存在 → 404。数据源 = 磁盘真实文件（任务产物），非内存镜像——单一来源。
        app.MapGet("/api/files", (string? path, bool? raw) =>
        {
            var root = PathBoundary.Normalize(o.WorkspaceRoot);
            var full = string.IsNullOrWhiteSpace(path)
                ? root
                : PathBoundary.Normalize(Path.Combine(root, path.TrimStart('/', '\\')));
            if (!PathBoundary.IsInside(root, full))
                return Results.Json(new { error = "路径越界。" }, JsonOpts, statusCode: 400);
            var rel = full == root ? "" : (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(root.Length).TrimStart('\\') : Path.GetFileName(full));
            if (Directory.Exists(full))
            {
                var children = Directory.EnumerateFileSystemEntries(full)
                    .Select(p => new
                    {
                        name = Path.GetFileName(p),
                        isDir = Directory.Exists(p),
                        size = File.Exists(p) ? new FileInfo(p).Length : (long?)null,
                        modifiedAt = File.Exists(p)
                            ? (DateTimeOffset?)new FileInfo(p).LastWriteTimeUtc : null,
                    })
                    .OrderByDescending(c => c.isDir)
                    .ThenBy(c => c.name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return Results.Json(new { path = rel, isDir = true, children }, JsonOpts);
            }
            if (File.Exists(full))
            {
                var fi = new FileInfo(full);
                if (raw == true)
                    return Results.File(fi.FullName, "application/octet-stream", fi.Name);
                var isText = IsTextFile(fi.Name);
                return Results.Json(new
                {
                    path = rel, isDir = false, size = fi.Length, text = isText,
                    content = isText ? PreviewText(fi.FullName) : null,
                }, JsonOpts);
            }
            return Results.Json(new { error = "路径不存在。" }, JsonOpts, statusCode: 404);
        });

        // ── 任务详情回放：完整事件序列（10-07 UI 层 #1/#2；10-10 P0 补 type）──
        // P0 修复：AgentEvent 为 C# record，序列化无 type 判别字段——SSE/回放双出口统一补
        // wire type 短名（源头隔离，前端单一渲染函数直接消费，消双路径漂移）。
        // 返回平铺事件数组 [{type:"thinking"|"toolCall"|…, ...payload}]；无事件 → 空数组。
        app.MapGet("/api/tasks/{taskId}/events", (string taskId) =>
        {
            var list = o.EventStore.GetEventsAsync(taskId);
            var arr = new System.Text.Json.Nodes.JsonArray();
            foreach (var s in list)
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(s.Payload);
                if (node is System.Text.Json.Nodes.JsonObject jo)
                {
                    jo.Insert(0, "type", WireType(s.Type));
                    arr.Add(jo);
                }
            }
            return Results.Json(arr, JsonOpts);
        });

        // ── Diff：备份 vs 当前（直接序列化引擎模型，无 DTO）──
        // 10-08 Monaco DiffEditor：额外附 original/modified 全文（仅非 tooLarge；超大文件回退红绿行文本视图）。
        app.MapGet("/api/diff", (string taskId, string rel, CancellationToken ct) =>
        {
            if (o.ProjectRoot is null)
                return Results.BadRequest(new { error = "未设置工程根，diff 不可用。" });
            (string TaskId, string Stamp, string Rel, string Full)? backup = null;
            foreach (var b in BackupService.ListBackups(o.WorkspaceRoot))
            {
                if (string.Equals(b.TaskId, taskId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(b.Rel, rel, StringComparison.OrdinalIgnoreCase))
                {
                    backup = b;
                    break;
                }
            }
            if (backup is null)
                return Results.NotFound(new { error = $"任务 {taskId} 无文件 {rel} 的备份。" });
            var current = Path.Combine(o.ProjectRoot, backup.Value.Rel);
            var result = FileDiffService.Compute(backup.Value.Full, current);
            string? originalText = null, modifiedText = null;
            if (!result.TooLarge)
            {
                // backup.Value.Full 是备份文件路径（ListBackups 返回 Full=完整路径）——须 ReadAllText 取全文
                originalText = File.ReadAllText(backup.Value.Full);
                modifiedText = File.Exists(current) ? File.ReadAllText(current) : "";
            }
            return Results.Json(new { original = originalText, modified = modifiedText, diff = result }, JsonOpts);
        });

        // ── 路由调权状态（10-07）：三处调权当前判定——与终端 stats"路由调权状态"小节同语义 ──
        // 【10-14 P2-1 单源】样本查询 + 三判定改调 RoutingTuningReporter（与 CLI stats / ModelRouter 同源）——
        // 本端点曾是 10-11 P2 层级错配的漏改出口（web 端 P2 永远 0 样本，任务8-A1）；单源后不可再分叉。
        // 时代过滤在 reporter 内（LawUnifiedEpoch 非空 → 只统计 30B 时代样本）；阈值来自 AgentOptions.Routing*。
        app.MapGet("/api/tuning", async (CancellationToken ct) =>
        {
            var t = await Agent.Infrastructure.Routing.RoutingTuningReporter.BuildAsync(o.Metrics, o.Options, ct);
            var opt = o.Options;
            var rate = Agent.Infrastructure.Routing.RoutingTuningReporter.RateOf;

            return Results.Json(new
            {
                epoch = o.LawUnifiedEpoch,
                thresholds = new
                {
                    minSamples = opt.RoutingTuningMinSamples,
                    downgradeMinSuccessRate = opt.RoutingDowngradeMinSuccessRate,
                    readUpgradeBadRate = opt.RoutingReadUpgradeBadRate,
                    readUpgradeMinSuccessRate = opt.RoutingReadUpgradeMinSuccessRate,
                    dropAgentProMinSamples = opt.RoutingDropAgentProMinSamples,
                    dropAgentProMinSuccessRate = opt.RoutingDropAgentProMinSuccessRate,
                },
                writeDowngrade = new { sampleCount = t.WriteDowngrade.SampleCount, successRate = t.WriteDowngrade.SuccessRate, threshold = opt.RoutingDowngradeMinSuccessRate, triggered = t.WriteDowngrade.Triggered, reason = t.WriteDowngrade.Reason },
                readUpgrade = new { small = new { sampleCount = t.Small.TotalCount, successRate = rate(t.Small) }, pro = new { sampleCount = t.Pro.TotalCount, successRate = rate(t.Pro) }, badRateThreshold = opt.RoutingReadUpgradeBadRate, minSuccessRate = opt.RoutingReadUpgradeMinSuccessRate, triggered = t.ReadUpgrade.Triggered, reason = t.ReadUpgrade.Reason },
                complexDrop = new { sampleCount = t.ComplexDrop.SampleCount, successRate = t.ComplexDrop.SuccessRate, threshold = opt.RoutingDropAgentProMinSuccessRate, triggered = t.ComplexDrop.Triggered, reason = t.ComplexDrop.Reason },
            }, JsonOpts);
        });

        // ── 记忆视图（10-07）：列表 + 统计（与 CLI memory list/stats 同数据源 agent_memory.db）──
        app.MapGet("/api/memory", (int take) =>
        {
            take = take is >= 1 and <= 100 ? take : 50;
            var store = memoryStore;
            var rows = store.List(take);
            var stats = store.GetStats();
            return Results.Json(new
            {
                stats,
                rows = rows.Select(m => new
                {
                    m.Id, m.Kind, m.Topic, m.HitCount, m.Count, m.CreatedAt, m.LastHitAt, m.Feature
                }),
            }, JsonOpts);
        });

        // ── 工程依赖图谱（10-07 UI 层 #3）：全工程边概览 / 单文件直接+反向依赖 ──
        // 无 path → { files, edges, topReverse }（改动风险 Top5）；path=相对文件 → { direct, reverse }。
        // 与 find_symbol/analyze_deps 同一 symbol_index.db + DependencyAnalyzer 引擎（单一来源，无 DTO）。
        app.MapGet("/api/deps", async (string? path, CancellationToken ct) =>
        {
            if (o.ProjectRoot is null)
                return Results.BadRequest(new { error = "未设置工程根（CLI 执行 use <路径> 后刷新），依赖图谱不可用。" });
            var store = symbolStore;
            ct.ThrowIfCancellationRequested();

            DepGraph graph;
            if (!TryGetCachedGraph(o.ProjectRoot, out graph!))
            {
                try
                {
                    if (!store.HasIndex(o.ProjectRoot))
                    {
                        if (store.BuildAndSave(o.ProjectRoot) == 0)
                            return Results.Ok(new { files = Array.Empty<string>(), edges = Array.Empty<object>(), topReverse = Array.Empty<object>(), direct = Array.Empty<string>(), reverse = Array.Empty<string>() });
                    }
                    else
                    {
                        store.BuildAndSaveIncremental(o.ProjectRoot);
                    }
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = $"符号索引失败：{ex.Message}" });
                }
                ct.ThrowIfCancellationRequested();   // 索引/边计算为同步 CPU+IO，节点间检查取消
                var symbols = store.GetAll(o.ProjectRoot);
                var edges = DependencyAnalyzer.Build(o.ProjectRoot, symbols);
                var files = edges.Select(e => e.File).Concat(edges.Select(e => e.DependsOnFile))
                    .Distinct().OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                graph = new DepGraph(files, edges);
                SetCachedGraph(o.ProjectRoot, graph);
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                var topReverse = graph.Files
                    .Select(f => (File: f, Count: DependencyAnalyzer.GetReverseDependencies(f, graph.Edges).Count))
                    .Where(x => x.Count > 0)
                    .OrderByDescending(x => x.Count).ThenBy(x => x.File, StringComparer.OrdinalIgnoreCase)
                    .Take(5)
                    .Select(x => new { file = x.File, count = x.Count })
                    .ToList();
                return Results.Json(new { files = graph.Files, edges = graph.Edges, topReverse }, JsonOpts);
            }
            var norm = path.Replace('\\', '/');
            var direct = DependencyAnalyzer.GetDirectDependencies(norm, graph.Edges);
            var reverse = DependencyAnalyzer.GetReverseDependencies(norm, graph.Edges);
            return Results.Json(new { direct, reverse }, JsonOpts);
        });

        // ── 命令提交（10-07 webUI 交互）：web 命令汇入 CLI 主循环命令通道 ──
        // 终端输入与 web 提交统一排队（CommandHub 有界 100 DropOldest）——一次一任务串行语义不变。
        // 空输入/超限 → 400；通道满 → 429；成功 → 202 {queued:true, seq, pending}。
        app.MapPost("/api/commands", (CommandRequest? req) =>
        {
            var input = req?.Input?.TrimStart('\uFEFF')?.Trim();
            if (string.IsNullOrWhiteSpace(input))
                return Results.BadRequest(new { error = "input 为空。" });
            if (input.Length > 2000)
                return Results.BadRequest(new { error = "input 超过 2000 字符上限。" });
            if (o.CommandHub.Count >= 100)
                return Results.Json(new { error = "命令通道已满（排队过多），稍后重试。" }, JsonOpts, statusCode: 429);
            var seq = o.CommandHub.TrySubmit(input, "web");
            return Results.Accepted("/api/commands", new { queued = true, seq, pending = o.CommandHub.Count });
        });

        // ── 命令队列状态（10-07 P0）：排队中的命令列表（seq/输入/提交时间）——web 用户可看到自己的位置 ──
        app.MapGet("/api/queue", () =>
        {
            var items = o.CommandHub.Snapshot();
            return Results.Json(new
            {
                pending = items.Count,
                items = items.Select(q => new { q.Seq, q.Input, q.SubmittedAt }),
            }, JsonOpts);
        });

        // ── 取消当前任务（10-07 P0）：触发 CLI 主循环当前任务的取消令牌（与终端 q 键同一机制）──
        app.MapPost("/api/cancel", () =>
            o.CommandHub.CancelCurrentTask()
                ? Results.Ok(new { cancelled = true })
                : Results.Conflict(new { error = "当前无运行中任务（无任务或已取消）。" }));

        // ── 当前任务归属（10-08 P1）：SSE 混排归属问题——前端显示"当前任务 = 哪条提交（seq）"──
        app.MapGet("/api/current-task", () =>
        {
            var (seq, input) = o.CommandHub.CurrentTask;
            return seq == 0 || input is null
                ? Results.Json(new { running = false }, JsonOpts)
                : Results.Json(new { running = true, seq, input }, JsonOpts);
        });

        // ── SSE：当前任务实时事件流（每订阅者 BoundedChannel 1000 DropOldest）──
        app.MapGet("/events", async (HttpContext ctx, CancellationToken ct) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection = "keep-alive";
            var (reader, handle) = o.Hub.Subscribe();
            using (handle)
            {
                await foreach (var evt in reader.ReadAllAsync(ct))
                {
                    // 【10-14 P2-2 同源】与回放/存储完全相同的序列化路径：Serialize(evt, GetType(), JsonOpts)
                    // + 首位补 wire type。原实现走 ToWire 手工匿名对象（34 行）——record 新增字段时
                    // 回放能看到、SSE 看不到，同一任务两视图悄悄分叉（机制不对称 = 漂移温床）。
                    var node = JsonSerializer.SerializeToNode(evt, evt.GetType(), JsonOpts)
                        as System.Text.Json.Nodes.JsonObject;
                    if (node is null) continue;
                    node.Insert(0, "type", WireType(evt.GetType().Name));
                    await ctx.Response.WriteAsync($"data: {node.ToJsonString(JsonOpts)}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
        });
    }

    private static string ReadEmbeddedPage()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("Agent.Dashboard.Pages.index.html")
            ?? throw new InvalidOperationException("嵌入资源 Agent.Dashboard.Pages.index.html 缺失");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // ── P0 事件 wire type（10-10）：AgentEvent 是 record，JSON 无 type 判别字段；
    //    SSE 与回放双出口用同一映射补短名（前端渲染单一入口消费）。
    // 【10-14 P2-2】映射来源改为 Agent.Core 的 [WireName] 特性反射构建（原 nameof 手工表 8 项：
    // 新增/重命名事件需手工同步两处，漏改则两视图不一致）。.NET 类名 → wire 短名；
    // 未标记/未知类名兜底原样（不吞）。原 ToWire（34 行手工匿名对象）已删——SSE 与回放
    // 现共用"序列化 + 补 type"同一条路径，record 加字段两视图同步可见。
    private static readonly Dictionary<string, string> WireTypeMap = BuildWireTypeMap();

    private static Dictionary<string, string> BuildWireTypeMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in typeof(Agent.Core.Models.AgentEvent).GetNestedTypes())
        {
            var attr = t.GetCustomAttributes(typeof(Agent.Core.Models.WireNameAttribute), inherit: false)
                .OfType<Agent.Core.Models.WireNameAttribute>()
                .FirstOrDefault();
            map[t.Name] = attr?.Name ?? t.Name;
        }
        return map;
    }

    private static string WireType(string typeName) =>
        WireTypeMap.TryGetValue(typeName, out var w) ? w : typeName;

    // ── 【P2-4】依赖图缓存（30s TTL）：原 /api/deps 每次请求都"增量索引检查 + 全量依赖边重算"，
    //    前端刷新/轮询即重算（51 文件毫秒级，工程变大后线性恶化）。TTL 内复用同一份图；
    //    键为工程根（换 use 目标自动切换），条目上限 8（CLI 会话内项目数有限，超限整体清空）。
    private sealed record DepGraph(List<string> Files, List<DependencyEdge> Edges);

    private static readonly TimeSpan DepGraphTtl = TimeSpan.FromSeconds(30);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, DepGraph Graph)> DepGraphCache = new();

    private static bool TryGetCachedGraph(string projectRoot, out DepGraph graph)
    {
        graph = null!;
        if (!DepGraphCache.TryGetValue(projectRoot, out var hit)) return false;
        if (DateTimeOffset.UtcNow - hit.At > DepGraphTtl)
        {
            DepGraphCache.TryRemove(projectRoot, out _);
            return false;
        }
        graph = hit.Graph;
        return true;
    }

    private static void SetCachedGraph(string projectRoot, DepGraph graph)
    {
        if (DepGraphCache.Count > 8) DepGraphCache.Clear();
        DepGraphCache[projectRoot] = (DateTimeOffset.UtcNow, graph);
    }

    private sealed record EpochBreakdown(
        Agent.Infrastructure.Routing.ServiceStats Current,
        Agent.Infrastructure.Routing.ServiceStats Legacy);
}
