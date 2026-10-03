// ============================================================
// SqliteMetricsCollector.cs
// 基于 SQLite 的指标采集器。
// ============================================================

using Agent.Core.Models;
using Microsoft.Data.Sqlite;

namespace Agent.Infrastructure.Metrics;

/// <summary>
/// SQLite 指标采集器。
/// </summary>
public sealed class SqliteMetricsCollector : IMetricsCollector
{
    private readonly string _connectionString;

    public SqliteMetricsCollector(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";

        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        Initialize();
    }

    /// <summary>建表 + 兼容旧数据库。</summary>
    private void Initialize()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        // 建表（新库直接用新 schema）
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS task_metrics (
                    task_id             TEXT PRIMARY KEY,
                    session_id          TEXT NOT NULL,
                    user_input          TEXT NOT NULL,
                    mode                TEXT NOT NULL,
                    routing_tier        TEXT NOT NULL,
                    service_id          TEXT NOT NULL,
                    routing_reason      TEXT NOT NULL,
                    success             INTEGER NOT NULL,
                    is_suspicious       INTEGER NOT NULL DEFAULT 0,
                    retry_count         INTEGER NOT NULL,
                    total_ms            INTEGER NOT NULL,
                    planner_ms          INTEGER NOT NULL,
                    executor_ms         INTEGER NOT NULL,
                    validator_ms        INTEGER NOT NULL,
                    lint_blocking_errors INTEGER NOT NULL,
                    lint_warning_errors INTEGER NOT NULL,
                    error_summary       TEXT NOT NULL,
                    started_at          INTEGER NOT NULL,
                    finished_at         INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_metrics_started ON task_metrics(started_at DESC);
                CREATE INDEX IF NOT EXISTS idx_metrics_success ON task_metrics(success);
            """;
            cmd.ExecuteNonQuery();
        }

        // 兼容旧数据库：如果旧表没有 is_suspicious 列，ALTER 加上
        try
        {
            using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText =
                "ALTER TABLE task_metrics ADD COLUMN is_suspicious INTEGER NOT NULL DEFAULT 0";
            alterCmd.ExecuteNonQuery();
        }
        catch
        {
            // 列已存在 → 忽略
        }
    }

    /// <summary>记录一次任务。</summary>
    public async Task RecordAsync(TaskMetric metric, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO task_metrics(
                task_id, session_id, user_input, mode,
                routing_tier, service_id, routing_reason,
                success, is_suspicious, retry_count,
                total_ms, planner_ms, executor_ms, validator_ms,
                lint_blocking_errors, lint_warning_errors,
                error_summary, started_at, finished_at
            ) VALUES (
                $task_id, $session_id, $user_input, $mode,
                $routing_tier, $service_id, $routing_reason,
                $success, $is_suspicious, $retry_count,
                $total_ms, $planner_ms, $executor_ms, $validator_ms,
                $lint_blocking_errors, $lint_warning_errors,
                $error_summary, $started_at, $finished_at
            );
        """;

        cmd.Parameters.AddWithValue("$task_id", metric.TaskId);
        cmd.Parameters.AddWithValue("$session_id", metric.SessionId);
        cmd.Parameters.AddWithValue("$user_input", Truncate(metric.UserInput, 500));
        cmd.Parameters.AddWithValue("$mode", metric.Mode);
        cmd.Parameters.AddWithValue("$routing_tier", metric.RoutingTier);
        cmd.Parameters.AddWithValue("$service_id", metric.ServiceId);
        cmd.Parameters.AddWithValue("$routing_reason", metric.RoutingReason);
        cmd.Parameters.AddWithValue("$success", metric.Success ? 1 : 0);
        cmd.Parameters.AddWithValue("$is_suspicious", metric.IsSuspicious ? 1 : 0);
        cmd.Parameters.AddWithValue("$retry_count", metric.RetryCount);
        cmd.Parameters.AddWithValue("$total_ms", metric.TotalMs);
        cmd.Parameters.AddWithValue("$planner_ms", metric.PlannerMs);
        cmd.Parameters.AddWithValue("$executor_ms", metric.ExecutorMs);
        cmd.Parameters.AddWithValue("$validator_ms", metric.ValidatorMs);
        cmd.Parameters.AddWithValue("$lint_blocking_errors", metric.LintBlockingErrors);
        cmd.Parameters.AddWithValue("$lint_warning_errors", metric.LintWarningErrors);
        cmd.Parameters.AddWithValue("$error_summary", Truncate(metric.ErrorSummary, 500));
        cmd.Parameters.AddWithValue("$started_at", metric.StartedAt.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$finished_at", metric.FinishedAt.ToUnixTimeSeconds());

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>查询最近 N 条。</summary>
    public async Task<IReadOnlyList<TaskMetric>> GetRecentAsync(int take, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM task_metrics ORDER BY started_at DESC LIMIT $take;";
        cmd.Parameters.AddWithValue("$take", take);

        return await ReadAllAsync(cmd, ct);
    }

    /// <summary>查询最近的失败记录。</summary>
    public async Task<IReadOnlyList<TaskMetric>> GetRecentFailuresAsync(int take, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM task_metrics WHERE success = 0 ORDER BY started_at DESC LIMIT $take;";
        cmd.Parameters.AddWithValue("$take", take);

        return await ReadAllAsync(cmd, ct);
    }

    /// <summary>获取统计汇总。</summary>
    public async Task<MetricsSummary> GetSummaryAsync(DateTime? since, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*) AS total,
                SUM(CASE WHEN success = 1 THEN 1 ELSE 0 END) AS success_count,
                SUM(CASE WHEN is_suspicious = 1 THEN 1 ELSE 0 END) AS suspicious_count,
                AVG(retry_count) AS avg_retry,
                AVG(total_ms) AS avg_total,
                AVG(planner_ms) AS avg_planner,
                AVG(executor_ms) AS avg_executor,
                AVG(validator_ms) AS avg_validator
            FROM task_metrics
            WHERE ($since IS NULL OR started_at >= $since);
        """;

        object sinceParam = since.HasValue
            ? new DateTimeOffset(since.Value.ToUniversalTime()).ToUnixTimeSeconds()
            : DBNull.Value;
        cmd.Parameters.AddWithValue("$since", sinceParam);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            return new MetricsSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        var total = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
        var success = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        var suspicious = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
        var failure = total - success;
        var rate = total > 0 ? (double)success / total : 0;

        return new MetricsSummary(
            TotalCount: total,
            SuccessCount: success,
            FailureCount: failure,
            SuspiciousCount: suspicious,
            SuccessRate: rate,
            AvgRetryCount: reader.IsDBNull(3) ? 0 : reader.GetDouble(3),
            AvgTotalMs: reader.IsDBNull(4) ? 0 : reader.GetDouble(4),
            AvgPlannerMs: reader.IsDBNull(5) ? 0 : reader.GetDouble(5),
            AvgExecutorMs: reader.IsDBNull(6) ? 0 : reader.GetDouble(6),
            AvgValidatorMs: reader.IsDBNull(7) ? 0 : reader.GetDouble(7));
    }

    /// <summary>按服务+路由层级聚合最近 N 条（数据闭环输入）。</summary>
    public async Task<Agent.Infrastructure.Routing.ServiceStats> GetServiceStatsAsync(
        string serviceId, string routingTier, int take, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) AS total,
                   SUM(CASE WHEN success = 1 THEN 1 ELSE 0 END) AS ok_count,
                   AVG(total_ms) AS avg_ms
            FROM (
                SELECT * FROM task_metrics
                WHERE service_id = $service AND routing_tier = $tier
                ORDER BY started_at DESC
                LIMIT $take
            );
        """;
        cmd.Parameters.AddWithValue("$service", serviceId);
        cmd.Parameters.AddWithValue("$tier", routingTier);
        cmd.Parameters.AddWithValue("$take", take);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new Agent.Infrastructure.Routing.ServiceStats(0, 0, 0);

        return new Agent.Infrastructure.Routing.ServiceStats(
            TotalCount: reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            SuccessCount: reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            AvgTotalMs: reader.IsDBNull(2) ? 0 : reader.GetDouble(2));
    }

    /// <summary>【时代隔离】按时代边界拆分某服务的表现（当前时代 ≥ epoch vs 历史时代 < epoch）。</summary>
    public async Task<(Agent.Infrastructure.Routing.ServiceStats Current, Agent.Infrastructure.Routing.ServiceStats Legacy)> GetEpochBreakdownAsync(
        string serviceId, DateTimeOffset epoch, CancellationToken ct)
    {
        var epochUnix = epoch.ToUnixTimeSeconds();

        async Task<Agent.Infrastructure.Routing.ServiceStats> QueryAsync(string cmp)
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT COUNT(*) AS total,
                       SUM(CASE WHEN success = 1 THEN 1 ELSE 0 END) AS ok_count,
                       AVG(total_ms) AS avg_ms
                FROM task_metrics
                WHERE service_id = $service AND started_at {cmp} $epoch;
            """;
            cmd.Parameters.AddWithValue("$service", serviceId);
            cmd.Parameters.AddWithValue("$epoch", epochUnix);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return new Agent.Infrastructure.Routing.ServiceStats(0, 0, 0);
            return new Agent.Infrastructure.Routing.ServiceStats(
                TotalCount: reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                SuccessCount: reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                AvgTotalMs: reader.IsDBNull(2) ? 0 : reader.GetDouble(2));
        }

        var current = await QueryAsync(">=");
        var legacy = await QueryAsync("<");
        return (current, legacy);
    }

    // ─────────── 辅助 ───────────

    private static async Task<IReadOnlyList<TaskMetric>> ReadAllAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var result = new List<TaskMetric>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            result.Add(new TaskMetric
            {
                TaskId = reader.GetString(reader.GetOrdinal("task_id")),
                SessionId = reader.GetString(reader.GetOrdinal("session_id")),
                UserInput = reader.GetString(reader.GetOrdinal("user_input")),
                Mode = reader.GetString(reader.GetOrdinal("mode")),
                RoutingTier = reader.GetString(reader.GetOrdinal("routing_tier")),
                ServiceId = reader.GetString(reader.GetOrdinal("service_id")),
                RoutingReason = reader.GetString(reader.GetOrdinal("routing_reason")),
                Success = reader.GetInt32(reader.GetOrdinal("success")) == 1,
                // 用 GetOrdinal 而不是数字索引，兼容旧数据库
                IsSuspicious = SafeGetInt(reader, "is_suspicious") == 1,
                RetryCount = reader.GetInt32(reader.GetOrdinal("retry_count")),
                TotalMs = reader.GetInt64(reader.GetOrdinal("total_ms")),
                PlannerMs = reader.GetInt64(reader.GetOrdinal("planner_ms")),
                ExecutorMs = reader.GetInt64(reader.GetOrdinal("executor_ms")),
                ValidatorMs = reader.GetInt64(reader.GetOrdinal("validator_ms")),
                LintBlockingErrors = reader.GetInt32(reader.GetOrdinal("lint_blocking_errors")),
                LintWarningErrors = reader.GetInt32(reader.GetOrdinal("lint_warning_errors")),
                ErrorSummary = reader.GetString(reader.GetOrdinal("error_summary")),
                StartedAt = DateTimeOffset.FromUnixTimeSeconds(
                    reader.GetInt64(reader.GetOrdinal("started_at"))).ToLocalTime(),
                FinishedAt = DateTimeOffset.FromUnixTimeSeconds(
                    reader.GetInt64(reader.GetOrdinal("finished_at"))).ToLocalTime()
            });
        }

        return result;
    }

    /// <summary>安全读取 int 列（列可能不存在时返回 0）。</summary>
    private static int SafeGetInt(SqliteDataReader reader, string columnName)
    {
        try
        {
            var ordinal = reader.GetOrdinal(columnName);
            return reader.IsDBNull(ordinal) ? 0 : reader.GetInt32(ordinal);
        }
        catch
        {
            return 0;
        }
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "...");
}