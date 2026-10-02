// ============================================================
// SymbolIndexer.cs + SymbolIndexStore.cs
// 工程图谱（符号级索引）：跨文件提取类型/方法/属性/函数定义，
// SQLite 持久化（复用签名缓存模式），供 find_symbol 确定性定位。
// 无 LLM、纯正则扫描，可测。
// ============================================================

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Agent.Infrastructure.Project;

/// <summary>符号条目：符号名 + 定义位置。</summary>
public sealed record SymbolEntry(string Symbol, string File, int Line, string Kind);

/// <summary>符号索引结果。</summary>
public sealed record SymbolIndexResult(int Count, string Message);

/// <summary>
/// 确定性符号索引器：扫描工程代码文件，提取类型/方法/属性/函数定义。
/// 上限：代码文件 ≤ 300、总大小 ≤ 8MB（超出跳过，防止大工程卡死）。
/// </summary>
public static class SymbolIndexer
{
    // 修饰符组可跨空格重复匹配（public static class → 命中），并排除控制流关键字误判。
    private static readonly Regex CSharpTypeRe = new(
        @"^\s*(?:(?:public|private|protected|internal|static|abstract|sealed|partial|readonly)\s+)*(class|interface|enum|record|struct)\s+([A-Za-z_]\w*)",
        RegexOptions.Compiled);
    private static readonly Regex CSharpMethodRe = new(
        @"^\s*(?:(?:public|private|protected|internal|static|async|virtual|override|abstract|sealed|partial)\s+)*[\w<>\[\],\?\s]*\s+([A-Za-z_]\w*)\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex CSharpPropRe = new(
        @"^\s*(?:(?:public|private|protected|internal|static|virtual|override|abstract|readonly)\s+)*[\w<>\[\]?\s]*\s+([A-Za-z_]\w*)\s*\{\s*(?:get|set)",
        RegexOptions.Compiled);
    /// <summary>控制流/语句关键字，方法正则命中后过滤（if/for/while 等误判）。</summary>
    private static readonly HashSet<string> NonMethodKeywords = new(StringComparer.Ordinal)
    {
        "if", "for", "while", "switch", "catch", "return", "using", "foreach", "lock", "throw", "new", "case", "else", "do"
    };
    private static readonly Regex PyClassRe = new(@"^\s*class\s+([A-Za-z_]\w*)", RegexOptions.Compiled);
    private static readonly Regex PyDefRe = new(@"^\s*def\s+([A-Za-z_]\w*)\s*\(", RegexOptions.Compiled);

    /// <summary>可索引的代码扩展名 → 解析风格。</summary>
    private static readonly Dictionary<string, string> CodeExts = new()
    {
        [".cs"] = "csharp", [".java"] = "csharp", [".go"] = "csharp",
        [".js"] = "csharp", [".ts"] = "csharp", [".py"] = "python"
    };

