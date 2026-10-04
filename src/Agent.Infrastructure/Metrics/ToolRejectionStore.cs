// ============================================================
// ToolRejectionStore.cs
// 【C】工具拒绝事件落库（tool_rejections 表）：WriteFileTool / EditFileTool 每次拒绝
// （P-D 类型冲突 / 工程只读 / 项目文件 / 越界 / 空内容 / 交互 / 未找到等）记一条事件。
// 补「工具层拦截不可观测」度量盲区：error_summary 看不到工具级拒绝，
// 导致"3 连拒熔断""模型反复重试"类失败无法归因。
// 纯数据层，失败静默（记录失败不影响工具主流程）。
// ============================================================

using Microsoft.Data.Sqlite;

namespace Agent.Infrastructure.Metrics;

/// <summary>工具拒绝事件聚合摘要（供 stats 展示）。</summary>
public sealed record ToolRejectionSummary(
    int Total,
    IReadOnlyList<(string Category, int Count)> ByCategory,
    IReadOnlyList<(string Tool, int Count)> ByTool);

/// <summary>工具拒绝事件落库存储。</summary>
public sealed class ToolRejectionStore
{
    /// <summary>message 截断长度。</summary>
    public const int MaxMessageLength = 200;

    private readonly string _connectionString;

    public ToolRejectionStore(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";

        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        Initialize();
    }

    /// <summary>建表 + 索引。</summary>
    private void Initialize()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS tool_rejections (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    tool        TEXT NOT NULL,
                    category    TEXT NOT NULL,
                    path        TEXT NOT NULL DEFAULT '',
                    task_id     TEXT NOT NULL DEFAULT '',
                    message     TEXT NOT NULL DEFAULT '',
                    created_at  INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_tr_created  ON tool_rejections(created_at DESC);
                CREATE INDEX IF NOT EXISTS idx_tr_category ON tool_rejections(category);
            """;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>记录一次工具拒绝事件（失败静默，不影响工具主流程）。</summary>
    public async Task RecordAsync(
        string tool,
        string category,
        string path,
        string taskId,
        string message,
        CancellationToken ct)
    {
        var msg = message ?? "";
        if (msg.Length > MaxMessageLength)
            msg = msg[..MaxMessageLength];

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tool_rejections(tool, category, path, task_id, message, created_at)
            VALUES($tool, $cat, $path, $task, $msg, $ts)
            """;
        cmd.Parameters.AddWithValue("$tool", tool);
        cmd.Parameters.AddWithValue("$cat", category);
        cmd.Parameters.AddWithValue("$path", path ?? "");
        cmd.Parameters.AddWithValue("$task", taskId ?? "");
        cmd.Parameters.AddWithValue("$msg", msg);
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>聚合摘要：总数 + 按类别/工具分布（供 stats 展示）。</summary>
    public async Task<ToolRejectionSummary> GetSummaryAsync(CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        int total = 0;
        await using (var totalCmd = conn.CreateCommand())
        {
            totalCmd.CommandText = "SELECT COUNT(*) FROM tool_rejections";
            total = Convert.ToInt32(await totalCmd.ExecuteScalarAsync(ct));
        }

        async Task<List<(string, int)>> GroupByAsync(string column)
        {
            var list = new List<(string, int)>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {column}, COUNT(*) FROM tool_rejections GROUP BY {column} ORDER BY COUNT(*) DESC";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                list.Add((reader.GetString(0), reader.GetInt32(1)));
            return list;
        }

        var byCategory = await GroupByAsync("category");
        var byTool = await GroupByAsync("tool");

        return new ToolRejectionSummary(total, byCategory, byTool);
    }
}
