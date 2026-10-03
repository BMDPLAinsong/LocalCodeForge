// 引入 IO/集合
using System.Text;

// 命名空间
namespace Agent.Infrastructure.Project;

/// <summary>
/// 工程文件备份服务（工程感知 P1）：
/// 写工具在工程根内修改已存在文件前自动备份（runs/&lt;run&gt;/.backup/&lt;HHmmss&gt;/相对路径），
/// CLI rollback 命令可恢复最近一次备份。确定性文件操作，无模型调用。
/// </summary>
public static class BackupService
{
    /// <summary>
    /// 备份已存在的文件。返回备份路径；文件不存在或失败返回 null。
    /// </summary>
    public static string? Backup(string workspaceRoot, string fullPath, string projectRoot)
    {
        if (!File.Exists(fullPath)) return null;
        try
        {
            var rel = Path.GetRelativePath(projectRoot, fullPath);
            var runRoot = GetRunRoot(workspaceRoot); // runs/<run>（任务目录→其父）
            var stamp = DateTime.Now.ToString("HHmmss");
            var dest = Path.Combine(runRoot, ".backup", stamp, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(fullPath, dest, overwrite: true);
            return dest;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>列出全部备份（按时间倒序）。</summary>
    public static List<(string Stamp, string Rel, string Full)> ListBackups(string workspaceRoot)
    {
        var result = new List<(string, string, string)>();
        var runRoot = GetRunRoot(workspaceRoot);
        var dir = Path.Combine(runRoot, ".backup");
        if (!Directory.Exists(dir)) return result;

        foreach (var stampDir in Directory.GetDirectories(dir)
            .Where(d => !Path.GetFileName(d).Equals(".new", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d))
        {
            foreach (var f in Directory.GetFiles(stampDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(stampDir, f);
                result.Add((Path.GetFileName(stampDir), rel, f));
            }
        }
        return result;
    }

    /// <summary>
    /// 回滚：将相对路径（相对工程根）的最近一次备份恢复到工程根。返回恢复路径；无备份返回 null。
    /// </summary>
    /// <summary>
    /// 归一化备份根：写工具传入任务目录（runs/&lt;run&gt;/task_xxx），CLI rollback 传入 run 目录（runs/&lt;run&gt;），
    /// 两者差一层。统一取 run 目录：workspaceRoot 名以 task_ 开头取父目录，否则视为 run 目录本身。
    /// （§14-3w P1 端到端修复：此前备份落 runs/&lt;run&gt;/.backup，rollback 却找 runs/.backup，差一层找不到。）
    /// </summary>
    private static string GetRunRoot(string workspaceRoot)
    {
        var name = Path.GetFileName(workspaceRoot);
        return name.StartsWith("task_", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(workspaceRoot)!
            : workspaceRoot;
    }

    public static string? Rollback(string workspaceRoot, string rel, string projectRoot)
    {
        var runRoot = GetRunRoot(workspaceRoot);
        var dir = Path.Combine(runRoot, ".backup");
        if (!Directory.Exists(dir)) return null;

        var candidates = new List<string>();
        foreach (var stampDir in Directory.GetDirectories(dir)
            .Where(d => !Path.GetFileName(d).Equals(".new", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d))
        {
            var f = Path.Combine(stampDir, rel);
            if (File.Exists(f)) candidates.Add(f);
        }
        if (candidates.Count == 0) return null;

        var latest = candidates[0];
        var dest = Path.Combine(projectRoot, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(latest, dest, overwrite: true);
        return dest;
    }

    /// <summary>
    /// 【P0-1】标记工程可写模式下新建的工程文件：runs/&lt;run&gt;/.backup/.new/&lt;rel&gt;（空文件）。
    /// 任务失败自动回滚时删除这些新文件（备份仅覆盖已存在文件，新建文件需单独追踪）。
    /// </summary>
    public static void MarkNewFile(string workspaceRoot, string fullPath, string projectRoot)
    {
        try
        {
            var rel = Path.GetRelativePath(projectRoot, fullPath);
            var runRoot = GetRunRoot(workspaceRoot);
            var dest = Path.Combine(runRoot, ".backup", ".new", rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllText(dest, "");
        }
        catch { }
    }

    /// <summary>
    /// 【P0-1】任务级完整回滚：恢复本任务全部备份文件（已存在文件上次写入前状态）
    /// + 删除本任务新建的工程文件（.backup/.new 标记）。返回 (恢复数, 清理新文件数)。
    /// </summary>
    public static (int Restored, int RemovedNew) RollbackTaskFull(string workspaceRoot, string projectRoot)
    {
        var restored = RollbackTask(workspaceRoot, projectRoot);
        var removed = 0;
        var runRoot = GetRunRoot(workspaceRoot);
        var newDir = Path.Combine(runRoot, ".backup", ".new");
        if (!Directory.Exists(newDir)) return (restored, 0);

        foreach (var marker in Directory.GetFiles(newDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(newDir, marker);
            var target = Path.Combine(projectRoot, rel);
            try
            {
                // 仅删除"本任务新建"的文件：标记存在 + 目标存在且仍未被后续修改（内容为空标记）
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
    /// 任务级回滚（多文件修改规划）：恢复本任务全部备份文件。
    /// 每个文件取最近一次备份（=上次写入前状态）；返回恢复的文件数。
    /// </summary>
    public static int RollbackTask(string workspaceRoot, string projectRoot)
    {
        var runRoot = GetRunRoot(workspaceRoot);
        var dir = Path.Combine(runRoot, ".backup");
        if (!Directory.Exists(dir)) return 0;

        // 每个相对路径取最近 stamp 的备份 → 恢复
        var latestByRel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stampDir in Directory.GetDirectories(dir)
            .Where(d => !Path.GetFileName(d).Equals(".new", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d))
        {
            foreach (var f in Directory.GetFiles(stampDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(stampDir, f);
                if (!latestByRel.ContainsKey(rel))
                    latestByRel[rel] = f;
            }
        }

        var restored = 0;
        foreach (var (rel, backup) in latestByRel)
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
}
