// 引入 IO/集合
using System.Text;

// 命名空间
namespace Agent.Infrastructure.Project;

/// <summary>
/// 工程文件备份服务（工程感知 P1 + P-B 任务隔离）：
/// 写工具在工程根内修改已存在文件前自动备份（runs/&lt;run&gt;/.backup/&lt;taskId&gt;/&lt;stamp&gt;/相对路径），
/// CLI rollback 命令可恢复备份。确定性文件操作，无模型调用。
///
/// 【P-B】taskId 隔离：备份与新文件标记全部按任务归属（.backup/&lt;taskId&gt;/），
/// 回滚只恢复/清理本任务的文件——跨任务互不干扰（A 成功改动不会被 B 失败回滚误伤）。
/// 【P-A】回滚取【最早】备份：任务多次写入时最近备份=任务自身中间状态（方向性 bug），
/// 最早备份=任务第一次写入前=任务前干净状态。
/// 【P-C】stamp 毫秒粒度（HHmmssfff）：防同秒多次写入覆盖备份。
/// </summary>
public static class BackupService
{
    /// <summary>
    /// 备份已存在的文件：.backup/&lt;taskId&gt;/&lt;stamp&gt;/&lt;rel&gt;。返回备份路径；文件不存在或失败返回 null。
    /// </summary>
    public static string? Backup(string workspaceRoot, string fullPath, string projectRoot, string taskId)
    {
        if (!File.Exists(fullPath)) return null;
        try
        {
            var rel = Path.GetRelativePath(projectRoot, fullPath);
            var runRoot = workspaceRoot; // runs/<run>（任务目录→其父）
            var stamp = DateTime.Now.ToString("HHmmssfff"); // 【P-C】毫秒防同秒覆盖
            var dest = Path.Combine(runRoot, ".backup", taskId, stamp, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(fullPath, dest, overwrite: true);
            return dest;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>列出全部备份（新结构 &lt;taskId&gt;/&lt;stamp&gt;/&lt;rel&gt;，taskId 倒序展示）。返回 (TaskId, Stamp, Rel, Full)。</summary>
    public static List<(string TaskId, string Stamp, string Rel, string Full)> ListBackups(string workspaceRoot)
    {
        var result = new List<(string, string, string, string)>();
        var runRoot = workspaceRoot;
        var dir = Path.Combine(runRoot, ".backup");
        if (!Directory.Exists(dir)) return result;

        foreach (var taskDir in Directory.GetDirectories(dir)
            .Where(d => !Path.GetFileName(d).Equals(".new", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d))
        {
            var taskId = Path.GetFileName(taskDir);
            foreach (var stampDir in Directory.GetDirectories(taskDir).OrderByDescending(d => d))
            {
                foreach (var f in Directory.GetFiles(stampDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(stampDir, f);
                    result.Add((taskId, Path.GetFileName(stampDir), rel, f));
                }
            }
        }
        return result;
    }

    /// <summary>
    /// 任务级回滚：恢复本任务每文件【最早】备份（= 任务第一次写入前 = 任务前干净状态）。
    /// 【P-A】取最早而非最近：任务多次写入时，最近备份=任务自身中间坏状态（复测实证：
    /// OrderService.cs 回滚后仍是含重复 Order 类的内容）——回滚必须恢复到任务前。
    /// 返回恢复的文件数。
    /// </summary>
    public static int RollbackTask(string workspaceRoot, string projectRoot, string taskId)
    {
        var runRoot = workspaceRoot;
        var dir = Path.Combine(runRoot, ".backup", taskId);
        if (!Directory.Exists(dir)) return 0;

        // 每个相对路径取最早 stamp 的备份（升序第一个）→ 恢复（排除 .new 标记目录）
        var earliestByRel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stampDir in Directory.GetDirectories(dir)
            .Where(d => !Path.GetFileName(d).Equals(".new", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d))
        {
            foreach (var f in Directory.GetFiles(stampDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(stampDir, f);
                if (!earliestByRel.ContainsKey(rel))
                    earliestByRel[rel] = f;
            }
        }

        var restored = 0;
        foreach (var (rel, backup) in earliestByRel)
        {
            var dest = Path.Combine(projectRoot, rel);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(backup, dest, overwrite: true);
                restored++;
            }
            catch { }
        }
        return restored;
    }

    /// <summary>
    /// 任务级完整回滚：恢复本任务最早备份（已存在文件任务前状态）
    /// + 删除本任务新建的工程文件（.backup/&lt;taskId&gt;/.new 标记）。返回 (恢复数, 清理新文件数)。
    /// </summary>
    public static (int Restored, int RemovedNew) RollbackTaskFull(string workspaceRoot, string projectRoot, string taskId)
    {
        var restored = RollbackTask(workspaceRoot, projectRoot, taskId);
        var removed = 0;
        var runRoot = workspaceRoot;
        var newDir = Path.Combine(runRoot, ".backup", taskId, ".new");
        if (!Directory.Exists(newDir)) return (restored, 0);

        foreach (var marker in Directory.GetFiles(newDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(newDir, marker);
            var target = Path.Combine(projectRoot, rel);
            try
            {
                // 仅删除"本任务新建"的文件：标记存在 + 目标存在
                if (File.Exists(target))
                {
                    File.Delete(target);
                    removed++;
                }
                File.Delete(marker);
            }
            catch { }
        }
        // 清理空的 .new 目录
        try { if (Directory.Exists(newDir) && !Directory.EnumerateFileSystemEntries(newDir).Any()) Directory.Delete(newDir); } catch { }
        return (restored, removed);
    }

    /// <summary>
    /// 【P-B】成功收尾清理：任务成功后其改动是预期保留的，备份与 .new 标记失去意义。
    /// 删除该任务整个 .backup/&lt;taskId&gt; 目录——跨任务彻底隔离（任务 B 失败回滚只碰 B 自己的目录）。
    /// </summary>
    public static void ClearNewMarkers(string workspaceRoot, string taskId)
    {
        try
        {
            var runRoot = workspaceRoot;
            var taskDir = Path.Combine(runRoot, ".backup", taskId);
            if (Directory.Exists(taskDir))
                Directory.Delete(taskDir, true);
        }
        catch { }
    }

    /// <summary>
    /// 【任务4 备份生命周期】按 taskId 目录名降序（最近优先）保留最近 keepLastN 个任务的备份目录
    /// （含 .new 标记），删除更早的——防 .backup 无限增长；手动 rollback 仍可覆盖最近 N 个任务。
    /// 返回清理的目录数。
    /// </summary>
    public static int PruneBackups(string workspaceRoot, int keepLastN = 8)
    {
        var removed = 0;
        try
        {
            var runRoot = workspaceRoot;
            var dir = Path.Combine(runRoot, ".backup");
            if (!Directory.Exists(dir)) return 0;

            var taskDirs = Directory.GetDirectories(dir)
                .Where(d => !Path.GetFileName(d).Equals(".new", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var stale in taskDirs.Skip(keepLastN))
            {
                try { Directory.Delete(stale, true); removed++; } catch { }
            }
        }
        catch { }
        return removed;
    }

    /// <summary>
    /// 标记工程可写模式下新建的工程文件：.backup/&lt;taskId&gt;/.new/&lt;rel&gt;（空文件）。
    /// 任务失败自动回滚时删除这些新文件（备份仅覆盖已存在文件，新建文件需单独追踪）。
    /// </summary>
    public static void MarkNewFile(string workspaceRoot, string fullPath, string projectRoot, string taskId)
    {
        try
        {
            var rel = Path.GetRelativePath(projectRoot, fullPath);
            var runRoot = workspaceRoot;
            var dest = Path.Combine(runRoot, ".backup", taskId, ".new", rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllText(dest, "");
        }
        catch { }
    }

    /// <summary>
    /// 【dry-run】预览任务回滚：返回将恢复的文件相对路径列表 + 将删除的新文件相对路径列表（不执行）。
    /// 供 rollback --dry-run 在回滚前向用户展示影响面。
    /// </summary>
    public static (List<string> RestoreTargets, List<string> RemoveTargets) DryRunRollbackTask(
        string workspaceRoot, string projectRoot, string taskId)
    {
        var restoreTargets = new List<string>();
        var runRoot = workspaceRoot;
        var dir = Path.Combine(runRoot, ".backup", taskId);
        if (Directory.Exists(dir))
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var stampDir in Directory.GetDirectories(dir)
                .Where(d => !Path.GetFileName(d).Equals(".new", StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d))
            {
                foreach (var f in Directory.GetFiles(stampDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(stampDir, f);
                    if (seen.Add(rel))
                        restoreTargets.Add(rel);
                }
            }
        }

        var removeTargets = new List<string>();
        var newDir = Path.Combine(runRoot, ".backup", taskId, ".new");
        if (Directory.Exists(newDir))
        {
            foreach (var marker in Directory.GetFiles(newDir, "*", SearchOption.AllDirectories))
                removeTargets.Add(Path.GetRelativePath(newDir, marker));
        }
        return (restoreTargets, removeTargets);
    }

    /// <summary>
    /// 单文件手动回滚（rollback &lt;rel&gt;）：在指定任务作用域内恢复该 rel 的【最近一次】备份
    /// （撤销最近修改——手动语义，与任务级自动回滚"恢复到任务前"不同）。
    /// 返回恢复路径；无备份返回 null。
    /// </summary>
    public static string? Rollback(string workspaceRoot, string rel, string projectRoot, string taskId)
    {
        var runRoot = workspaceRoot;
        var dir = Path.Combine(runRoot, ".backup", taskId);
        if (!Directory.Exists(dir)) return null;

        foreach (var stampDir in Directory.GetDirectories(dir)
            .Where(d => !Path.GetFileName(d).Equals(".new", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d))
        {
            var f = Path.Combine(stampDir, rel);
            if (File.Exists(f))
            {
                var dest = Path.Combine(projectRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(f, dest, overwrite: true);
                return dest;
            }
        }
        return null;
    }

    // 【10-14 P4-22 删除 task_ 命名启发式】此处原有 GetRunRoot(workspaceRoot)：
//   目录名以 "task_" 开头 → .backup 落**父目录**，否则落自身；配套文档曾称"写工具传入任务目录
//   （runs/<run>/task_xxx），CLI rollback 传入 run 目录，两者差一层"——**该文档已失效**：
//   写工具现收 DI 绑定的 run 根（DependencyInjection 的 workspaceRoot = AddAgent 传入的 run 目录），
//   每任务隔离由 `.backup/<taskId>/` 目录结构承担，不再靠目录名。
//   **实测全库 15 处调用点无一命中该分支**：CLI 7 处传 `workspace`、Dashboard 3 处传
//   `o.WorkspaceRoot`、Write/Edit 工具 5 处传 `_workspaceRoot`——三者是同一个 run 目录
//   （yyyyMMdd-HHmmss-xxxx），且无任何代码创建 task_ 前缀目录。
//   故删除：保留它等于保留一个"改目录名会静默改变备份落点"的陷阱，且其哨兵断言（区段 2450）
//   锁定的是不可达分支。现行契约：**备份落点恒等于传入的工作区根**。
//   （审查任务5-A10 曾建议"改显式注入 runRoot 参数"：需改 15 处调用点签名，误接风险高于收益——
//     既然分支不可达，删除比注入化更彻底且对所有现存路径零行为变化。）
}
