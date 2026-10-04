// 命名空间
namespace Agent.Infrastructure.Project;

/// <summary>
/// 路径边界判定（读写策略共用）。根治 StartsWith 前缀假阳性：
/// 按路径【逐段】比较，root 自身与任意子路径 = true；
/// 前缀同名目录（X:\proj2 vs X:\proj）= false；.. 跳脱经 GetFullPath 归一化后必越界。
/// </summary>
public static class PathBoundary
{
    /// <summary>
    /// fullPath 是否位于 root 之内（含 root 自身）。
    /// 逐段比较（OrdinalIgnoreCase + 分隔符归一），杜绝字符串前缀误判。
    /// </summary>
    public static bool IsInside(string root, string fullPath)
    {
        var r = Normalize(root);
        var f = Normalize(fullPath);
        if (string.IsNullOrEmpty(r) || string.IsNullOrEmpty(f))
            return false;

        var rSegs = SplitSegments(r);
        var fSegs = SplitSegments(f);

        // 段数不足（f 比 root 短）→ 不可能在内（root 自身 = 段数相等且全等）
        if (fSegs.Length < rSegs.Length)
            return false;

        for (var i = 0; i < rSegs.Length; i++)
        {
            if (!string.Equals(rSegs[i], fSegs[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    /// <summary>路径归一化：剥 \\?\ / \\.\ 前缀 + 绝对化 + 统一分隔符为 \ 。</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var p = path.Trim();
        // UNC 长路径前缀（\\?\ 与 \\.\）：剥掉后按普通路径归一化，再按需由调用方决定是否保留 UNC 语义
        if (p.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            p = p.Substring(4);
        else if (p.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
            p = p.Substring(4);

        try
        {
            p = Path.GetFullPath(p);
        }
        catch
        {
            // 非法路径原样返回（后续逐段比较自然不匹配）
        }

        return p.Replace('/', '\\');
    }

    /// <summary>按路径分段切分（根盘符 / UNC 服务器段与普通目录段统一处理）。</summary>
    private static string[] SplitSegments(string normalized)
    {
        var segs = normalized.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
        // 盘符根（C:）或 UNC 根（\\server\share）首段不带反斜杠，补齐语义：
        // 直接返回切分结果即可——逐段比较对首段同样适用（C: vs C:，\\server vs \\server）
        return segs;
    }
}
