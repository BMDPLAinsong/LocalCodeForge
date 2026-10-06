// ============================================================
// Diff / FileDiffService.cs
// 文件行级 Diff 引擎（unified hunk 模型，零第三方依赖）。
//   · 输入：备份文件（改动前） + 当前文件（改动后）；缺文件按空处理。
//   · 算法：剥离共同前缀/后缀 → 只对中间变化段做行级 LCS
//     （byte 方向表 + 滚动长度数组，回溯输出全量 diff）。
//     大文件改 1 行：中间段 1×1，O(1) 恒可 diff；全不同大文件：中间段超限才降级。
//   · 裁剪：默认 ±3 上下文行，相邻块 gap ≤ 2×Context 合并，输出 unified hunk（@@ 头 + 行）。
//   · 降级：中间变化段任一侧 > 2500 行或乘积超限 → TooLarge（提示用 rollback 恢复）。
// 设计（10-07）：语义单一来源——终端 diff 命令与 Web Dashboard 的 /api/diff 直接复用本模型，
// 不设 DTO（消双路径漂移）。tests 直接断言 hunk 结构与裁剪行为。
// ============================================================
namespace Agent.Infrastructure.Diff;

public enum DiffLineType { Same, Add, Remove }

/// <summary>一行 diff：类型 + 新旧行号（1-based，仅 Same/Remove 有 OldNo，Same/Add 有 NewNo）+ 文本。</summary>
public sealed record DiffLine(DiffLineType Type, int OldNo, int NewNo, string Text);

/// <summary>一个 unified hunk：@@ -OldStart,OldCount +NewStart,NewCount @@ + 行。</summary>
public sealed record DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<DiffLine> Lines);

/// <summary>diff 结果：TooLarge=true 时 Hunks 为空（明确降级）。</summary>
public sealed record DiffResult(bool TooLarge, string? TooLargeReason, IReadOnlyList<DiffHunk> Hunks);

/// <summary>裁剪选项：默认 ±3 上下文行。</summary>
public sealed record DiffOptions
{
    public int ContextLines { get; init; } = 3;
}

public static class FileDiffService
{
    /// <summary>降级阈值（针对中间变化段）：任一侧超限，或乘积超限（byte 方向表 2500²=6.25MB 安全）。</summary>
    public const int MaxSideLines = 2500;

    public static DiffResult Compute(string beforePath, string afterPath, DiffOptions? options = null)
    {
        var before = ReadLines(beforePath);
        var after = ReadLines(afterPath);
        return ComputeLines(before, after, options);
    }

    public static DiffResult ComputeLines(IReadOnlyList<string> before, IReadOnlyList<string> after, DiffOptions? options = null)
    {
        var ctx = options?.ContextLines ?? 3;
        int n = before.Count, m = after.Count;

        // 剥离共同前缀/后缀——大文件改 1 行时中间段极小，LCS 恒可行
        int prefix = 0;
        while (prefix < n && prefix < m && string.Equals(before[prefix], after[prefix], StringComparison.Ordinal))
            prefix++;
        int suffix = 0;
        while (suffix < n - prefix && suffix < m - prefix
               && string.Equals(before[n - 1 - suffix], after[m - 1 - suffix], StringComparison.Ordinal))
            suffix++;
        var midN = n - prefix - suffix;
        var midM = m - prefix - suffix;

        // 完全相同 → 无 diff（连 LCS 都不跑）
        if (midN == 0 && midM == 0)
            return new DiffResult(false, null, Array.Empty<DiffHunk>());

        // 降级只针对中间变化段
        if (midN > MaxSideLines || midM > MaxSideLines || (long)midN * midM > (long)MaxSideLines * MaxSideLines)
            return new DiffResult(
                TooLarge: true,
                TooLargeReason: $"文件变化过大（{n} → {m} 行，变化段 {midN}×{midM}），diff 不支持；可用 rollback 恢复。",
                Hunks: Array.Empty<DiffHunk>());

        var full = ComputeMidDiff(before, after, prefix, suffix);
        return new DiffResult(false, null, BuildHunks(full, ctx));
    }

    // ───────────────────────────── 内部：变化段 LCS + 回溯 ─────────────────────────────

