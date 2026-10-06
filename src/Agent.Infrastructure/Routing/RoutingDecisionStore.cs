// ============================================================
// RoutingDecisionStore.cs
// 【3A】路由决策落库：每次路由决策写入 SQLite（routing_decisions 表），
// 作为路由数据调权（3B/3C）的数据地基。
// 纯数据层，失败静默（记录失败不影响路由主流程）。
// ============================================================

using Microsoft.Data.Sqlite;

namespace Agent.Infrastructure.Routing;

/// <summary>路由决策聚合摘要（供 stats 展示）。</summary>
public sealed record RoutingDecisionSummary(
    int Total,
    IReadOnlyList<(string Service, int Count)> ByService,
    IReadOnlyList<(string Feature, int Count)> ByFeature,
    IReadOnlyList<(string Tier, int Count)> ByTier);

/// <summary>路由决策落库存储。</summary>
public sealed class RoutingDecisionStore
{
    /// <summary>user_input 截断长度（防脏数据撑库）。</summary>
    public const int MaxInputLength = 200;

    private readonly string _connectionString;

    public RoutingDecisionStore(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";

        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        Initialize();
    }

    /// <summary>建表 + 索引（新库直接建；老库无此表时 IF NOT EXISTS 兜底）。</summary>
    private void Initialize()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS routing_decisions (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id  TEXT NOT NULL DEFAULT '',
                    user_input  TEXT NOT NULL,
                    feature     TEXT NOT NULL DEFAULT '',
                    tier        TEXT NOT NULL,
                    service_id  TEXT NOT NULL,
                    reason      TEXT NOT NULL,
                    intent      TEXT NOT NULL DEFAULT '',
                    input_len   INTEGER NOT NULL,
                    created_at  INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_rd_created  ON routing_decisions(created_at DESC);
                CREATE INDEX IF NOT EXISTS idx_rd_service  ON routing_decisions(service_id);
                CREATE INDEX IF NOT EXISTS idx_rd_feature  ON routing_decisions(feature);
            """;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>记录一次路由决策（失败静默，不影响主流程）。</summary>
    public async Task RecordAsync(
        RoutingDecision decision,
        string userInput,
        string? sessionId,
        CancellationToken ct)
    {
        var input = userInput ?? "";
        if (input.Length > MaxInputLength)
            input = input[..MaxInputLength];

        var feature = TaskFeatureClassifier.FeatureName(TaskFeatureClassifier.Classify(input));

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO routing_decisions(
                session_id, user_input, feature, tier, service_id, reason, intent, input_len, created_at)
            VALUES($session, $input, $feature, $tier, $service, $reason, $intent, $len, $ts)
            """;
        cmd.Parameters.AddWithValue("$session", sessionId ?? "");
        cmd.Parameters.AddWithValue("$input", input);
        cmd.Parameters.AddWithValue("$feature", feature);
        cmd.Parameters.AddWithValue("$tier", decision.Tier.ToString());
        cmd.Parameters.AddWithValue("$service", decision.ServiceId);
        cmd.Parameters.AddWithValue("$reason", decision.Reason ?? "");
        cmd.Parameters.AddWithValue("$intent", decision.Intent?.ToString() ?? "");
        cmd.Parameters.AddWithValue("$len", input.Length);
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 清理早于指定天数的路由决策明细（防表无限增长；调权仅用近 10 条，历史无统计价值）。
    /// 失败静默（不影响启动主流程）。
    /// </summary>
    public void PruneOlderThan(int days)
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM routing_decisions WHERE created_at < $cutoff";
            cmd.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // 清理失败静默（不阻塞启动）
        }
    }

    /// <summary>聚合摘要：总数 + 按服务/特征类/层级分布（供 stats 展示）。</summary>
    public async Task<RoutingDecisionSummary> GetSummaryAsync(CancellationToken ct)    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        // 总数
        int total = 0;
        await using (var totalCmd = conn.CreateCommand())
        {
            totalCmd.CommandText = "SELECT COUNT(*) FROM routing_decisions";
            total = Convert.ToInt32(await totalCmd.ExecuteScalarAsync(ct));
        }

        async Task<List<(string, int)>> GroupByAsync(string column)
        {
            var list = new List<(string, int)>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {column}, COUNT(*) FROM routing_decisions GROUP BY {column} ORDER BY COUNT(*) DESC";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                list.Add((reader.GetString(0), reader.GetInt32(1)));
            return list;
        }

        var byService = await GroupByAsync("service_id");
        var byFeature = await GroupByAsync("feature");
        var byTier = await GroupByAsync("tier");

        return new RoutingDecisionSummary(total, byService, byFeature, byTier);
    }
}
