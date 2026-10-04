// 引入运行时互操作（GetLongPathNameW / GetShortPathNameW）
using System.Runtime.InteropServices;
using System.Text;

// 命名空间
namespace Agent.Infrastructure.Project;

/// <summary>
/// 敏感区源头守卫（读=黑名单排除）。核心语义：
/// 【源头拒绝】路径在读取动作【之前】判定，命中即拒——内容物理不进 chatHistory / SQLite / 记忆库。
/// 覆盖四条绕过通道：
///   1) StartsWith 前缀假阳性          → PathBoundary 逐段比较
///   2) 8.3 短文件名（C:\PROGRA~1）     → GetLongPathNameW / GetShortPathNameW 双向展开判定
///   3) 符号链接 / junction             → ResolveLinkTarget 解析最终目标后双查（仅路径存在时，零开销于普通路径）
///   4) UNC 长路径前缀（\\?\C:\…）      → Normalize 剥前缀
/// 已知边界（诚实记录）：脚本内敏感读（python script.py 内部读敏感文件）不做——
///   触发前提是用户明确要求读敏感文件（用户意图即授权）；下一轮单独做"写入内容敏感扫描"。
/// </summary>
public static class SensitivePathGuard
{
    // 拒绝根集合（长名形式，OrdinalIgnoreCase 逐段比较）
    private static readonly HashSet<string> _deniedRoots = new(StringComparer.OrdinalIgnoreCase);

    // 拒绝根的 8.3 短名形式（仅对存在的目录成功展开）
    private static readonly HashSet<string> _deniedShortRoots = new(StringComparer.OrdinalIgnoreCase);

    // 用户自定义 denylist 是否已加载（幂等）
    private static bool _userListLoaded;

    /// <summary>内置拒绝根（系统区 + 凭据区）。</summary>
    private static readonly string[] BuiltinDeniedRoots =
    {
        // ── 第一类：系统区（防误读系统文件）──
        @"C:\Windows",
        @"C:\Program Files",
        @"C:\Program Files (x86)",
        @"C:\ProgramData",

        // ── 第二类：凭据区（防敏感信息进持久层）──
        @"C:\Users\26543\.ssh",
        @"C:\Users\26543\.aws",
        @"C:\Users\26543\.kube",
        @"C:\Users\26543\.docker",
        @"C:\Users\26543\.config\gcloud",
        @"C:\Users\26543\.git-credentials",
        @"C:\Users\26543\.npmrc",
        @"C:\Users\26543\AppData\Local\Microsoft\Credentials",
        @"C:\Users\26543\AppData\Roaming\Microsoft\Credentials"
    };

    static SensitivePathGuard()
    {
        foreach (var root in BuiltinDeniedRoots)
        {
            AddDeniedRoot(root);
        }
    }

    /// <summary>加入一个拒绝根（含 8.3 短名形式，若该目录存在且可展开）。</summary>
    public static void AddDeniedRoot(string root)
    {
        var norm = PathBoundary.Normalize(root);
        if (string.IsNullOrEmpty(norm))
            return;

        _deniedRoots.Add(norm);

        // 短名展开：仅对存在的目录有效（不存在则无短名可言）
        var shortForm = TryExpandShort(norm, longForm: false);
        if (!string.IsNullOrEmpty(shortForm) && !shortForm.Equals(norm, StringComparison.OrdinalIgnoreCase))
            _deniedShortRoots.Add(shortForm);
    }

    /// <summary>
    /// 加载用户自定义 denylist（第三类预留）：workspace\sensitive_denylist.txt。
    /// 格式：# 注释 / 空行跳过 / trim 后归一化；文件不存在静默跳过。幂等（只加载一次）。
    /// </summary>
    public static void LoadUserDenylist(string workspaceRoot)
    {
        if (_userListLoaded) return;
        _userListLoaded = true;

        try
        {
            var file = Path.Combine(workspaceRoot, "sensitive_denylist.txt");
            if (!File.Exists(file)) return;

            foreach (var raw in File.ReadAllLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;
                AddDeniedRoot(line);
            }
        }
        catch
        {
            // 加载失败静默：内置黑名单仍生效
        }
    }

