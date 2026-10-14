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
    /// <summary>排除目录（构建产物/版本库/依赖）——【10-14 P1-4】单源 FileGrammar.ExcludedDirNames
    /// （本表原最全实现提升为全层共享；SymbolIndexer/DependencyAnalyzer 原两份顶层 StartsWith 复制同源改调）。</summary>
    private static readonly HashSet<string> ExcludedDirs = FileGrammar.ExcludedDirNames;

    /// <summary>入口/说明文件（精确名匹配）。</summary>
    private static readonly HashSet<string> EntryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "program.cs", "main.py", "app.py", "readme.md", "package.json"
    };

    /// <summary>依赖/工程清单扩展名（读这些能了解技术栈）——【10-14 P1-4 复核】单源
    /// FileGrammar.ProjectManifestExtensions（原与 Orchestrator.EnumerateExistingFiles 各一份）。</summary>
    private static readonly HashSet<string> DepExtensions = FileGrammar.ProjectManifestExtensions;
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

    /// <summary>
    /// 【10-09 方案1/2 数据源】返回工程内全部可见文件（相对路径 + 大小），复用 Walk 的排除规则。
    /// 供 read_file 失败候选匹配（FindFileCandidates）与核心文件清单（CoreFileLines）使用。
    /// </summary>
    public static List<(string Rel, long Size)> ListFilesWithSize(string root)
    {
        root = Path.GetFullPath(root);
        var files = new List<(string Rel, long Size, long MtimeTicks)>();
        var dirs = new List<string>();
        int excluded = 0;
        Walk(root, "", 0, files, dirs, ref excluded);
        return files.Select(f => (f.Rel, f.Size)).ToList();
    }

    /// <summary>
    /// 【10-09 方案1】read_file 命中"文件不存在"时的候选路径匹配（数据驱动，非提示词）：
    /// 先按文件名精确匹配（同名不同目录 → 全部列出）；无精确命中再模糊匹配
    /// （扩展名相同 + 名字公共前缀 ≥4 字符，如 TaskEvent.cs → TaskEventHub.cs；
    ///  或一名字包含另一名字，如 Order.cs → OrderService.cs）。
    /// 返回最多 5 个相对路径；无匹配返回空列表。
    /// </summary>
    public static List<string> FindFileCandidates(string root, string requestedRel)
    {
        if (string.IsNullOrWhiteSpace(requestedRel)) return new List<string>();
        var requestedName = Path.GetFileName(requestedRel.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(requestedName)) return new List<string>();

        List<(string Rel, long Size)> files;
        try { files = ListFilesWithSize(root); }
        catch { return new List<string>(); }

        var exact = files
            .Where(f => string.Equals(Path.GetFileName(f.Rel), requestedName, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Rel)
            .Take(5)
            .ToList();
        if (exact.Count > 0) return exact;

        var reqStem = Path.GetFileNameWithoutExtension(requestedName);
        var reqExt = Path.GetExtension(requestedName);
        return files
            .Where(f =>
            {
                var candName = Path.GetFileName(f.Rel);
                var candStem = Path.GetFileNameWithoutExtension(candName);
                var candExt = Path.GetExtension(candName);
                // 扩展名相同（或双方都无扩展名）+ 名字公共前缀 ≥4 → 近似（TaskEvent.cs → TaskEventHub.cs）
                if (string.Equals(reqExt, candExt, StringComparison.OrdinalIgnoreCase)
                    && CommonPrefixLen(reqStem, candStem) >= 4)
                    return true;
                // 名字包含关系（Order.cs → OrderService.cs / OrderService.cs → Order.cs）
                return candStem.Contains(reqStem, StringComparison.OrdinalIgnoreCase)
                    || reqStem.Contains(candStem, StringComparison.OrdinalIgnoreCase);
            })
            .Select(f => f.Rel)
            .Take(5)
            .ToList();
    }

    /// <summary>两名字的公共前缀长度（大小写不敏感）。</summary>
    private static int CommonPrefixLen(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i])) i++;
        return i;
    }

    /// <summary>
    /// 【10-09 方案2】核心文件清单（分析/总结类任务注入用，均为相对工程根的真实路径）：
    /// 入口（EntryNames + sln/slnx）+ 依赖清单 + 按体积降序 Top8 源码文件。
    /// 让模型 5-8 步内读完全部核心文件并总结，避免逐文件发散探索。
    /// </summary>
    public static string CoreFileLines(string root)
    {
        List<(string Rel, long Size)> files;
        try { files = ListFilesWithSize(root); }
        catch { return string.Empty; }
        if (files.Count == 0) return string.Empty;

        var entries = files
            .Where(f => EntryNames.Contains(Path.GetFileName(f.Rel))
                     || f.Rel.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                     || f.Rel.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Rel)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var deps = files
            .Where(f => DepExtensions.Contains(Path.GetExtension(f.Rel))
                     || DepNames.Contains(Path.GetFileName(f.Rel)))
            .Select(f => f.Rel)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sourceExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".py", ".js", ".ts", ".java", ".go", ".rs", ".cpp", ".c", ".h", ".vue", ".cshtml", ".razor"
        };
        var big = files
            .Where(f => sourceExts.Contains(Path.GetExtension(f.Rel)))
            .OrderByDescending(f => f.Size)
            .Take(8)
            .Select(f => $"{f.Rel} ({FormatBytes(f.Size)})")
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("[工程核心文件]（均为相对工程根的路径，建议按序阅读后总结）");
        if (entries.Count > 0) sb.AppendLine("入口: " + string.Join(", ", entries.Take(6)));
        if (deps.Count > 0) sb.AppendLine("依赖: " + string.Join(", ", deps.Take(6)));
        if (big.Count > 0) sb.AppendLine("大源码文件(按体积): " + string.Join(", ", big));
        return sb.ToString();
    }

    /// <summary>递归遍历（限深度）。无权限子目录（回收站/系统隐藏）跳过不崩溃（10-08：use 盘根实证）。</summary>
    private static void Walk(string root, string rel, int depth,
        List<(string Rel, long Size, long MtimeTicks)> files, List<string> dirs, ref int excluded)
    {
        if (depth > MaxDepth) return;
        var full = Path.Combine(root, rel);

        string[] subDirs;
        try
        {
            subDirs = Directory.GetDirectories(full);
        }
        catch (UnauthorizedAccessException)
        {
            excluded++;   // 无权限目录（如 $RECYCLE.BIN）跳过，不计入统计
            return;
        }
        catch (IOException) { return; }   // 目录被占用/已删除等瞬时异常，同样容忍

        foreach (var d in subDirs)
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
        try
        {
            foreach (var f in Directory.GetFiles(full))
            {
                if (files.Count >= MaxFiles) break;
                var childRel = rel.Length == 0 ? Path.GetFileName(f) : rel + "\\" + Path.GetFileName(f);
                var fi = new FileInfo(f);
                files.Add((childRel, fi.Length, fi.LastWriteTimeUtc.Ticks));
            }
        }
        catch (UnauthorizedAccessException) { /* 目录内个别文件无权限：跳过该目录文件枚举 */ }
        catch (IOException) { }
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