    /// <summary>中间变化段全量行 diff（Same/Add/Remove 完整序列，行号带 prefix 偏移）。</summary>
    private static List<DiffLine> ComputeMidDiff(IReadOnlyList<string> a, IReadOnlyList<string> b, int prefix, int suffix)
    {
        int n = a.Count, m = b.Count;
        int midN = n - prefix - suffix, midM = m - prefix - suffix;
        // 滚动长度数组（O(midM)）+ byte 方向表（O(midN*midM)，0=对角/1=上(before 删)/2=左(after 增)）
        var dir = new byte[midN + 1, midM + 1];
        var dp = new int[midM + 1];
        for (var i = 1; i <= midN; i++)
        {
            var prev = 0;
            for (var j = 1; j <= midM; j++)
            {
                var cur = dp[j];
                if (string.Equals(a[prefix + i - 1], b[prefix + j - 1], StringComparison.Ordinal))
                {
                    dp[j] = prev + 1;
                    dir[i, j] = 0;
                }
                else if (dp[j] >= dp[j - 1]) // 上（before 删）优先于左（after 增）——稳定删除偏好
                {
                    dp[j] = cur;
                    dir[i, j] = 1;
                }
                else
                {
                    dp[j] = dp[j - 1];
                    dir[i, j] = 2;
                }
                prev = cur;
            }
        }

        // 回溯（倒序），行号 = prefix + 段内索引（1-based）
        var rev = new List<DiffLine>();
        int i0 = midN, j0 = midM;
        while (i0 > 0 || j0 > 0)
        {
            if (i0 > 0 && j0 > 0 && dir[i0, j0] == 0)
            {
                rev.Add(new DiffLine(DiffLineType.Same, prefix + i0, prefix + j0, a[prefix + i0 - 1]));
                i0--; j0--;
            }
            else if (i0 > 0 && (j0 == 0 || dir[i0, j0] == 1))
            {
                rev.Add(new DiffLine(DiffLineType.Remove, prefix + i0, 0, a[prefix + i0 - 1]));
                i0--;
            }
            else
            {
                rev.Add(new DiffLine(DiffLineType.Add, 0, prefix + j0, b[prefix + j0 - 1]));
                j0--;
            }
        }
        rev.Reverse();

        // 补全共同前缀/后缀 Same 行 → 完整全量 diff（上下文裁剪才有行可显示）
        var full = new List<DiffLine>(prefix + rev.Count + suffix);
        for (var i = 1; i <= prefix; i++)
            full.Add(new DiffLine(DiffLineType.Same, i, i, a[i - 1]));
        full.AddRange(rev);
        for (var i = 0; i < suffix; i++)
        {
            var oldNo = n - suffix + i + 1;
            full.Add(new DiffLine(DiffLineType.Same, oldNo, oldNo, a[oldNo - 1]));
        }
        return full;
    }

    // ───────────────────────────── 内部：unified hunk 裁剪 ─────────────────────────────

    /// <summary>把全量 diff 裁剪为 unified hunks（±Context 上下文，相邻块 gap ≤ 2×Context 合并）。</summary>
    private static List<DiffHunk> BuildHunks(IReadOnlyList<DiffLine> full, int ctx)
    {
        var changeStarts = new List<int>();
        for (var i = 0; i < full.Count; i++)
            if (full[i].Type != DiffLineType.Same)
                changeStarts.Add(i);

        // 每块 [s,e] 扩展上下文后合并重叠
        var ranges = new List<(int S, int E)>();
        foreach (var s in changeStarts)
        {
            var e = s;
            while (e + 1 < full.Count && full[e + 1].Type != DiffLineType.Same) e++;
            var rs = Math.Max(0, s - ctx);
            var re = Math.Min(full.Count - 1, e + ctx);
            if (ranges.Count > 0 && rs <= ranges[^1].E)
                ranges[^1] = (ranges[^1].S, re);
            else
                ranges.Add((rs, re));
        }

        var hunks = new List<DiffHunk>();
        foreach (var (s, e) in ranges)
        {
            var lines = new List<DiffLine>();
            int oldStart = 0, newStart = 0, oldCount = 0, newCount = 0;
            for (var i = s; i <= e; i++)
            {
                var ln = full[i];
                lines.Add(ln);
                if (ln.Type == DiffLineType.Same || ln.Type == DiffLineType.Remove)
                {
                    if (oldStart == 0) oldStart = ln.OldNo;
                    oldCount++;
                }
                if (ln.Type == DiffLineType.Same || ln.Type == DiffLineType.Add)
                {
                    if (newStart == 0) newStart = ln.NewNo;
                    newCount++;
                }
            }
            hunks.Add(new DiffHunk(oldStart, oldCount, newStart, newCount, lines));
        }
        return hunks;
    }

    private static IReadOnlyList<string> ReadLines(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return Array.Empty<string>();
        try { return File.ReadAllLines(path); }
        catch { return Array.Empty<string>(); }
    }
}

/// <summary>无头 diff 文本渲染（PlainText 通道；Spectre 交互态用 SpectreRenderer.DiffBlock 红绿渲染）。</summary>
public static class PlainDiffFormatter
{
    public static string Format(DiffResult result)
    {
        var sb = new System.Text.StringBuilder();
        if (result.TooLarge)
        {
            sb.AppendLine(result.TooLargeReason ?? "文件过大，diff 不支持");
            return sb.ToString().TrimEnd();
        }
        if (result.Hunks.Count == 0)
            return "无差异";
        foreach (var h in result.Hunks)
        {
            sb.AppendLine($"@@ -{h.OldStart},{h.OldCount} +{h.NewStart},{h.NewCount} @@");
            foreach (var ln in h.Lines)
            {
                var sign = ln.Type switch
                {
                    DiffLineType.Add => "+",
                    DiffLineType.Remove => "-",
                    _ => " "
                };
                var num = ln.Type switch
                {
                    DiffLineType.Add => ln.NewNo.ToString(),
                    DiffLineType.Remove => ln.OldNo.ToString(),
                    _ => $"{ln.OldNo} {ln.NewNo}"
                };
                sb.AppendLine($"{sign} {num}| {ln.Text}");
            }
        }
        return sb.ToString().TrimEnd();
    }
}
