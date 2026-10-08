using System.IO;
using System.Text.RegularExpressions;

namespace Agent.Infrastructure.Project;

/// <summary>
/// 双根保护·输入路径自动识别（"给路径就干活"）：CLI 层在任务执行前检测
/// 输入是否含"存在的绝对目录路径 + 读/分析语境（非写任务）"→ 自动只读 use。
/// 纯确定性静态函数，无 LLM、无副作用，tests 直接断言。
/// </summary>
public static class ProjectPathDetector
{
    // 读/分析语境词表（命中其一即视为"想了解这个路径"）
    private static readonly string[] ReadContextWords =
    {
        "读取", "分析", "看看", "查看", "了解", "项目", "工程", "结构",
        "代码", "内容", "说明", "入口", "依赖", "文件树", "扫描", "学习", "阅读", "检查", "介绍"
    };

    // 写任务词表（命中其一 → 不自动 use：路径是目标文件而非读目标；写优先与 IsWriteTask 对称）
    private static readonly string[] WriteContextWords =
    {
        "写", "创建", "生成", "修改", "更改", "重写", "新增", "删除", "实现",
        "复制", "移动", "保存", "输出", "打印", "替换", "追加", "覆盖", "建", "做", "修复", "重构"
    };

    // 分片标点（中文 + 半角）：路径与后续语境之间的天然分隔。
    // 注意：不含 ':' / '：'——盘符冒号是路径组成部分（X:\），作为分片符会把路径切开。
    private static readonly char[] SplitMarks = { '，', '。', '；', '、', '！', '？', ',', ';', '!', '?' };

    // 【10-08 会话写目标指代触发器】"这个文件/该文件/刚建的文件…" 等指代词 → 命中才继承会话写目标
    // （白名单延续 + 上下文注入 + 达成门三处由同一个开关驱动；未命中保持任务隔离）。
    private static readonly string[] ReferentialPronouns =
    {
        "这个文件", "该文件", "上述文件", "刚才那个文件", "刚建的文件", "新建的文件",
        "创建的文件", "刚才的文件", "这个脚本", "该脚本", "上述脚本", "这个程序",
        "刚才写的", "刚才创建的", "刚才新建的", "这个目录", "该目录", "这个文件夹", "这个路径"
    };