    /// <summary>
    /// 路径是否命中敏感区（源头判定，读取动作前调用）。
    /// fullPath 可为绝对路径（读放开后模型直接给绝对路径）或经解析的完整路径。
    /// 覆盖：原路径长名 / 短名 / 符号链接最终目标 三条判定线，任一命中即拒。
    /// </summary>
    public static bool IsDenied(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return false;

        var norm = PathBoundary.Normalize(fullPath);
        if (string.IsNullOrEmpty(norm))
            return false;

        // 判定线 1：原路径（长名）
        if (IsUnderAny(norm, _deniedRoots))
            return true;

        // 判定线 2：8.3 短名展开（原路径转长名 / 转短名）
        var longForm = TryExpandShort(norm, longForm: true);
        if (!string.IsNullOrEmpty(longForm) && IsUnderAny(longForm, _deniedRoots))
            return true;

        var shortForm = TryExpandShort(norm, longForm: false);
        if (!string.IsNullOrEmpty(shortForm) && IsUnderAny(shortForm, _deniedShortRoots))
            return true;

        // 判定线 3：符号链接 / junction 逐段解析（Windows 最常见的绕过形态是中间段链接：
        // X:\work\link → C:\Users\26543\.ssh，Path.GetFullPath 不展开；reparse point 在目录段上，
        // 只对 fullPath 整体 ResolveLinkTarget 会漏——必须逐段探测）。
        // 性能：普通路径每段 ResolveLinkTarget 返回 null（毫秒级），只有命中链接才展开最终目标。
        // 用户优化：不存在的段跳过（不存在的路径读不了，解析无意义）。
        try
        {
            var segs = norm.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            var prefix = new StringBuilder();
            for (var i = 0; i < segs.Length; i++)
            {
                // 首段（盘符/UNC 服务器）不可能是链接，跳过探测
                if (i == 0)
                {
                    prefix.Append(segs[i]);
                    continue;
                }
                prefix.Append('\\').Append(segs[i]);
                var segPath = prefix.ToString();

                FileSystemInfo? segInfo = null;
                if (Directory.Exists(segPath)) segInfo = new DirectoryInfo(segPath);
                else if (File.Exists(segPath)) segInfo = new FileInfo(segPath);
                else continue; // 段不存在（目标文件缺失）→ 跳过

                var target = segInfo.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null) continue;

                var tail = i < segs.Length - 1
                    ? "\\" + string.Join("\\", segs.Skip(i + 1))
                    : string.Empty;
                var resolved = Path.IsPathRooted(target.FullName)
                    ? target.FullName + tail
                    : Path.GetFullPath(target.FullName + tail, segPath);
                if (IsUnderAny(PathBoundary.Normalize(resolved), _deniedRoots))
                    return true;
            }
        }
        catch
        {
            // 解析失败按未命中处理（不阻断正常读取）
        }

        return false;
    }

    /// <summary>
    /// 启动自检：writeRoots（workspaceRoot / projectRoot）是否与敏感区重叠。
    /// 用户手滑把工程根加进 denylist 会导致读写双拒——启动时 WARN 提示。
    /// </summary>
    public static List<string> FindWriteRootOverlaps(IEnumerable<string> writeRoots)
    {
        var hits = new List<string>();
        foreach (var root in writeRoots)
        {
            var norm = PathBoundary.Normalize(root);
            if (string.IsNullOrEmpty(norm)) continue;

            // 任一拒绝根是 norm 的祖先（即 norm 落在敏感区内）→ 重叠
            foreach (var denied in _deniedRoots)
            {
                if (PathBoundary.IsInside(denied, norm))
                {
                    hits.Add(norm);
                    break;
                }
            }
        }
        return hits;
    }

    /// <summary>fullPath 是否位于任一拒绝根之下（含自身）。</summary>
    private static bool IsUnderAny(string fullPath, HashSet<string> roots)
    {
        foreach (var root in roots)
        {
            if (PathBoundary.IsInside(root, fullPath))
                return true;
        }
        return false;
    }

    // ─────────── Win32 8.3 短名展开 ───────────

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string lpszShortPath, StringBuilder lpszLongPath, uint cchBuffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);

    /// <summary>把路径展开为长名（longForm=true）或短名（longForm=false）。失败返回 null。</summary>
    private static string? TryExpandShort(string path, bool longForm)
    {
        try
        {
            var buf = new StringBuilder(4096);
            var r = longForm
                ? GetLongPathNameW(path, buf, (uint)buf.Capacity)
                : GetShortPathNameW(path, buf, (uint)buf.Capacity);
            if (r > 0 && r < buf.Capacity)
            {
                var s = buf.ToString();
                return s.Length > 0 ? PathBoundary.Normalize(s) : null;
            }
        }
        catch
        {
            // P/Invoke 失败按不可用处理
        }
        return null;
    }
}
