// 引入 IO/集合
using System.Text;

// 命名空间
namespace Agent.Infrastructure.Project;

/// <summary>
/// 工程扫描器（工程感知 P0）：确定性扫描工程根，
/// 产出结构化「工程上下文数据块」供模型感知项目结构。
/// 纯文件系统操作，无模型调用；排除构建产物/版本库/依赖目录防止上下文爆炸。
/// </summary>
public static class ProjectScanner
{
    /// <summary>排除目录（构建产物/版本库/依赖）。</summary>
    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "obj", "bin", ".git", ".vs", ".vscode", ".idea",
        "node_modules", "dist", "build", "packages", "__pycache__", ".venv", "venv", "workspace"
    };

    /// <summary>入口/说明文件（精确名匹配）。</summary>
    private static readonly HashSet<string> EntryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "program.cs", "main.py", "app.py", "readme.md", "package.json"
    };

    /// <summary>依赖/工程清单扩展名（读这些能了解技术栈）。</summary>
    private static readonly HashSet<string> DepExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj", ".sln", ".slnx", ".fsproj", ".vbproj"
    };
    private static readonly HashSet<string> DepNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json", "requirements.txt", "pyproject.toml", "cargo.toml", "go.mod", "pom.xml"
    };

    /// <summary>扫描深度上限（防符号链接/深嵌套爆炸）。</summary>
    private const int MaxDepth = 6;
    /// <summary>目录树输出行数上限。</summary>
    private const int MaxTreeLines = 300;
    /// <summary>文件数上限（超出截断并标注）。</summary>
    private const int MaxFiles = 2000;

    /// <summary>扫描结果。</summary>
    /// <remarks>DirCount/TotalBytes/MaxMtimeTicks 构成文件系统签名（P2 跨任务记忆：缓存校验用）。
    /// CompactBlock 为注入瘦身版（概要+目录聚合），模型需要详情时 list_dir/read_file 自取。</remarks>
    public sealed record ScanResult(
        string Overview,
        string ContextBlock,
        string CompactBlock,
        int FileCount,
        int DirCount,
        long TotalBytes,
        long MaxMtimeTicks);

    /// <summary>扫描工程根。</summary>
    public static ScanResult Scan(string root)
    {
        root = Path.GetFullPath(root);
        var files = new List<(string Rel, long Size, long MtimeTicks)>();
        var dirs = new List<string>();
        var excludedCount = 0;

        Walk(root, "", 0, files, dirs, ref excludedCount);

        // 入口识别：EntryNames 精确匹配 + 工程清单（sln/slnx）
        var entries = files
            .Where(f => EntryNames.Contains(Path.GetFileName(f.Rel)))
            .Select(f => f.Rel)
            .ToList();
        var slnEntries = files
            .Where(f => f.Rel.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                     || f.Rel.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Rel)
            .ToList();
        entries.AddRange(slnEntries);
        entries = entries.Distinct().OrderBy(x => x).ToList();

        // 依赖清单
        var deps = files
            .Where(f => DepExtensions.Contains(Path.GetExtension(f.Rel))
                     || DepNames.Contains(Path.GetFileName(f.Rel)))
            .Select(f => f.Rel)
            .OrderBy(x => x)
            .ToList();

        // 总大小 + 最新修改时间（文件系统签名）
        var totalBytes = files.Sum(f => f.Size);
        var maxMtimeTicks = files.Count == 0 ? 0L : files.Max(f => f.MtimeTicks);

        // Overview（CLI 展示用）
        var overview = new StringBuilder();
        overview.AppendLine($"[工程] {root}");
        overview.AppendLine($"  文件 {files.Count} 个 / 目录 {dirs.Count} 个 / 总大小 {FormatBytes(totalBytes)}（排除 {excludedCount} 个忽略项）");
        if (entries.Count > 0)
            overview.AppendLine($"  入口/说明: {string.Join(", ", entries.Take(8))}");
        if (deps.Count > 0)
            overview.AppendLine($"  依赖清单: {string.Join(", ", deps.Take(6))}");
        if (files.Count == 0)
            overview.AppendLine("  (目录为空或无可见文件)");

        // ContextBlock（注入模型）
        var cb = new StringBuilder();
        cb.AppendLine("[工程上下文] 工程根: " + root);
        cb.AppendLine($"文件统计: {files.Count} 个文件 / {dirs.Count} 个目录（排除 obj/bin/.git 等 {excludedCount} 个忽略项）");
        cb.AppendLine($"总大小: {FormatBytes(totalBytes)}");
        if (entries.Count > 0)
            cb.AppendLine("入口/说明: " + string.Join(", ", entries));
        else
            cb.AppendLine("入口/说明: (未识别到 Program.cs/Main.py/README 等)");
        if (deps.Count > 0)
            cb.AppendLine("依赖清单: " + string.Join(", ", deps));
        cb.AppendLine("目录树:");
        var (treeLines, treeTruncated) = BuildTree(root, files, dirs);
        foreach (var line in treeLines)
            cb.AppendLine(line);
        if (treeTruncated)
            cb.AppendLine($"...(目录树已截断，共 {files.Count} 文件)");
        cb.AppendLine("可用工具：list_dir/read_file 可读取工程内文件（路径相对工程根）。");

        // CompactBlock（P2 注入瘦身版：概要 + 目录聚合树，模型需要详情时 list_dir/read_file 自取）
        var compact = new StringBuilder();
        compact.AppendLine("[工程上下文] 工程根: " + root);
        compact.AppendLine($"文件统计: {files.Count} 个文件 / {dirs.Count} 个目录（排除 obj/bin/.git 等 {excludedCount} 个忽略项）");
        compact.AppendLine($"总大小: {FormatBytes(totalBytes)}");
        if (entries.Count > 0)
            compact.AppendLine("入口/说明: " + string.Join(", ", entries));
        else
            compact.AppendLine("入口/说明: (未识别到 Program.cs/Main.py/README 等)");
        if (deps.Count > 0)
            compact.AppendLine("依赖清单: " + string.Join(", ", deps));
        compact.AppendLine("目录文件数（按目录聚合；具体文件用 list_dir/read_file 查看）:");
        var dirCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dirs) dirCounts[d] = 0;
        var rootFileCount = 0;
        foreach (var f in files)
        {
            var d = Path.GetDirectoryName(f.Rel);
            if (d is null || d.Length == 0) rootFileCount++;
            else if (dirCounts.ContainsKey(d)) dirCounts[d]++;
        }
        compact.AppendLine("(根) " + rootFileCount + " 个文件");
        foreach (var d in dirs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            compact.AppendLine(d + "/ " + dirCounts[d] + " 个文件");
        compact.AppendLine("可用工具：list_dir/read_file 可读取工程内文件（路径相对工程根）。");

        return new ScanResult(overview.ToString(), cb.ToString(), compact.ToString(), files.Count, dirs.Count, totalBytes, maxMtimeTicks);
    }

    /// <summary>
    /// 轻量文件系统签名（P2 跨任务记忆）：
    /// 快速 Walk 收集 (FileCount, DirCount, TotalBytes, MaxMtimeTicks)，不构建目录树/上下文块。
    /// 供 AgentLoop 每任务注入前校验 ProjectContextStore 缓存是否仍有效。
    /// </summary>
    public static (int FileCount, int DirCount, long TotalBytes, long MaxMtimeTicks) ComputeSignature(string root)
    {
        root = Path.GetFullPath(root);
        var files = new List<(string Rel, long Size, long MtimeTicks)>();
        var dirs = new List<string>();
        int excluded = 0;
        Walk(root, "", 0, files, dirs, ref excluded);
        var totalBytes = files.Sum(f => f.Size);
        var maxMtimeTicks = files.Count == 0 ? 0L : files.Max(f => f.MtimeTicks);
        return (files.Count, dirs.Count, totalBytes, maxMtimeTicks);
    }

    /// <summary>递归遍历（限深度）。</summary>
    private static void Walk(string root, string rel, int depth,
        List<(string Rel, long Size, long MtimeTicks)> files, List<string> dirs, ref int excluded)
    {
        if (depth > MaxDepth) return;
        var full = Path.Combine(root, rel);

        foreach (var d in Directory.GetDirectories(full))
        {
            var name = Path.GetFileName(d);
            if (ExcludedDirs.Contains(name))
            {
                excluded++;
                continue;
            }
            var childRel = rel.Length == 0 ? name : rel + "\\" + name;
            dirs.Add(childRel);
            Walk(root, childRel, depth + 1, files, dirs, ref excluded);
        }

        if (files.Count >= MaxFiles) return;
        foreach (var f in Directory.GetFiles(full))
        {
            if (files.Count >= MaxFiles) break;
            var childRel = rel.Length == 0 ? Path.GetFileName(f) : rel + "\\" + Path.GetFileName(f);
            var fi = new FileInfo(f);
            files.Add((childRel, fi.Length, fi.LastWriteTimeUtc.Ticks));
        }
    }

    /// <summary>目录树（缩进 + 截断）。</summary>
    private static (List<string> Lines, bool Truncated) BuildTree(string root, List<(string Rel, long Size, long MtimeTicks)> files,
        List<string> dirs)
    {
        var lines = new List<string>();
        var truncated = false;
        // 目录树：每个文件按其 rel 的层级缩进
        var all = files.Select(f => f.Rel)
            .Concat(dirs.Select(d => d + "\\"))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var item in all)
        {
            if (lines.Count >= MaxTreeLines)
            {
                truncated = true;
                break;
            }
            var isDir = item.EndsWith("\\");
            var depth = item.Count(c => c == Path.DirectorySeparatorChar);
            var name = isDir ? Path.GetFileName(item.TrimEnd('\\')) + "/" : Path.GetFileName(item);
            lines.Add(new string(' ', depth * 2) + name);
        }
        if (lines.Count == 0)
            lines.Add("(空)");
        return (lines, truncated);
    }

    /// <summary>字节格式化。</summary>
    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
        return (bytes / (1024.0 * 1024)).ToString("F1") + " MB";
    }
}
