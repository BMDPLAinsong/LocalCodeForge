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

    private static bool ContainsCjk(string s)
    {
        foreach (var ch in s)
            if (ch is >= '\u4E00' and <= '\u9FFF') return true;
        return false;
    }
}