    /// <summary>
    /// 扫描工程根，返回符号条目列表。
    /// </summary>
    public static List<SymbolEntry> Build(string projectRoot)
    {
        var result = new List<SymbolEntry>();
        if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
            return result;

        var codeFiles = new List<string>();
        long totalBytes = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(projectRoot, "*.*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(projectRoot, f);
                // 排除生成/依赖目录
                if (rel.Contains(".git", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("bin", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("obj", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("node_modules", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("workspace", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith(".vs", StringComparison.OrdinalIgnoreCase))
                    continue;

                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (!CodeExts.ContainsKey(ext))
                    continue;

                try
                {
                    totalBytes += new FileInfo(f).Length;
                    if (totalBytes > 8 * 1024 * 1024)
                        return result;
                    codeFiles.Add(f);
                }
                catch { }
            }
        }
        catch { }

        if (codeFiles.Count > 300)
            return result;

        foreach (var f in codeFiles)
        {
            var rel = Path.GetRelativePath(projectRoot, f).Replace('\\', '/');
            var style = CodeExts[Path.GetExtension(f).ToLowerInvariant()];
            try
            {
                var lines = File.ReadAllText(f).Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].TrimEnd('\r');
                    if (style == "python")
                    {
                        var m = PyClassRe.Match(line);
                        if (m.Success)
                        {
                            result.Add(new SymbolEntry(m.Groups[1].Value, rel, i + 1, "class"));
                            continue;
                        }
                        m = PyDefRe.Match(line);
                        if (m.Success)
                        {
                            var indent = line.Length - line.TrimStart().Length;
                            result.Add(new SymbolEntry(m.Groups[1].Value, rel, i + 1,
                                indent > 0 ? "method" : "function"));
                        }
                    }
                    else
                    {
                        var m = CSharpTypeRe.Match(line);
                        if (m.Success)
                        {
                            result.Add(new SymbolEntry(m.Groups[2].Value, rel, i + 1, m.Groups[1].Value));
                            continue;
                        }
                        m = CSharpPropRe.Match(line);
                        if (m.Success)
                        {
                            result.Add(new SymbolEntry(m.Groups[1].Value, rel, i + 1, "property"));
                            continue;
                        }
                        m = CSharpMethodRe.Match(line);
                        if (m.Success && !NonMethodKeywords.Contains(m.Groups[1].Value))
                        {
                            result.Add(new SymbolEntry(m.Groups[1].Value, rel, i + 1, "method"));
                        }
                    }
                }
            }
            catch { }
        }

        return result;
    }
}

/// <summary>
/// 符号索引持久化（SQLite，按工程根键控）。
/// </summary>
public sealed class SymbolIndexStore
{
    private readonly string _connectionString;

    public SymbolIndexStore(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        Initialize();
    }

    private void Initialize()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS symbol_index (
                project_root TEXT NOT NULL,
                symbol       TEXT NOT NULL,
                file         TEXT NOT NULL,
                line         INTEGER NOT NULL,
                kind         TEXT NOT NULL,
                PRIMARY KEY (project_root, symbol, file, line)
            );
            CREATE INDEX IF NOT EXISTS idx_symbol ON symbol_index (project_root, symbol);
        """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>是否已有该工程的索引。</summary>
    public bool HasIndex(string projectRoot)
    {
        var key = Path.GetFullPath(projectRoot);
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM symbol_index WHERE project_root = $k;";
            cmd.Parameters.AddWithValue("$k", key);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }
        catch { return false; }
    }

    /// <summary>重建索引（先清后插）。返回符号数。</summary>
    public int BuildAndSave(string projectRoot)
    {
        var key = Path.GetFullPath(projectRoot);
        var entries = SymbolIndexer.Build(projectRoot);

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM symbol_index WHERE project_root = $k;";
        del.Parameters.AddWithValue("$k", key);
        del.ExecuteNonQuery();

        using var tx = conn.BeginTransaction();
        using var ins = conn.CreateCommand();
        ins.CommandText = """
            INSERT INTO symbol_index (project_root, symbol, file, line, kind)
            VALUES ($k, $s, $f, $l, $k2);
        """;
        var pk = ins.Parameters.Add("$k", SqliteType.Text);
        var ps = ins.Parameters.Add("$s", SqliteType.Text);
        var pf = ins.Parameters.Add("$f", SqliteType.Text);
        var pl = ins.Parameters.Add("$l", SqliteType.Integer);
        var pk2 = ins.Parameters.Add("$k2", SqliteType.Text);

        foreach (var e in entries)
        {
            pk.Value = key;
            ps.Value = e.Symbol;
            pf.Value = e.File;
            pl.Value = e.Line;
            pk2.Value = e.Kind;
            ins.ExecuteNonQuery();
        }
        tx.Commit();
        return entries.Count;
    }

    /// <summary>按符号查询定义位置。返回空列表表示无命中。</summary>
    public List<SymbolEntry> Find(string projectRoot, string symbol)
    {
        var key = Path.GetFullPath(projectRoot);
        var result = new List<SymbolEntry>();
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT symbol, file, line, kind FROM symbol_index
                WHERE project_root = $k AND symbol = $s
                ORDER BY file, line;
            """;
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$s", symbol);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new SymbolEntry(
                    reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3)));
            }
        }
        catch { }
        return result;
    }
}
