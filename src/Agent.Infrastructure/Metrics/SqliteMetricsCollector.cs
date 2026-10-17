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
                    is_e2e              INTEGER NOT NULL DEFAULT 0,
                    retry_count         INTEGER NOT NULL,
                    total_ms            INTEGER NOT NULL,
                    planner_ms          INTEGER NOT NULL,
                    executor_ms         INTEGER NOT NULL,
                    validator_ms        INTEGER NOT NULL,
                    lint_blocking_errors INTEGER NOT NULL,
                    lint_warning_errors INTEGER NOT NULL,
                    error_summary       TEXT NOT NULL,
                    failure_category    TEXT NOT NULL DEFAULT '',
                    started_at          INTEGER NOT NULL,
                    finished_at         INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_metrics_started ON task_metrics(started_at DESC);
                CREATE INDEX IF NOT EXISTS idx_metrics_success ON task_metrics(success);
            """;
            cmd.ExecuteNonQuery();
        }

        // 兼容旧数据库：缺列则 ALTER 补上（is_suspicious → failure_category）
        var cols = new List<string>();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(task_metrics)";
            using var reader = pragma.ExecuteReader();
            while (reader.Read())
                cols.Add(reader.GetString(1));
        }

        if (!cols.Contains("is_suspicious", StringComparer.OrdinalIgnoreCase))
        {
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

        // 【10-08 metrics 数据治理】is_e2e 列迁移 + 历史回填：
        // E2E 用例的工程根在 workspace\e2e\case_*（编译错误路径或 use 输入含该路径）——强特征回填。
        if (!cols.Contains("is_e2e", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText =
                    "ALTER TABLE task_metrics ADD COLUMN is_e2e INTEGER NOT NULL DEFAULT 0";
                alterCmd.ExecuteNonQuery();
            }
            catch
            {
                // 列已存在 → 忽略
            }
        }

        // 历史回填（幂等：只回填未标记行；每次启动执行，605 行毫秒级）。
        // 特征必须精确到 E2E 构造措辞——不能靠"类名+动词"宽组合：
        // 【10-08 教训】宽特征 (OrderService AND (增加/新增)) 曾误标真实任务
        // "在工程中新增 DiscountService/TaxService 类…并在 OrderService…"
        // （10-03 项目名幻觉任务的真实输入）。精确特征只匹配 E2E 用例的固定开头：
        //   "给 OrderService 增加 GetSummary…" / "新增 OrderTotalService.cs" / "给 Order 增加 TotalAmount…"
        try
        {
            using var backfillCmd = conn.CreateCommand();
            backfillCmd.CommandText = """
                UPDATE task_metrics SET is_e2e = 1
                WHERE is_e2e = 0
                  AND (error_summary LIKE '%workspace\e2e%'
                       OR user_input LIKE '%workspace\e2e%'
                       OR user_input LIKE '%ShippingService%'
                       OR user_input LIKE '%OrderSummary%'
                       OR user_input LIKE 'use --write%'
                       OR user_input LIKE '%给 OrderService 增加%'
                       OR user_input LIKE '%OrderTotalService%'
                       OR user_input LIKE '%给 Order 增加 TotalAmount%')
                """;
            backfillCmd.ExecuteNonQuery();
        }
        catch
        {
            // 旧库无 is_e2e 列时忽略（ALTER 失败场景）
        }

        if (!cols.Contains("failure_category", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText =
                    "ALTER TABLE task_metrics ADD COLUMN failure_category TEXT NOT NULL DEFAULT ''";
                alterCmd.ExecuteNonQuery();
            }
            catch
            {
                // 列已存在 → 忽略
            }
        }

        // 失败类别索引：必须在 ALTER 之后建（旧库先迁移再建索引，否则 CREATE INDEX 引用不存在列报错）
        try
        {
            using var idxCmd = conn.CreateCommand();
            idxCmd.CommandText =
                "CREATE INDEX IF NOT EXISTS idx_metrics_category ON task_metrics(failure_category)";
            idxCmd.ExecuteNonQuery();
        }
        catch
        {
            // 列迁移失败时忽略（索引不建不致命）
        }

        // 【10-10 真实口径视图】task_metrics_real：唯一权威的"真实任务统计口径"。
        // 所有面向真实统计的查询统一 FROM 本视图（is_e2e=0），消灭"每个查询记得加 WHERE"的脆弱约定；
        // E2E 用例仍查 task_metrics 原表（is_e2e=1，GetE2ECountAsync 等）。
        // 必须在 is_e2e 列 ALTER 之后建（列不存在时 CREATE VIEW 会引用未知列失败）。
        try
        {
            using var viewCmd = conn.CreateCommand();
            viewCmd.CommandText = """
                CREATE VIEW IF NOT EXISTS task_metrics_real AS
                SELECT * FROM task_metrics WHERE is_e2e = 0;
                """;
            viewCmd.ExecuteNonQuery();
        }
        catch
        {
            // 视图已存在 / 列迁移失败 → 忽略（真实口径退化为显式 WHERE 写法，不致命）
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
                success, is_suspicious, is_e2e, retry_count,
                total_ms, planner_ms, executor_ms, validator_ms,
                lint_blocking_errors, lint_warning_errors,
                error_summary, failure_category, started_at, finished_at
            ) VALUES (
                $task_id, $session_id, $user_input, $mode,
                $routing_tier, $service_id, $routing_reason,
                $success, $is_suspicious, $is_e2e, $retry_count,
                $total_ms, $planner_ms, $executor_ms, $validator_ms,
                $lint_blocking_errors, $lint_warning_errors,
                $error_summary, $failure_category, $started_at, $finished_at
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
        cmd.Parameters.AddWithValue("$is_e2e", metric.IsE2E ? 1 : 0);
        cmd.Parameters.AddWithValue("$retry_count", metric.RetryCount);
        cmd.Parameters.AddWithValue("$total_ms", metric.TotalMs);
        cmd.Parameters.AddWithValue("$planner_ms", metric.PlannerMs);
        cmd.Parameters.AddWithValue("$executor_ms", metric.ExecutorMs);
        cmd.Parameters.AddWithValue("$validator_ms", metric.ValidatorMs);
        cmd.Parameters.AddWithValue("$lint_blocking_errors", metric.LintBlockingErrors);
        cmd.Parameters.AddWithValue("$lint_warning_errors", metric.LintWarningErrors);
        cmd.Parameters.AddWithValue("$error_summary", Truncate(metric.ErrorSummary, 500));
        // 【3】失败类别自动归类：成功 → 空；失败 → FailureCategorizer 词表打标（纯数据层，无模型）
        var failureCategory = metric.Success
            ? ""
            : FailureCategorizer.Categorize(metric.ErrorSummary);
        cmd.Parameters.AddWithValue("$failure_category", failureCategory);
        cmd.Parameters.AddWithValue("$started_at", metric.StartedAt.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$finished_at", metric.FinishedAt.ToUnixTimeSeconds());

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>失败类别分布（成功任务不统计；统一走 task_metrics_real 真实口径视图，E2E 自动剔除）。</summary>
    public async Task<IReadOnlyList<(string Category, int Count)>> GetFailureBreakdownAsync(CancellationToken ct)
    {
        var list = new List<(string, int)>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT failure_category, COUNT(*)
            FROM task_metrics_real
            WHERE success = 0
            GROUP BY failure_category
            ORDER BY COUNT(*) DESC
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var cat = reader.GetString(0);
            list.Add((string.IsNullOrEmpty(cat) ? "other" : cat, reader.GetInt32(1)));
        }
        return list;
    }

    /// <summary>查询最近 N 条（真实口径视图）。</summary>
    public async Task<IReadOnlyList<TaskMetric>> GetRecentAsync(int take, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM task_metrics_real ORDER BY started_at DESC LIMIT $take;";
        cmd.Parameters.AddWithValue("$take", take);

        return await ReadAllAsync(cmd, ct);
    }

    /// <summary>
    /// 【10-17 ③】最近 N 条 + SQLite 下推过滤（/api/tasks 数据源；任务量放大前下沉查询层）。
    /// q：user_input/error_summary 子串（LIKE 转义 % _ \，与旧内存 Contains 语义一致，ASCII 大小写不敏感）；
    /// status：ok=success=1 / fail=success=0 AND is_suspicious=0 / susp=is_suspicious=1（非法值不加条件，与旧 switch 一致）；
    /// since：started_at（UTC epoch）≥。排序/截断全在库内，无过滤时行为与 GetRecentAsync 等价。
    /// </summary>
    public async Task<IReadOnlyList<TaskMetric>> GetFilteredRecentAsync(string? q, string? status, DateTimeOffset? since, int take, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = new System.Text.StringBuilder("SELECT * FROM task_metrics_real WHERE 1=1");
        if (!string.IsNullOrWhiteSpace(q))
            sql.Append(" AND (user_input LIKE $q ESCAPE '\\' OR error_summary LIKE $q ESCAPE '\\')");
        if (status is not null)
        {
            var s = status.ToLowerInvariant();
            if (s == "ok") sql.Append(" AND success = 1");
            else if (s == "fail") sql.Append(" AND success = 0 AND is_suspicious = 0");
            else if (s == "susp") sql.Append(" AND is_suspicious = 1");
        }
        if (since is not null)
            sql.Append(" AND started_at >= $since");
        sql.Append(" ORDER BY started_at DESC LIMIT $take;");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql.ToString();
        if (!string.IsNullOrWhiteSpace(q))
            cmd.Parameters.AddWithValue("$q", "%" + EscapeLike(q.Trim()) + "%");
        if (since is not null)
            cmd.Parameters.AddWithValue("$since", since.Value.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$take", take);

        return await ReadAllAsync(cmd, ct);
    }

    /// <summary>【10-17 ③】LIKE 通配符转义（% _ \）——q 为子串匹配且参数化，与 ESCAPE '\' 配套，
    /// 用户输入含 _（如 flt_susp 类 task id 片段）不致被当单字符通配符。</summary>
    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>【10-08 metrics 数据治理】E2E 用例计数（stats 单列展示，不污染真实统计）。</summary>
    public async Task<int> GetE2ECountAsync(CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM task_metrics WHERE is_e2e = 1;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    /// <summary>
    /// 【批次3·C 父失败回填】顶层 orchestrator 失败时，把本任务窗口内（FinishedAt ≥ since、
    /// UserInput 匹配前缀）的 Executor 子记录成功回填为失败并附父失败说明。
    /// 匹配用前缀（子记录 UserInput 被 Truncate 500，顶层可能更长），时间窗隔离同输入的历史任务。
    /// </summary>
    public async Task<int> BackfillFailedSubMetricsAsync(string userInput, DateTimeOffset since, string note, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE task_metrics
            SET success = 0,
                failure_category = 'validator_failed',
                error_summary = substr(error_summary || ' [' || $note || ']', 1, 500)
            WHERE routing_tier = 'OrchestratorExecutor'
              AND success = 1
              AND is_e2e = 0
              AND finished_at >= $since
              AND (user_input = substr($u, 1, 500) OR user_input LIKE substr($u, 1, 500) || '%')
            """;
        cmd.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$u", userInput);
        cmd.Parameters.AddWithValue("$note", note);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>查询最近的失败记录（真实口径视图）。</summary>
    public async Task<IReadOnlyList<TaskMetric>> GetRecentFailuresAsync(int take, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM task_metrics_real WHERE success = 0 ORDER BY started_at DESC LIMIT $take;";
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
            FROM task_metrics_real
            WHERE $since IS NULL OR started_at >= $since;
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
        => await QueryStatsAsync(serviceId, routingTier, take, sinceUnix: null, ct);

    /// <summary>【P2 调权地基】时代过滤版闭环查询：只统计 started_at ≥ since 的最近 N 条。</summary>
    public async Task<Agent.Infrastructure.Routing.ServiceStats> GetServiceStatsSinceAsync(
        string serviceId, string routingTier, DateTimeOffset since, int take, CancellationToken ct)
        => await QueryStatsAsync(serviceId, routingTier, take, since.ToUnixTimeSeconds(), ct);

    /// <summary>
    /// 公共聚合查询：按服务+路由层级聚合最近 N 条表现；sinceUnix 非空时只统计时代内样本。
    /// （两版 SQL 单处维护，防改列/加条件时一处漏改的漂移。）
    /// </summary>
    private async Task<Agent.Infrastructure.Routing.ServiceStats> QueryStatsAsync(
        string serviceId, string routingTier, int take, long? sinceUnix, CancellationToken ct)
    {
        var where = sinceUnix is null
            ? "service_id = $service AND routing_tier = $tier"
            : "service_id = $service AND routing_tier = $tier AND started_at >= $since";
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            SELECT COUNT(*) AS total,
                   SUM(CASE WHEN success = 1 THEN 1 ELSE 0 END) AS ok_count,
                   AVG(total_ms) AS avg_ms
            FROM (
                SELECT * FROM task_metrics_real
                WHERE {{where}}
                ORDER BY started_at DESC
                LIMIT $take
            );
        """;
        cmd.Parameters.AddWithValue("$service", serviceId);
        cmd.Parameters.AddWithValue("$tier", routingTier);
        cmd.Parameters.AddWithValue("$take", take);
        if (sinceUnix is not null)
            cmd.Parameters.AddWithValue("$since", sinceUnix.Value);

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
                FROM task_metrics_real
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
                IsE2E = SafeGetInt(reader, "is_e2e") == 1,
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