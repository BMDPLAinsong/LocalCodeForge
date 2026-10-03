// ============================================================
// DependencyAnalyzer.cs
// 工程图谱增强：符号引用 → 文件依赖边（确定性、纯正则、无 LLM）。
// 输入符号索引（SymbolIndexer.Build）+ 工程根，扫描代码文本中
// 已索引【类型级符号】的出现（词边界）→ 若符号定义在文件 B 且
// 当前文件 A 引用了它 → 依赖边 A→B。
// 用途：多文件规划（改 A 会影响谁 / 新文件应放哪）+ analyze_deps 工具。
// 注：粗粒度（注释/字符串内的符号出现也会计入，仅供参考方向）。
// ============================================================

using System.Text.RegularExpressions;

namespace Agent.Infrastructure.Project;

/// <summary>依赖边：文件 File 引用了 DependsOnFile 中定义的符号 Symbol。</summary>
public sealed record DependencyEdge(string File, string DependsOnFile, string Symbol);

/// <summary>
/// 确定性依赖分析器：符号引用 → 文件依赖边 + 正/反向查询。
/// </summary>
public static class DependencyAnalyzer
{
    /// <summary>参与依赖计算的符号类型（类型级，避免方法名/属性名跨文件误报）。</summary>
    private static readonly HashSet<string> TypeKinds = new(StringComparer.Ordinal)
    {
        "class", "interface", "enum", "record", "struct", "trait", "type"
    };

    private const int MaxTypeSymbols = 600;

    /// <summary>
    /// 构建工程依赖边。
    /// </summary>
    public static List<DependencyEdge> Build(string projectRoot, List<SymbolEntry> symbols)
    {
        var result = new List<DependencyEdge>();
        if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot) || symbols.Count == 0)
            return result;

        // 类型级符号 → 定义文件（多文件同名取首个；C# 允许 partial 分布多文件）
        var typeDefs = symbols
            .Where(s => TypeKinds.Contains(s.Kind))
            .GroupBy(s => s.Symbol)
            .Where(g => g.Count() <= 8)   // 同名符号过多（如泛型重载）跳过，防噪音
            .ToDictionary(g => g.Key, g => g.Select(s => Normalize(s.File)).Distinct().ToList());

        if (typeDefs.Count == 0 || typeDefs.Count > MaxTypeSymbols)
            return result;

        // 扫描代码文件，词边界匹配类型符号
        foreach (var file in EnumerateCodeFiles(projectRoot))
        {
            string content;
            try
            {
                content = File.ReadAllText(file);
            }
            catch
            {
                continue;
            }
            if (content.Length > 8 * 1024 * 1024)
                continue;

            var rel = Normalize(Path.GetRelativePath(projectRoot, file));
            foreach (var (symbol, defFiles) in typeDefs)
            {
                var target = defFiles.FirstOrDefault(d => !d.Equals(rel, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                    continue; // 符号只在本文件定义（或无跨文件定义）

                // 词边界匹配（大小写敏感，避免子串误报）
                var re = new Regex(@"(?<![\w])" + Regex.Escape(symbol) + @"(?![\w])", RegexOptions.Compiled);
                if (re.IsMatch(content))
                {
                    result.Add(new DependencyEdge(rel, target, symbol));
                }
            }
        }

        return result;
    }

    /// <summary>规范化相对路径（统一斜杠 + 小写比较用）。</summary>
    private static string Normalize(string path)
        => path.Replace('\\', '/');

    private static IEnumerable<string> EnumerateCodeFiles(string projectRoot)
    {
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".py", ".js", ".ts", ".java", ".go"
        };
        var list = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(projectRoot, "*.*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(projectRoot, f);
                if (rel.Contains(".git", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("bin", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("obj", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("node_modules", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("workspace", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith(".vs", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (exts.Contains(Path.GetExtension(f)) && list.Count < 300)
                    list.Add(f);
            }
        }
        catch { }
        return list;
    }

    /// <summary>某文件直接依赖的文件（它引用了谁）。</summary>
    public static List<string> GetDirectDependencies(string file, List<DependencyEdge> edges)
    {
        var f = Normalize(file);
        return edges
            .Where(e => e.File.Equals(f, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.DependsOnFile)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>某文件的反向依赖（谁引用了它——改动影响面）。</summary>
    public static List<string> GetReverseDependencies(string file, List<DependencyEdge> edges)
    {
        var f = Normalize(file);
        return edges
            .Where(e => e.DependsOnFile.Equals(f, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.File)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
