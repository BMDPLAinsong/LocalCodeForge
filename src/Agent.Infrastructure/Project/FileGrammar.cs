// ============================================================
// FileGrammar.cs
// 文件系统语法单源（10-14 P1-4，架构审查任务5-A1/A5 + 任务8-C13）：
//   · ExcludedDirNames —— 工程扫描排除目录（段名精确匹配，任意层级）。
//     此前【四处】独立维护：ProjectScanner.ExcludedDirs（最全）+ SymbolIndexer /
//     DependencyAnalyzer / Orchestrator 三份行级复制（仅顶层 StartsWith，且 "binary"
//     前缀误伤、".git" 子串误伤同类名）。改一处漏三处的温床——收敛为唯一权威表。
//   · CodeExtensions —— 代码文件扩展名（原 SymbolIndexer.CodeExts 键集、
//     DependencyAnalyzer 内联集合、Orchestrator 内联集合三份归一；解析风格映射仍归 SymbolIndexer）。
//   · ProjectManifestExtensions —— 工程清单扩展名（ProjectScanner.DepExtensions 与
//     Orchestrator.EnumerateExistingFiles 两份归一）。
//   · EnumerateCodeFiles —— 工程代码文件惰性枚举（排除 + 扩展过滤，IO 容忍）。
//     文件数/字节上限策略由调用方施加（SymbolIndexer 8MB+300 清空、
//     DependencyAnalyzer 取前 300）——语法归一，策略各留。
//   · TryExtractChineseDiskRoot / TryExtractLiteralDiskRoot —— 盘符两种语法
//     （"X盘根目录"自然语言 / "X:\" 字面 + 存在性校验），自 ProjectPathDetector /
//     UseCommandParser 归位（两语义不同，各自保留——归位非合并）。
//
// 【故意不合并的清单（各有独立用途，勿当遗漏再"归一"）】
//   · ProjectScanner.CoreFileLines.sourceExts（13 项含 .rs/.cpp/.vue/.razor）——上下文注入的
//     "大源码文件"体积排序榜，范围比索引更宽（.rs/.cpp 不入符号索引）。
//   · ProjectPathDetector.KnownFileExtensions（24 项无点前缀）——自然语言文件名提取词表
//     （"新建一下test.txt" 的 txt 属文本文件而非代码文件）。
//   · DashboardHost.TextExts（31 项）——产物预览"可当文本打开"的展示面清单。
//
// 无 LLM、纯文件系统/正则，可测。
// ============================================================

using System.Text.RegularExpressions;

namespace Agent.Infrastructure.Project;

/// <summary>文件系统语法单源：排除目录 / 代码扩展名 / 代码文件枚举 / 盘符路径提取。</summary>
public static class FileGrammar
{
    /// <summary>工程扫描排除目录（构建产物/版本库/依赖；段名精确匹配，任意层级）。
    /// 含盘根扫描遇系统隐藏目录（$RECYCLE.BIN 等无权限/无意义）。
    /// 原 ProjectScanner.ExcludedDirs 提升为全层共享单源。</summary>
    public static readonly HashSet<string> ExcludedDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "obj", "bin", ".git", ".vs", ".vscode", ".idea",
        "node_modules", "dist", "build", "packages", "__pycache__", ".venv", "venv", "workspace",
        "$recycle.bin", "system volume information"
    };

    /// <summary>代码文件扩展名（符号索引/依赖分析/规划已有文件清单共用）。
    /// 注意：解析风格映射（csharp/python）归 SymbolIndexer.CodeExts，此表只管"是否代码文件"。</summary>
    public static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".py", ".js", ".ts", ".java", ".go"
    };

    /// <summary>工程清单扩展名（工程结构锚：.sln/.csproj 等，读这些能了解技术栈）。
    /// 【10-14 P1-4 复核】原 ProjectScanner.DepExtensions 与 Orchestrator.EnumerateExistingFiles 各一份。</summary>
    public static readonly HashSet<string> ProjectManifestExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj", ".sln", ".slnx", ".fsproj", ".vbproj"
    };

    /// <summary>相对路径是否命中排除目录（任一路径段名 ∈ ExcludedDirNames）。</summary>
    public static bool IsExcludedRel(string rel)
    {
        if (string.IsNullOrEmpty(rel)) return false;
        foreach (var seg in rel.Split('\\', '/'))
            if (ExcludedDirNames.Contains(seg))
                return true;
        return false;
    }

    /// <summary>
    /// 惰性枚举工程代码文件（排除目录 + CodeExtensions），返回 (全路径, 相对路径, 小写扩展名)。
    /// IO 异常容忍：枚举中途失败即截断（与原 SymbolIndexer / DependencyAnalyzer 行为一致）。
    /// 上限策略（文件数/总字节）由调用方施加——本方法只负责语法过滤。
    /// </summary>
    public static IEnumerable<(string Full, string Rel, string Ext)> EnumerateCodeFiles(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
            yield break;

        IEnumerator<string> it;
        try
        {
            it = Directory.EnumerateFiles(projectRoot, "*.*", SearchOption.AllDirectories).GetEnumerator();
        }
        catch
        {
            yield break;
        }

        using (it)
        {
            while (true)
            {
                string f;
                try
                {
                    if (!it.MoveNext()) break;
                    f = it.Current;
                }
                catch
                {
                    break;   // 枚举中途 IO 异常：容忍截断（与原实现一致）
                }

                string rel;
                try { rel = Path.GetRelativePath(projectRoot, f); }
                catch { continue; }

                if (IsExcludedRel(rel)) continue;

                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (!CodeExtensions.Contains(ext)) continue;

                yield return (f, rel, ext);
            }
        }
    }

    /// <summary>
    /// 自然语言盘符："X盘 / X盘根目录 / X盘下 / X盘里" → "X:\"（不校验存在性）。
    /// 原 ProjectPathDetector.TryExtractDiskRoot 实现归位（写目标通道2 等使用）。
    /// </summary>
    public static bool TryExtractChineseDiskRoot(string input, out string root)
    {
        root = null!;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var m = Regex.Match(input, @"([A-Za-z])\s*盘\s*(根目录|根|下|上|里|中)?");
        if (!m.Success) return false;
        root = $"{m.Groups[1].Value.ToUpperInvariant()}:\\";
        return true;
    }

    /// <summary>
    /// 字面盘符："X:\" / "X:" → "X:\"（带存在性校验；"X:" 归一为 "X:\" 防盘符相对语义）。
    /// 原 UseCommandParser.TryExtractDiskRoot 实现归位（use 参数非路径开头兜底）。
    /// </summary>
    public static bool TryExtractLiteralDiskRoot(string input, out string root)
    {
        root = "";
        if (string.IsNullOrWhiteSpace(input)) return false;
        var m = Regex.Match(input, @"[A-Za-z]:\\?");
        if (!m.Success) return false;
        var r = m.Value;
        if (r.Length == 2) r += "\\";          // "X:" → "X:\"（盘符相对语义防御）
        if (!Directory.Exists(r)) return false;
        root = r;
        return true;
    }
}
