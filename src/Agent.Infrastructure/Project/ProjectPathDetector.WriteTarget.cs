using System.IO;
using System.Text.RegularExpressions;

namespace Agent.Infrastructure.Project;

// 【10-14 P5-23】ProjectPathDetector 的"写目标提取"域（partial，见主文件类注释）：
// 显式写授权（CLI）与目标达成门（CompletionGate）共用的写目标/文件名提取。
public static partial class ProjectPathDetector
{
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
    /// 【10-14 P1-4】实现归位 FileGrammar.TryExtractChineseDiskRoot（单源），此处保留公共 API 委托。
    /// </summary>
    public static bool TryExtractDiskRoot(string input, out string root)
        => FileGrammar.TryExtractChineseDiskRoot(input, out root);

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

            var candidate = CleanPathSuffix(segment.Substring(m.Index));   // 【10-12 X2】先清洗方位/类别词，再判存在/形态（防"盘下"脏路径）
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
}
