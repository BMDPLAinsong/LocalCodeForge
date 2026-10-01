// ============================================================
// ProjectContextStore.cs
// 工程上下文跨任务记忆（工程感知 P2）：
// 将 ProjectScanner.Scan 结果持久化到 SQLite（workspace/project_context.db），
// 并用内存缓存加速同进程内多任务复用。
// 缓存命中以文件系统签名一致为条件（FileCount/DirCount/TotalBytes/MaxMtimeTicks），
// 工程文件被修改 → 签名变化 → 自动重新扫描，保证正确性。
// 确定性 SQLite 操作，无模型调用。
// ============================================================

using Microsoft.Data.Sqlite;

namespace Agent.Infrastructure.Project;

/// <summary>
/// 工程上下文缓存（内存 + SQLite 双层）。
/// </summary>
public sealed class ProjectContextStore
{
    /// <summary>扫描器版本。ProjectScanner 逻辑变更时递增，缓存自动失效。</summary>
    public const string ScanVersion = "v1";

    private readonly string _connectionString;
    private readonly Dictionary<string, CacheEntry> _memory = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CacheEntry(
        string Overview,
        string ContextBlock,
        string CompactBlock,
        int FileCount,
        int DirCount,
        long TotalBytes,
        long MaxMtimeTicks,
        string ScannedAt);

    /// <summary>构造函数，接收数据库文件路径。</summary>
    public ProjectContextStore(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        Initialize();
    }

    /// <summary>建表（含旧表迁移：早期版本缺 compact_block 列）。</summary>
    private void Initialize()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS project_context (
                project_root     TEXT PRIMARY KEY,
                scan_version     TEXT NOT NULL,
                overview         TEXT NOT NULL,
                context_block    TEXT NOT NULL,
                compact_block    TEXT NOT NULL,
                file_count       INTEGER NOT NULL,
                dir_count        INTEGER NOT NULL,
                total_bytes      INTEGER NOT NULL,
                max_mtime_ticks  INTEGER NOT NULL,
                scanned_at       TEXT NOT NULL
            );
        """;
        cmd.ExecuteNonQuery();

        // 迁移：旧表（CREATE 时缺 compact_block 列）补列
        var hasCompact = false;
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(project_context);";
            using var reader = pragma.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetString(1) == "compact_block") hasCompact = true;
            }
        }
        if (!hasCompact)
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE project_context ADD COLUMN compact_block TEXT NOT NULL DEFAULT '';";
            alter.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 按工程根读取缓存。返回 null 表示未命中（需重新扫描）。
    /// 命中条件：存在记录且 scan_version 一致且文件系统签名一致。
    /// </summary>
    public ProjectScanner.ScanResult? TryGet(string projectRoot, int fileCount, int dirCount, long totalBytes, long maxMtimeTicks)
    {
        var key = Path.GetFullPath(projectRoot);

        // 1) 内存缓存
        if (_memory.TryGetValue(key, out var mem))
        {
            if (SignatureMatches(mem, fileCount, dirCount, totalBytes, maxMtimeTicks))
                return ToResult(mem);
        }

        // 2) SQLite 持久化
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT scan_version, overview, context_block, compact_block, file_count, dir_count, total_bytes, max_mtime_ticks, scanned_at
                FROM project_context WHERE project_root = @root;
            """;
            cmd.Parameters.AddWithValue("@root", key);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var ver = reader.GetString(0);
                if (ver != ScanVersion) return null;
                var entry = new CacheEntry(
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    reader.GetInt64(6),
                    reader.GetInt64(7),
                    reader.GetString(8));
                if (!SignatureMatches(entry, fileCount, dirCount, totalBytes, maxMtimeTicks))
                    return null;
                _memory[key] = entry; // 回填内存缓存
                return ToResult(entry);
            }
        }
        catch (SqliteException)
        {
            // DB 不可用时降级为未命中（调用方重新扫描，不阻塞）
        }
        return null;
    }

    /// <summary>写入缓存（内存 + SQLite）。</summary>
    public void Put(string projectRoot, ProjectScanner.ScanResult result)
    {
        var key = Path.GetFullPath(projectRoot);
        var entry = new CacheEntry(
            result.Overview,
            result.ContextBlock,
            result.CompactBlock,
            result.FileCount,
            result.DirCount,
            result.TotalBytes,
            result.MaxMtimeTicks,
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        _memory[key] = entry;

        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO project_context
                    (project_root, scan_version, overview, context_block, compact_block, file_count, dir_count, total_bytes, max_mtime_ticks, scanned_at)
                VALUES (@root, @ver, @overview, @block, @compact, @fc, @dc, @tb, @mtime, @at);
            """;
            cmd.Parameters.AddWithValue("@root", key);
            cmd.Parameters.AddWithValue("@ver", ScanVersion);
            cmd.Parameters.AddWithValue("@overview", result.Overview);
            cmd.Parameters.AddWithValue("@block", result.ContextBlock);
            cmd.Parameters.AddWithValue("@compact", result.CompactBlock);
            cmd.Parameters.AddWithValue("@fc", result.FileCount);
            cmd.Parameters.AddWithValue("@dc", result.DirCount);
            cmd.Parameters.AddWithValue("@tb", result.TotalBytes);
            cmd.Parameters.AddWithValue("@mtime", result.MaxMtimeTicks);
            cmd.Parameters.AddWithValue("@at", entry.ScannedAt);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // DB 不可用不阻塞任务
        }
    }

    private static bool SignatureMatches(CacheEntry entry, int fileCount, int dirCount, long totalBytes, long maxMtimeTicks)
        => entry.FileCount == fileCount
        && entry.DirCount == dirCount
        && entry.TotalBytes == totalBytes
        && entry.MaxMtimeTicks == maxMtimeTicks;

    private static ProjectScanner.ScanResult ToResult(CacheEntry e)
        => new(e.Overview, e.ContextBlock, e.CompactBlock, e.FileCount, e.DirCount, e.TotalBytes, e.MaxMtimeTicks);
}
