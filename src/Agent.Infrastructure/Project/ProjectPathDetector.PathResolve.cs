using System.IO;

namespace Agent.Infrastructure.Project;

// 【10-14 P5-23】ProjectPathDetector 的"路径清洗与绝对路径解析"域（partial，见主文件类注释）。
public static partial class ProjectPathDetector
{
    // ─────────── 路径清洗与绝对路径解析 ───────────

    /// <summary>
    /// 【10-12 X2】剥离路径串中的中文方位/类别词（"x:\盘下index.html文件" → "x:\index.html"）。
    /// 自然语言里"盘下/文件"常紧贴路径（"读取x:\盘下index.html文件，并修复…"），
    /// 不清洗会被当作路径成分 → 写目标/注入路径变脏（实测"盘下"误判进显式授权白名单）。
    /// 三步：① 盘根方位词正则归一（"x:\ 盘下 index.html" 空格分隔形态 → "x:\index.html"——
    ///   "盘下"直接替换会留双空格，须连同根后空白一起规整）；② 剥尾部类别词
    ///   （"文件/目录/文件夹"，循环长词优先）；③ 剥任意位置方位词（"盘下/根目录下/目录下"）。
    /// 词表为精确中文词：不剥独立"下"（避免误伤"X:\下载"这类真实目录名）。
    /// </summary>
    public static string CleanPathSuffix(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        // ① 盘根 + 方位词 + 任意空白 → 盘根（"x:\ 盘下 index.html" → "x:\index.html"）
        s = System.Text.RegularExpressions.Regex.Replace(
            s, @"^([A-Za-z]:\\)\s*(根目录下|目录下|盘下)\s*", "$1");
        // ② 剥尾部类别词
        var tailWords = new[] { "文件目录", "文件夹", "文件", "目录" };
        var prev = "";
        while (prev != s)
        {
            prev = s;
            foreach (var w in tailWords)
                if (s.EndsWith(w, StringComparison.Ordinal)) { s = s[..^w.Length]; break; }
        }
        // ③ 剥任意位置方位词（连相邻空白一起规整："\盘下 target.html" → "\target.html"，
        //   "x:\ 盘下 index.html" 盘根开头形态已由 ① 归一，此处兜底子目录/其他位置）
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*(根目录下|目录下|盘下)\s*", "");
        return s.TrimEnd();
    }

    /// <summary>
    /// 【10-12 X1】从输入解析"存在的文件绝对路径"（优先于文件名搜索注入）。
    /// 输入已含盘符绝对路径（"读取x:\盘下index.html文件…"）→ 正则提取候选 → 清洗方位/类别词 →
    /// File.Exists 校验 → 命中返回干净绝对路径。纯字符 + 文件系统事实，无模糊匹配。
    /// </summary>
    public static bool TryResolveAbsolutePath(string input, out string path)
        => Resolve(input, out path, file: true);

    /// <summary>
    /// 【10-12 B2】与 TryResolveAbsolutePath 对称的目录解析：输入含"存在的目录绝对路径"→ 命中。
    /// use 一步到位时区分文件/目录：修单文件走轻量通道（不挂工程根），分析目录才 use 该目录。
    /// 同一候选提取 + 最长存在前缀剥段逻辑，杜绝双通道漂移。
    /// </summary>
    public static bool TryResolveAbsoluteDirectory(string input, out string path)
        => Resolve(input, out path, file: false);

    private static bool Resolve(string input, out string path, bool file)
    {
        path = null!;
        if (string.IsNullOrWhiteSpace(input)) return false;
        // 【10-12 A】候选含空格（原正则排除 \s → 空格分隔形态"x:\ 盘下 index.html"截断在盘根，File.Exists(目录)=false）；
        // 截断点为中文标点/行尾。
        var m = System.Text.RegularExpressions.Regex.Match(input, @"[A-Za-z]:\\[^\r\n，。；、！？,;!?]*");
        if (!m.Success) return false;
        var candidate = CleanPathSuffix(m.Value);
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        try
        {
            // 最长存在前缀：候选含尾部自然语言（"readme.md 的内容"）时逐段剥尾部空格段重试；
            // 路径内空格（"D:\My Projects"）不受影响——只剥最后一个空格段，中间空格保留。
            var cand = candidate;
            while (cand.Length > 3)
            {
                var full = Path.GetFullPath(cand);
                var hit = file ? File.Exists(full) : Directory.Exists(full);
                if (hit) { path = full; return true; }
                var sp = cand.LastIndexOf(' ');
                if (sp <= 2) break;
                cand = cand[..sp].TrimEnd();
            }
        }
        catch { }
        return false;
    }
}
