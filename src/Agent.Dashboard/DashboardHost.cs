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
using Agent.Core.Models;
using Agent.Infrastructure.Diff;
using Agent.Infrastructure.Events;
using Agent.Infrastructure.Metrics;
using Agent.Infrastructure.Project;

namespace Agent.Dashboard;

/// <summary>Dashboard 宿主依赖（由 CLI 组装传入，宿主不碰 Console——可测）。</summary>
public sealed record DashboardOptions(
    string WorkspaceRoot,
    string? ProjectRoot,
    string SymbolDbPath,               // ← 10-07 UI 层 #3：工程依赖图谱（symbol_index.db 路径，与 find_symbol/analyze_deps 同源）
    DateTimeOffset? LawUnifiedEpoch,
    IMetricsCollector Metrics,
    TaskEventHub Hub,
    TaskEventStore EventStore);   // ← 10-07 UI 层 #1：任务事件持久化（详情回放数据源）

public static class DashboardHost
{
    // 【10-07 UI 层 #1】序列化单一来源在 TaskEventStore.JsonOpts（存储/SSE/回放共用同一格式，
    // 前端一个渲染函数吃三种数据源——消双路径漂移）
    private static readonly JsonSerializerOptions JsonOpts = TaskEventStore.JsonOpts;

    // 持有当前宿主引用：StartAsync 返回后 Kestrel 依赖 app 对象存活（防 GC 停止服务）
    private static WebApplication? _running;

    /// <summary>启动 Kestrel（127.0.0.1:0 随机端口）。返回 (实际 URL, 停止委托)——Stop() 调用时才停服
    /// （app.StopAsync() 调用即开始执行，若直接返回其 Task 会在 StartAsync 返回后立刻关闭服务器）。</summary>
    public static async Task<(string Url, Func<Task> Stop)> StartAsync(DashboardOptions options, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        _running = app;
        MapEndpoints(app, options);
        await app.StartAsync(ct);
        var addr = app.Urls.FirstOrDefault() ?? "http://127.0.0.1:0";
        return (addr, () => app.StopAsync());
    }

    private static void MapEndpoints(WebApplication app, DashboardOptions o)
    {
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

        // ── 任务详情回放：完整事件序列（10-07 UI 层 #1/#2）──
        // 返回 StoredEvent 数组（payload 为 JSON 字符串，与 SSE 推送格式一致）——
        // 前端 JSON.parse(payload) 后直接复用 renderEvent 渲染（单一渲染函数）。
        // 无事件 → 空数组（200），前端显示"无事件记录"。
        app.MapGet("/api/tasks/{taskId}/events", (string taskId) =>
            Results.Json(o.EventStore.GetEventsAsync(taskId), JsonOpts));

        // ── Diff：备份 vs 当前（直接序列化引擎模型，无 DTO）──
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
            return Results.Json(result, JsonOpts);
        });

        // ── 工程依赖图谱（10-07 UI 层 #3）：全工程边概览 / 单文件直接+反向依赖 ──
        // 无 path → { files, edges, topReverse }（改动风险 Top5）；path=相对文件 → { direct, reverse }。
        // 与 find_symbol/analyze_deps 同一 symbol_index.db + DependencyAnalyzer 引擎（单一来源，无 DTO）。
        app.MapGet("/api/deps", (string? path) =>
        {
            if (o.ProjectRoot is null)
                return Results.BadRequest(new { error = "未设置工程根（CLI 执行 use <路径> 后刷新），依赖图谱不可用。" });
            var store = new SymbolIndexStore(o.SymbolDbPath);
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
            var symbols = store.GetAll(o.ProjectRoot);
            var edges = DependencyAnalyzer.Build(o.ProjectRoot, symbols);
            var files = edges.Select(e => e.File).Concat(edges.Select(e => e.DependsOnFile)).Distinct().OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            if (string.IsNullOrWhiteSpace(path))
            {
                var topReverse = files
                    .Select(f => (File: f, Count: DependencyAnalyzer.GetReverseDependencies(f, edges).Count))
                    .Where(x => x.Count > 0)
                    .OrderByDescending(x => x.Count).ThenBy(x => x.File, StringComparer.OrdinalIgnoreCase)
                    .Take(5)
                    .Select(x => new { file = x.File, count = x.Count })
                    .ToList();
                return Results.Json(new { files, edges, topReverse }, JsonOpts);
            }
            var norm = path.Replace('\\', '/');
            var direct = DependencyAnalyzer.GetDirectDependencies(norm, edges);
            var reverse = DependencyAnalyzer.GetReverseDependencies(norm, edges);
            return Results.Json(new { direct, reverse }, JsonOpts);
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
                    var json = JsonSerializer.Serialize(evt, evt.GetType(), JsonOpts);
                    await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
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

    private sealed record EpochBreakdown(
        Agent.Infrastructure.Routing.ServiceStats Current,
        Agent.Infrastructure.Routing.ServiceStats Legacy);
}
