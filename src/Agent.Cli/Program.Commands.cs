// ============================================================
// Program.Commands.cs
// CLI command handlers (10-14 C1: extracted from Program.cs into a partial; pure move,
// zero behavior change). Main dispatch sites call these static handlers; the task
// execution core stays in Program.cs.
// ============================================================
using Agent.Core.Abstractions;
using Agent.Core.Models;
using Agent.Infrastructure;
using Agent.Infrastructure.Metrics;
using Agent.Infrastructure.Orchestration;
using Agent.Infrastructure.Project;
using Agent.Infrastructure.Rendering;
using Agent.Infrastructure.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;

namespace Agent.Cli;

internal partial class Program
{
    // ─────────────────────────────────────────────────────────────────────
    // 【10-14 P3-3】命令处理器（从 Main 分派循环逐字搬移）
    // 搬移铁律（本次拆分的行为等价保证）：
    //   ① 只发生两类改动——块内 `continue;` 改 `return;`、参数列表增补；
    //   ② 业务逻辑、输出文案、调用顺序一律不动；`// ═══` 横幅随块迁移（导航锚点）。
    // 状态传递两档：只读依赖走参数；需回写的（sessionId / lastTaskId / pendingTask）走 ref。
    // 任务执行核心（Main 内 ~172 行，与 loop/超时令牌/归档/自动回滚交织）明确不抽。
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>退出/EOF 判定（空输入或 exit，含 Trim 后的匹配）。</summary>
    static bool IsExitCommand(string? input) =>
        input is null || input.Length == 0 || input.Equals("exit", StringComparison.OrdinalIgnoreCase);

    /// <summary>new 命令：重置会话 ID + 清空会话写目标（不跨会话继承）。</summary>
    static void HandleNewCommand(ref string sessionId, List<(string Path, bool IsFile)> sessionWriteTargets, IRenderer renderer)
    {
        sessionId = Guid.NewGuid().ToString("N");
        sessionWriteTargets.Clear();   // 【10-08】开新会话 → 清空会话写目标（不跨会话继承）
        renderer.System("[系统] 已开启新会话。");
    }

    /// <summary>help 命令：命令表速查。</summary>
    static void HandleHelpCommand(IRenderer renderer)
    {
        renderer.System(
            "命令：use [--write] <路径>（工程模式/可写） | stats [failures|recent] | " +
            "memory [list [N]|stats|forget <topic>|prune|dedup] | rollback [--dry-run] | " +
            "plan <需求> | orch <需求> | dashboard（web UI；交互终端启动即自动起并开浏览器） | " +
            "new（新会话） | exit（退出） | help");
    }

