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

/// <summary>符号条目：符号名 + 定义位置 + 定义行原文（签名，截到首个 '{' 前）。</summary>
public sealed record SymbolEntry(string Symbol, string File, int Line, string Kind, string Signature = "");

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

    /// <summary>可索引的代码扩展名 → 解析风格（键集与 FileGrammar.CodeExtensions 同源；
    /// 枚举过滤走 FileGrammar，此处仅保留"扩展名→解析风格"映射）。</summary>
    private static readonly Dictionary<string, string> CodeExts = new()
    {
        [".cs"] = "csharp", [".java"] = "csharp", [".go"] = "csharp",
        [".js"] = "csharp", [".ts"] = "csharp", [".py"] = "python"
    };

    /// <summary>
    /// 【P1-1】枚举工程代码文件。返回全路径 + 相对路径 + mtime（增量索引对比用）。
    /// 【10-14 P1-4】过滤单源化：排除目录/扩展名走 FileGrammar（段级精确匹配，任意层级——
    /// 原顶层 StartsWith 复制有 "binary" 前缀误伤与 ".git" 子串误伤）；本方法只保留
    /// SymbolIndexer 特有上限策略：8MB 总量上限（超出截断）+ 300 文件上限（超出清空）。
    /// </summary>
    public static List<(string Full, string Rel, string Ext, long LastWriteTicks)> EnumerateFiles(string projectRoot)
    {
        var files = new List<(string, string, string, long)>();
        if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
            return files;

        long totalBytes = 0;
        foreach (var (full, rel, ext) in FileGrammar.EnumerateCodeFiles(projectRoot))
        {
            try
            {
                var fi = new FileInfo(full);
                totalBytes += fi.Length;
                if (totalBytes > 8 * 1024 * 1024)
                    return files;
                files.Add((full, rel, ext, fi.LastWriteTimeUtc.Ticks));
            }
            catch { }
        }

        if (files.Count > 300)
            return new List<(string, string, string, long)>();
        return files;
    }

    /// <summary>
    /// 定义行原文 → 签名（截到首个 '{' 前，防方法体/属性体污染；跨行声明无 '{' 则原样）。
    /// 【10-15 清单增强】确定性字符串裁剪，非解析——数据保真，零解析风险。
    /// </summary>
    private static string ToSignature(string line)
    {
        var b = line.Trim();
        var brace = b.IndexOf('{');
        return brace >= 0 ? b[..brace].TrimEnd() : b;
    }

    /// <summary>扫描单个代码文件（供增量重建复用）。rel 为相对工程根路径（/ 分隔）。</summary>
    public static List<SymbolEntry> BuildFile(string fullPath, string rel)
    {
        try
        {
            var style = CodeExts.GetValueOrDefault(Path.GetExtension(fullPath).ToLowerInvariant());
            return style is null ? new List<SymbolEntry>() : BuildContent(File.ReadAllText(fullPath), rel, style);
        }
        catch { return new List<SymbolEntry>(); }
    }

    /// <summary>【10-14 C3】从内存内容提取符号——与 BuildFile **同一实现**（单一提取源，避免第二份正则）。
    /// style 为 null 时按 rel 扩展名推断。供"已有类型清单"等内存态内容复用。</summary>
    public static List<SymbolEntry> BuildContent(string content, string rel, string? style = null)
    {
        style ??= CodeExts.GetValueOrDefault(Path.GetExtension(rel).ToLowerInvariant());
        var result = new List<SymbolEntry>();
        if (style is null || string.IsNullOrEmpty(content)) return result;

        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (style == "python")
            {
                var m = PyClassRe.Match(line);
                if (m.Success)
                {
                    result.Add(new SymbolEntry(m.Groups[1].Value, rel, i + 1, "class", ToSignature(line)));
                    continue;
                }
                m = PyDefRe.Match(line);
                if (m.Success)
                {
                    var indent = line.Length - line.TrimStart().Length;
                    result.Add(new SymbolEntry(m.Groups[1].Value, rel, i + 1,
                        indent > 0 ? "method" : "function", ToSignature(line)));
                }
            }
            else
            {
                var m = CSharpTypeRe.Match(line);
                if (m.Success)
                {
                    result.Add(new SymbolEntry(m.Groups[2].Value, rel, i + 1, m.Groups[1].Value, ToSignature(line)));
                    continue;
                }
                m = CSharpPropRe.Match(line);
                if (m.Success)
                {
                    result.Add(new SymbolEntry(m.Groups[1].Value, rel, i + 1, "property", ToSignature(line)));
                    continue;
                }
                m = CSharpMethodRe.Match(line);
                if (m.Success && !NonMethodKeywords.Contains(m.Groups[1].Value))
                {
                    result.Add(new SymbolEntry(m.Groups[1].Value, rel, i + 1, "method", ToSignature(line)));
                }
            }
        }
        return result;
    }

    public static List<SymbolEntry> Build(string projectRoot)
    {
        var result = new List<SymbolEntry>();
        foreach (var (full, rel, _, _) in EnumerateFiles(projectRoot))
        {
            result.AddRange(BuildFile(full, rel.Replace('\\', '/')));
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
                signature    TEXT NOT NULL DEFAULT '',
                file_mtime   INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (project_root, symbol, file, line)
            );
            CREATE INDEX IF NOT EXISTS idx_symbol ON symbol_index (project_root, symbol);
        """;
        cmd.ExecuteNonQuery();
        // 【P1-1】旧库迁移：补充 file_mtime 列（不存在则 ALTER ADD）。
        using var migrate = conn.CreateCommand();
        migrate.CommandText = "SELECT COUNT(*) FROM pragma_table_info('symbol_index') WHERE name='file_mtime';";
        if (Convert.ToInt64(migrate.ExecuteScalar()) == 0)
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE symbol_index ADD COLUMN file_mtime INTEGER NOT NULL DEFAULT 0;";
            alter.ExecuteNonQuery();
        }
        // 【10-15 清单增强】旧库迁移：补充 signature 列（不存在则 ALTER ADD）。
        using var migrateSig = conn.CreateCommand();
        migrateSig.CommandText = "SELECT COUNT(*) FROM pragma_table_info('symbol_index') WHERE name='signature';";
        if (Convert.ToInt64(migrateSig.ExecuteScalar()) == 0)
        {
            using var alterSig = conn.CreateCommand();
            alterSig.CommandText = "ALTER TABLE symbol_index ADD COLUMN signature TEXT NOT NULL DEFAULT '';";
            alterSig.ExecuteNonQuery();
        }
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

    /// <summary>重建索引（先清后插）。返回符号数。全量构建也存文件 mtime（增量对比基准）。</summary>
    public int BuildAndSave(string projectRoot)
    {
        var key = Path.GetFullPath(projectRoot);
        // 逐文件扫描（带 mtime）：保证全量与增量使用同一 mtime 基准
        var fileEntries = new List<(SymbolEntry E, long Mtime)>();
        foreach (var (full, rel, _, mtime) in SymbolIndexer.EnumerateFiles(projectRoot))
        {
            var relPath = rel.Replace('\\', '/');
            foreach (var e in SymbolIndexer.BuildFile(full, relPath))
                fileEntries.Add((e, mtime));
        }

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM symbol_index WHERE project_root = $k;";
            del.Parameters.AddWithValue("$k", key);
            del.ExecuteNonQuery();
        }

        // 【10-14 P2-5 批量插入】单事务内按批 multi-VALUES 插入（每批 ≤100 行 = 600 参数，远低于
        // SQLite 变量上限）——原逐行 ExecuteNonQuery 对 300 文件工程持锁上千次。
        using (var tx = conn.BeginTransaction())
        {
            InsertBatch(conn, tx, key, fileEntries);
            tx.Commit();
        }
        return fileEntries.Count;
    }

    /// <summary>
    /// 【P1-1】增量重建：对比磁盘文件 mtime 与索引记录，仅重扫新增/变更文件；
    /// 删除磁盘上已不存在的文件记录。返回 (总符号数, 重扫文件数)。
    /// 文件级增量（依赖边每次由 GetAll 全量计算，无需增量）。
    /// </summary>
    public (int Total, int Rescanned) BuildAndSaveIncremental(string projectRoot)
    {
        var key = Path.GetFullPath(projectRoot);
        var diskFiles = SymbolIndexer.EnumerateFiles(projectRoot);
        // 空枚举（工程过大/无代码文件）→ 全量清空该工程索引
        if (diskFiles.Count == 0)
        {
            DeleteProject(key);
            return (0, 0);
        }

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        // 读现有 (file → mtime)
        var existing = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT file, file_mtime FROM symbol_index WHERE project_root = $k;";
            read.Parameters.AddWithValue("$k", key);
            using var reader = read.ExecuteReader();
            while (reader.Read())
                existing[reader.GetString(0)] = reader.GetInt64(1);
        }

        var rescanned = 0;
        using var tx = conn.BeginTransaction();
        foreach (var (full, rel, _, mtime) in diskFiles)
        {
            var relPath = rel.Replace('\\', '/');
            var changed = !existing.TryGetValue(relPath, out var oldMtime) || oldMtime != mtime;
            if (!changed) continue;

            // 重扫该文件：删旧行 + 插新行
            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM symbol_index WHERE project_root = $k AND file = $f;";
                del.Parameters.AddWithValue("$k", key);
                del.Parameters.AddWithValue("$f", relPath);
                del.ExecuteNonQuery();
            }

            var entries = SymbolIndexer.BuildFile(full, relPath);
            if (entries.Count > 0)
            {
                // 【10-14 P2-5】按文件批插（单文件符号数通常 <100 → 单次 ExecuteNonQuery）
                var rows = new List<(SymbolEntry, long)>(entries.Count);
                foreach (var e in entries)
                    rows.Add((e, mtime));
                InsertBatch(conn, tx, key, rows);
            }
            rescanned++;
        }

        // 删除磁盘已不存在的文件记录
        var diskSet = diskFiles.Select(f => f.Rel.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = existing.Keys.Where(f => !diskSet.Contains(f)).ToList();
        foreach (var f in stale)
        {
            using var del = conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM symbol_index WHERE project_root = $k AND file = $f;";
            del.Parameters.AddWithValue("$k", key);
            del.Parameters.AddWithValue("$f", f);
            del.ExecuteNonQuery();
        }

        tx.Commit();

        long total = 0;
        using (var cnt = conn.CreateCommand())
        {
            cnt.CommandText = "SELECT COUNT(*) FROM symbol_index WHERE project_root = $k;";
            cnt.Parameters.AddWithValue("$k", key);
            total = Convert.ToInt64(cnt.ExecuteScalar());
        }
        return ((int)total, rescanned);
    }

    /// <summary>
    /// 【10-14 P2-5】批量插入符号行：按批拼 multi-VALUES INSERT，单次 ExecuteNonQuery。
    /// 分批 100 行 = 700 参数（SQLite 变量上限保守取值）；全量与增量两条路径共用此实现。
    /// </summary>
    private static void InsertBatch(SqliteConnection conn, SqliteTransaction tx, string key,
        IReadOnlyList<(SymbolEntry Entry, long Mtime)> rows)
    {
        const int batchSize = 100;
        for (var offset = 0; offset < rows.Count; offset += batchSize)
        {
            var take = Math.Min(batchSize, rows.Count - offset);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            var sb = new System.Text.StringBuilder(
                "INSERT INTO symbol_index (project_root, symbol, file, line, kind, signature, file_mtime) VALUES ");
            for (var i = 0; i < take; i++)
            {
                if (i > 0) sb.Append(',');
                var b = i * 7;
                sb.Append($"($k{b}, $s{b}, $f{b}, $l{b}, $k2{b}, $g{b}, $m{b})");
            }
            cmd.CommandText = sb.ToString();
            for (var i = 0; i < take; i++)
            {
                var (e, mtime) = rows[offset + i];
                var b = i * 7;
                cmd.Parameters.AddWithValue($"$k{b}", key);
                cmd.Parameters.AddWithValue($"$s{b}", e.Symbol);
                cmd.Parameters.AddWithValue($"$f{b}", e.File);
                cmd.Parameters.AddWithValue($"$l{b}", e.Line);
                cmd.Parameters.AddWithValue($"$k2{b}", e.Kind);
                cmd.Parameters.AddWithValue($"$g{b}", e.Signature);
                cmd.Parameters.AddWithValue($"$m{b}", mtime);
            }
            cmd.ExecuteNonQuery();
        }
    }

    private void DeleteProject(string key)
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM symbol_index WHERE project_root = $k;";
            del.Parameters.AddWithValue("$k", key);
            del.ExecuteNonQuery();
        }
        catch { }
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
                SELECT symbol, file, line, kind, signature FROM symbol_index
                WHERE project_root = $k AND symbol = $s
                ORDER BY file, line;
            """;
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$s", symbol);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new SymbolEntry(
                    reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
                    reader.GetString(4)));
            }
        }
        catch { }
        return result;
    }

    /// <summary>读取某工程的全部索引符号（供依赖分析等跨文件计算）。无索引返回空。</summary>
    public List<SymbolEntry> GetAll(string projectRoot)
    {
        var key = Path.GetFullPath(projectRoot);
        var result = new List<SymbolEntry>();
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT symbol, file, line, kind, signature FROM symbol_index
                WHERE project_root = $k
                ORDER BY file, line;
            """;
            cmd.Parameters.AddWithValue("$k", key);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new SymbolEntry(
                    reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
                    reader.GetString(4)));
            }
        }
        catch { }
        return result;
    }
}
