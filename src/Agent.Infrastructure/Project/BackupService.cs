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

        foreach (var stampDir in Directory.GetDirectories(dir).OrderByDescending(d => d))
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
        foreach (var stampDir in Directory.GetDirectories(dir).OrderByDescending(d => d))
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
}