    /// <summary>dashboard 命令：管道/重定向态拒绝启动（保护 E2E 输出逐字节兼容），
    /// 否则调共用启动逻辑（与主循环前自动启动同一实例，消双路径漂移）。
    /// startDashboard 以委托传入——局部函数 StartDashboardAsync 捕获 dashboardUrl 等状态，
    /// 转委托后仍是同一闭包，无需把状态逐个铺成参数。</summary>
    static async Task HandleDashboardCommandAsync(IRenderer renderer, Func<bool, Task<string?>> startDashboard)
    {
        // 无头守卫：管道/重定向模式下直接提示（不启动 Kestrel）——保护 E2E 输出逐字节兼容
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.WriteLine("dashboard 需交互终端（当前为管道/重定向模式）。");
            return;
        }
        var url = await startDashboard(false);   // 委托无具名参数：false = 非静默（已运行则打印提示）
        if (url is not null)
            renderer.Notice($"Dashboard 已启动（仅本机回环 + token 鉴权）：[link]{url}[/]\n  浏览器已自动打开（失败可手动复制上方 URL）；任务实时流 / stats / 历史任务 / 备份 Diff / 回滚 / 任务详情回放 / 任务产物浏览");
    }

    /// <summary>memory 命令：list/stats/forget/prune/dedup 五个子命令分派。</summary>
    static void HandleMemoryCommand(string input, IServiceProvider provider, IRenderer renderer)
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
    }

    // ── 【10-14 P3-3 步骤2】stats / use / 自动识别 / diff / rollback 五个分派块搬移 ──

    /// <summary>stats 命令：summary（默认，含交互态 BarChart）/ recent 10 / failures。</summary>
    static async Task HandleStatsCommandAsync(string input, IServiceProvider provider, AgentOptions options,
        Agent.Infrastructure.Routing.TaskMetricsAggregator featureAggregator, IRenderer renderer)
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
                    // 【10-08 metrics 数据治理】stats 概览为真实统计（E2E 已剔除）；E2E 用例数单列
                    var e2eCount = await metrics.GetE2ECountAsync(CancellationToken.None);
                    spec.StatsPanel("任务指标概览",
                        new List<(string, double)>
                        {
                            ("总任务数", s.TotalCount),
                            ("成功", s.SuccessCount),
                            ("失败", s.FailureCount),
                            ("成功率 %", Math.Round(s.SuccessRate * 100, 1)),
                            ("可疑", s.SuspiciousCount),
                            ("E2E 用例（已单列）", e2eCount),
                        });
                }
            }

            await PrintSummaryAsync(metrics, options.LawUnifiedEpoch, options, featureAggregator, decisionStore, rejectStore);
            await PrintRecentAsync(metrics, 5);
        }
    }

    /// <summary>use 命令（工程感知 P0/P1；10-08 A：裸 use 缺路径命令层拦截）。
    /// pendingTask 需回写（use &lt;路径&gt; &lt;任务&gt; 一步到位：剩余任务交主循环顶部消费）→ ref。</summary>
    static void HandleUseCommand(string trimmedInput, IServiceProvider provider, AgentOptions options,
        List<(string Path, bool IsFile)> sessionWriteTargets, IRenderer renderer, ref string? pendingTask)
    {
        if (Agent.Infrastructure.Project.UseCommandParser.TryParse(
                trimmedInput, out var usePath, out var useWriteFlag))
        {
            // 【10-08 C】use <path> <任务> 一步到位：路径 = Directory.Exists 校验的最长前缀
            // （路径可含空格不误切，如 "D:\My Projects\MyApp 分析一下这个项目"→ 路径 + "分析一下这个项目"）；
            // 剩余任务自动执行（主循环顶部 pendingTask 消费）。
            Agent.Infrastructure.Project.UseCommandParser.TrySplitPathTask(
                usePath, out var realUsePath, out var followupTask);
            // 【10-12 B2 精细化】use 参数非路径形态（TrySplitPathTask 断链）时，按目标类型分流：
            //   ① 存在的【文件】→ 不 use（修单文件轻量通道：显式写目标白名单覆盖写、沙箱显式读例外覆盖读；
            //      避免把整盘挂工程根 → 2000 文件 CompactBlock → 每轮 ~290s prefill）
            //   ② 存在的【目录】→ use 该目录（精确工程根，非盘根）
            //   ③ 仅盘根 → use 盘根（兜底）
            // 原"路径开头"形态行为不变。
            if (!Directory.Exists(realUsePath))
            {
                if (Agent.Infrastructure.Project.ProjectPathDetector.TryResolveAbsolutePath(usePath, out _))
                {
                    renderer.System("[自动提取] use 参数为文件目标（非工程），保持当前模式执行任务（显式写目标白名单生效）。");
                    pendingTask = usePath;
                    return;
                }
                if (Agent.Infrastructure.Project.ProjectPathDetector.TryResolveAbsoluteDirectory(usePath, out var realDir))
                {
                    realUsePath = realDir;
                    followupTask = usePath;
                    renderer.System($"[自动提取] use 参数非路径形态，工程根取 {realDir}，整段作为任务执行。");
                }
                else if (Agent.Infrastructure.Project.UseCommandParser.TryExtractDiskRoot(usePath, out var diskRoot))
                {
                    realUsePath = diskRoot;
                    followupTask = usePath;
                    renderer.System($"[自动提取] use 参数非路径形态，工程根取 {diskRoot}，整段作为任务执行。");
                }
                else
                {
                    Console.WriteLine($"错误：use 路径无效：{usePath}。用法：use <路径> [--write]，例如：use --write X:\\myproject");
                    return;
                }
            }
            ApplyUse(provider, options, realUsePath, useWriteFlag, autoSource: null);
            sessionWriteTargets.Clear();   // 【10-08】切换工程根 → 清空会话写目标（旧目标不跨工程继承）
            if (!string.IsNullOrWhiteSpace(followupTask))
            {
                renderer.System($"[续] 工程已切换，自动执行后续任务：{followupTask}");
                pendingTask = followupTask;
            }
            return;
        }
        // 裸 use / use --write：命令层拦截打印用法，不进任务流（10-08 实证裸 use 漏进任务流跑 8 步）
        if (Agent.Infrastructure.Project.UseCommandParser.IsUseCommand(trimmedInput))
        {
            Console.WriteLine("use <路径> [--write]：设置工程根。裸 use 不执行任务——请带上路径，例如：");
            Console.WriteLine("  use X:\\myproject            只读感知工程（读放开 + 写白名单）");
            Console.WriteLine("  use --write X:\\myproject    可写工程（改动前自动备份，可 rollback）");
            Console.WriteLine("当前工程根：" + (options.ProjectRoot ?? "未设置（沙箱模式）"));
        }
    }

    /// <summary>双根保护·输入路径自动识别（给路径就干活）：输入含"存在的绝对目录路径 + 读/分析语境"
    /// → 自动只读 use。敏感区路径源头不 use；显式 use 优先；写任务不触发。无终止语义（不 continue）。</summary>
    static void AutoDetectUseFromInput(string trimmedInput, IServiceProvider provider, AgentOptions options)
    {
        if (options.ProjectRoot is null &&
            Agent.Infrastructure.Project.ProjectPathDetector.TryGetAutoUsePath(trimmedInput, out var autoProjectPath))
        {
            ApplyUse(provider, options, autoProjectPath, writeFlag: false, autoSource: trimmedInput);
        }
    }

    /// <summary>diff 命令：diff [taskId] 显示该任务工程内备份 vs 当前文件的行级差异（无 taskId → 最近任务）。</summary>
    static void HandleDiffCommand(string input, string workspace, AgentOptions options, string? lastTaskId, IRenderer renderer)
    {
        if (options.ProjectRoot is null)
        {
            Console.WriteLine("diff 需要工程模式（use <路径> 后可用，显示工程改动备份差异）。");
            return;
        }
        var diffTaskId = input.Length > 5 ? input[5..].Trim() : lastTaskId;
        if (string.IsNullOrEmpty(diffTaskId))
        {
            Console.WriteLine("暂无最近任务，无法 diff。");
            return;
        }
        var diffBackups = BackupService.ListBackups(workspace)
            .Where(b => string.Equals(b.TaskId, diffTaskId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (diffBackups.Count == 0)
        {
            Console.WriteLine($"任务 {diffTaskId} 没有备份（未改工程文件，或备份已按 8 任务上限清理）。");
            return;
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
    }

    /// <summary>rollback 命令：rollback（列出备份 + 交互多选恢复）/ rollback all|dry-run / rollback &lt;相对路径&gt;。
    /// lastTaskId 只读（回写发生在任务执行段，非本处理器）。</summary>
    static void HandleRollbackCommand(string input, string workspace, AgentOptions options, string? lastTaskId, IRenderer renderer)
    {
        if (options.ProjectRoot is null)
        {
            Console.WriteLine("当前未设置工程根（先 use <路径>）。");
            return;
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
            return;
        }
        if (parts[1].Equals("all", StringComparison.OrdinalIgnoreCase) ||
            parts[1].Equals("--dry-run", StringComparison.OrdinalIgnoreCase))
        {
            var dryRun = parts[1].Equals("--dry-run", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(lastTaskId))
            {
                Console.WriteLine("本会话尚无任务（无备份作用域）。");
                return;
            }
            if (dryRun)
            {
                var (restoreTargets, removeTargets) = BackupService.DryRunRollbackTask(workspace, options.ProjectRoot, lastTaskId);
                Console.WriteLine($"[dry-run] 将恢复 {restoreTargets.Count} 个文件（最早备份=任务前状态）：");
                foreach (var r in restoreTargets) Console.WriteLine($"  恢复 {r}");
                Console.WriteLine($"[dry-run] 将删除 {removeTargets.Count} 个新建文件：");
                foreach (var r in removeTargets) Console.WriteLine($"  删除 {r}");
                Console.WriteLine("（未执行任何改动；确认后 rollback all 生效）");
                return;
            }
            var n = BackupService.RollbackTask(workspace, options.ProjectRoot, lastTaskId);
            Console.WriteLine($"已回滚全部：{n} 个文件恢复到最早备份（任务前状态）。");
            return;
        }
        var rel = string.Join(' ', parts.Skip(1)).Trim();
        if (string.IsNullOrEmpty(lastTaskId))
        {
            Console.WriteLine("本会话尚无任务（无备份作用域）。");
            return;
        }
        var dest = BackupService.Rollback(workspace, rel, options.ProjectRoot, lastTaskId);
        Console.WriteLine(dest is null
            ? $"没有找到 {rel} 的备份（最近任务 {lastTaskId}）。"
            : $"已回滚：{dest}");
    }
}