    /// <summary>
    /// 会话写目标指代检测（10-08）：输入含"这个文件 / 该文件 / 刚建的文件…"等指代词 → true。
    /// 只有命中才允许继承上一轮的显式写目标（避免无条件延续把旧目标一直挂在白名单/达成门里）。
    /// 确定性词表匹配，无 LLM。
    /// </summary>
    public static bool HasReferentialPronoun(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        foreach (var p in ReferentialPronouns)
            if (input.Contains(p, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>
    /// 从输入提取"存在的绝对目录路径"（首个命中）。
    /// 策略：按标点分片 → 取首个含盘符的片 → 定位盘符起点截取候选 →
    /// Directory.Exists 校验；失败且尾部 token 含 CJK（中文语境词）→ 剥最后空格段重试（≤3 次）。
    /// 纯字符 + 文件系统事实，无模糊匹配。
    /// </summary>
    public static bool TryExtractProjectPath(string input, out string path)
    {
        path = null!;
        if (string.IsNullOrWhiteSpace(input)) return false;

        foreach (var segment in input.Split(SplitMarks, StringSplitOptions.RemoveEmptyEntries))
        {
            var m = Regex.Match(segment, @"[A-Za-z]:\\");
            if (!m.Success) continue;

            var candidate = segment.Substring(m.Index);
            for (var i = 0; i <= 3; i++)
            {
                string full;
                try { full = Path.GetFullPath(candidate); }
                catch { break; }

                if (Directory.Exists(full))
                {
                    path = full;
                    return true;
                }

                var lastSpace = candidate.LastIndexOf(' ');
                if (lastSpace < 0) break;

                var tail = candidate.Substring(lastSpace + 1);
                // 仅当尾部 token 含 CJK（中文语境词）才剥——有效路径的空格段（如 PDF_RAG）不剥
                if (!ContainsCjk(tail)) break;

                candidate = candidate.Substring(0, lastSpace);
            }
        }
        return false;
    }

    /// <summary>读/分析语境命中（输入含 ReadContextWords 任一）。</summary>
    public static bool HasReadContext(string input) =>
        !string.IsNullOrWhiteSpace(input) &&
        ReadContextWords.Any(w => input.Contains(w, StringComparison.Ordinal));

    /// <summary>写任务特征命中（输入含 WriteContextWords 任一；写优先）。</summary>
    public static bool HasWriteContext(string input) =>
        !string.IsNullOrWhiteSpace(input) &&
        WriteContextWords.Any(w => input.Contains(w, StringComparison.Ordinal));

    /// <summary>
    /// 自动 use 判定：提取成功 && 非写任务 && 读语境 && 路径不敏感。
    /// 敏感区路径源头不 use（与 SensitivePathGuard 语义一致：内容不进 chatHistory）。
    /// </summary>
    public static bool TryGetAutoUsePath(string input, out string path)
    {
        path = null!;
        if (!TryExtractProjectPath(input, out path)) return false;
        if (HasWriteContext(input)) return false;
        if (!HasReadContext(input)) return false;
        if (SensitivePathGuard.IsDenied(path)) return false;
        return true;
    }

    // ─────────── 写目标提取（批次1：显式写授权 + 目标达成门共用） ───────────

    // 常见文本文件扩展名（自然语言文件名提取用；路径形态判定另靠 LooksLikeFile）
    private static readonly string[] KnownFileExtensions =
    {
        "txt", "py", "cs", "json", "md", "log", "xml", "html", "js", "ts",
        "java", "cpp", "c", "h", "ini", "yaml", "yml", "csv", "toml", "cfg", "sh", "bat", "ps1"
    };

    /// <summary>
    /// 从输入提取文件名（含扩展名，如 test.txt / main.py）。
    /// 确定性正则：已知扩展名 + ASCII 词边界（.NET \w 默认含 Unicode 中文，
    /// 用 ASCII 类避免"新建一下test.txt"被整串吞入；CJK 天然不在 ASCII 类内即边界）。
    /// </summary>
    public static bool TryExtractFileName(string input, out string name)
    {
        name = null!;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var ext = string.Join("|", KnownFileExtensions.Select(e => Regex.Escape(e)));
        var m = Regex.Match(input,
            $@"(?<![A-Za-z0-9_\\/.])[A-Za-z0-9_-]+\.(?:{ext})(?![A-Za-z0-9_\\/.])",
            RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        name = m.Value;
        return true;
    }

    /// <summary>
    /// 自然语言盘符解析："X盘根目录 / x盘 / X盘下 / X盘里" → "X:\"。
    /// 确定性正则，返回盘符根目录。
    /// </summary>
    public static bool TryExtractDiskRoot(string input, out string root)
    {
        root = null!;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var m = Regex.Match(input, @"([A-Za-z])\s*盘\s*(根目录|根|下|上|里|中)?");
        if (!m.Success) return false;
        root = $"{m.Groups[1].Value.ToUpperInvariant()}:\\";
        return true;
    }

    /// <summary>
    /// 写任务显式目标提取（批次1：显式写授权 + 目标达成门共用）。
    /// 两条通道：
    ///   通道1 完整绝对路径（文件：File.Exists 或最后段含 '.'；目录：Directory.Exists）；
    ///   通道2 自然语言盘符（"X盘根目录" + 可选文件名 → 文件级；无文件名 → 目录级）。
    /// 仅写语境触发；敏感区判定由调用方负责（CLI 授权 / CompletionGate 目标检查）。
    /// </summary>
    public static bool TryGetWriteTarget(string input, out string target, out bool isFile)
    {
        target = null!;
        isFile = false;
        if (string.IsNullOrWhiteSpace(input) || !HasWriteContext(input)) return false;

        // 通道 1：完整绝对路径（分片 + 盘符起点；目录/已存在文件/形似文件 → 文件级）
        foreach (var segment in input.Split(SplitMarks, StringSplitOptions.RemoveEmptyEntries))
        {
            var m = Regex.Match(segment, @"[A-Za-z]:\\");
            if (!m.Success) continue;

            var candidate = segment.Substring(m.Index);
            for (var i = 0; i <= 3; i++)
            {
                string full;
                try { full = Path.GetFullPath(candidate); }
                catch { break; }

                if (Directory.Exists(full)) { target = full; isFile = false; return true; }
                if (File.Exists(full)) { target = full; isFile = true; return true; }

                // 剥尾部 CJK 语境词（"new.log 文件"→"new.log"）后再判形似文件；
                // 尾部非 CJK（有效路径段）或无可剥 → 直接判文件形态
                var lastSpace = candidate.LastIndexOf(' ');
                if (lastSpace < 0)
                {
                    if (LooksLikeFile(full)) { target = full; isFile = true; return true; }
                    break;
                }
                var tail = candidate.Substring(lastSpace + 1);
                if (!ContainsCjk(tail))
                {
                    if (LooksLikeFile(full)) { target = full; isFile = true; return true; }
                    break;
                }
                candidate = candidate.Substring(0, lastSpace);
            }
        }

        // 通道 2：自然语言盘符（"X盘根目录"）
        if (TryExtractDiskRoot(input, out var root))
        {
            if (TryExtractFileName(input, out var name))
            {
                target = Path.GetFullPath(Path.Combine(root, name));
                isFile = true;
                return true;
            }
            target = root;
            isFile = false;
            return true;
        }

        return false;
    }

    /// <summary>路径最后一段含 '.' 视为文件形态（新建文件常不存在，靠形态识别）。</summary>
    private static bool LooksLikeFile(string fullPath)
    {
        var fileName = fullPath.Replace('\\', '/').Split('/').LastOrDefault();
        return !string.IsNullOrEmpty(fileName) && fileName.Contains('.') &&
               !fileName.EndsWith(".", StringComparison.Ordinal) &&
               !fileName.EndsWith("..", StringComparison.Ordinal);
    }

    private static bool ContainsCjk(string s)
    {
        foreach (var ch in s)
            if (ch is >= '\u4E00' and <= '\u9FFF') return true;
        return false;
    }
}
