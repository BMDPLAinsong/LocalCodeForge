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

    // 【10-14 P3-2】8.3 短名展开的惰性状态与锁：原在静态构造里对 13 个内置根各做一次
    // GetShortPathNameW（P/Invoke）——首次触发本类任意成员即阻塞（网络盘/异常用户目录可挂住进程）。
    // 现改为：静态构造只登记长名（零 P/Invoke），短名在首次 IsDenied 时一次性展开（加锁幂等）。
    private static bool _shortRootsExpanded;
    private static readonly object _expandLock = new();

    // 【10-14 P3-2】判定结果缓存：仅缓存"已判定为敏感"的正向结果（fail-safe 方向——
    // 路径后来被换成指向敏感区的链接也仍然拒），有界 4096 条、超限整体清空。
    // 【明确不做】负向（未命中）缓存：两次判定之间若有人把中间目录换成指向敏感区的 junction，
    // 陈旧的 false 会放行读取——这是安全漏洞，不是性能取舍。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _deniedVerdictCache
        = new(StringComparer.OrdinalIgnoreCase);
    private const int DeniedVerdictCacheCapacity = 4096;

    /// <summary>内置拒绝根（系统区 + 凭据区）。静态构造中按当前用户动态构建（不硬编码用户名）。</summary>
    private static readonly string[] BuiltinDeniedRoots;

    static SensitivePathGuard()
    {
        // 动态用户目录：不硬编码用户名（跨机器可用 + 公开仓库不暴露个人环境）
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appDataLocal = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appDataRoaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        BuiltinDeniedRoots = new[]
        {
            // ── 第一类：系统区（防误读系统文件）──
            @"C:\Windows",
            @"C:\Program Files",
            @"C:\Program Files (x86)",
            @"C:\ProgramData",

            // ── 第二类：凭据区（防敏感信息进持久层）──
            Path.Combine(userProfile, ".ssh"),
            Path.Combine(userProfile, ".aws"),
            Path.Combine(userProfile, ".kube"),
            Path.Combine(userProfile, ".docker"),
            Path.Combine(userProfile, ".config", "gcloud"),
            Path.Combine(userProfile, ".git-credentials"),
            Path.Combine(userProfile, ".npmrc"),
            Path.Combine(appDataLocal, "Microsoft", "Credentials"),
            Path.Combine(appDataRoaming, "Microsoft", "Credentials")
        };

        // 仅登记长名（静态构造零 P/Invoke）；短名展开延后到首次 IsDenied
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

        // 【10-14 P3-2】展开未完成时不各自 P/Invoke——统一由 EnsureShortRootsExpanded 覆盖
        // （含此刻之前/之后加入的全部根，避免"启动期集合"与"懒展开期集合"两套短名不一致）
        if (_shortRootsExpanded)
            TryAddShortForm(norm);
    }

    /// <summary>惰性展开全部拒绝根的 8.3 短名（首次 IsDenied 调用，加锁幂等）。</summary>
    private static void EnsureShortRootsExpanded()
    {
        if (_shortRootsExpanded) return;
        lock (_expandLock)
        {
            if (_shortRootsExpanded) return;
            foreach (var root in _deniedRoots)
                TryAddShortForm(root);
            _shortRootsExpanded = true;
        }
    }

    /// <summary>登记一个拒绝根的短名形式（仅对存在的目录有效——不存在则无短名可言）。</summary>
    private static void TryAddShortForm(string norm)
    {
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

        // 【10-14 P3-2】正向判定缓存（只记"敏感"；负向不缓存——理由见字段注释）
        if (_deniedVerdictCache.TryGetValue(norm, out var cachedVerdict))
            return cachedVerdict;

        // 短名展开惰性触发（加锁幂等；必须早于任何 _deniedShortRoots 读取）
        EnsureShortRootsExpanded();

        var denied = ComputeDenied(norm);
        if (denied)
        {
            if (_deniedVerdictCache.Count >= DeniedVerdictCacheCapacity)
                _deniedVerdictCache.Clear();   // 有界：整体清空（纯函数缓存，丢弃不影响正确性）
            _deniedVerdictCache[norm] = true;
        }
        return denied;
    }

    /// <summary>四条判定线的实际计算（原 IsDenied 主体，抽出以便缓存包装）。</summary>
    private static bool ComputeDenied(string norm)
    {
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
        // X:\work\link → %USERPROFILE%\.ssh，Path.GetFullPath 不展开；reparse point 在目录段上，
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
