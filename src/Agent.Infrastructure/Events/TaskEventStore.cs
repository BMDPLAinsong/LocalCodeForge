// ============================================================
// Events / TaskEventStore.cs
// 任务事件持久化（10-07，Dashboard 详情回放地基）。
//   · 旁路追加：事件循环在 eventHub.Publish 之后 Record——渲染/hub 行为不变，
//     tests 零冲击；fail-safe（写失败静默，绝不影响任务执行）。
//   · 表：task_events(task_id, seq, type, payload, created_at)，主键 (task_id, seq)。
//   · payload 序列化与 Dashboard 回放共用同一 JsonOpts（camelCase + 枚举转字符串）——
//     存储格式 = SSE 推送格式，回放无需二次转换（消双路径漂移）。
//   · 生命周期：PruneOlderThan(days) 启动时清理（默认 30 天）。
// ============================================================
using System.Text.Json;
using System.Text.Json.Serialization;
using Agent.Core.Models;

namespace Agent.Infrastructure.Events;

/// <summary>一条已持久化的事件记录（含事件 schema 版本，供未来 schema 演进识别/迁移锚点）。</summary>
public sealed record StoredEvent(string TaskId, int Seq, string Type, string Payload, int SchemaVersion, long CreatedAt);

public sealed class TaskEventStore
{
    /// <summary>【批次7 ⑩ 事件 schema 版本】当前事件 schema 版本。
    /// 事件数据结构演进（字段增删/改名/语义变化）时必须递增，并配套读取侧迁移/兼容。</summary>
    public const int CurrentSchemaVersion = 1;
    /// <summary>事件 JSON 序列化唯一来源（TaskEventStore 存储 + DashboardHost 回放 + SSE 共用）。
    /// camelCase 属性 + 枚举转字符串——前端直接消费。</summary>
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _dbPath;
    private readonly object _lock = new();

    public TaskEventStore(string dbPath) { _dbPath = dbPath; Initialize(); }

    /// <summary>建表 + 索引 + schema 版本迁移（幂等）。表与 metrics.db 同库（复用连接基础设施，无额外库文件）。
    /// 【批次7 ⑩】schema_version 列随建表默认 1；老库（无此列）PRAGMA 检测后 ALTER ADD 补列——事件数据从此带版本标签。</summary>
    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_dbPath))!);
        lock (_lock)
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS task_events (
                    task_id         TEXT NOT NULL,
                    seq             INTEGER NOT NULL,
                    type            TEXT NOT NULL,
                    payload         TEXT NOT NULL,
                    schema_version  INTEGER NOT NULL DEFAULT 1,
                    created_at      INTEGER NOT NULL,
                    PRIMARY KEY (task_id, seq)
                );
                CREATE INDEX IF NOT EXISTS idx_task_events_task ON task_events(task_id);
            """;
            cmd.ExecuteNonQuery();

            // 老库迁移：无 schema_version 列 → ALTER 补列（DEFAULT 1 兼容历史数据）
            var hasVersion = false;
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info(task_events);";
                using var reader = pragma.ExecuteReader();
                while (reader.Read())
                {
                    if (string.Equals(reader.GetString(1), "schema_version", StringComparison.OrdinalIgnoreCase))
                    {
                        hasVersion = true;
                        break;
                    }
                }
            }
            if (!hasVersion)
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = "ALTER TABLE task_events ADD COLUMN schema_version INTEGER NOT NULL DEFAULT 1;";
                alter.ExecuteNonQuery();
            }
        }
    }

    /// <summary>追加一条事件（fail-safe：异常静默吞掉，绝不影响任务执行/渲染）。
    /// 【10-14 P3-6 命名约定】原名 RecordAsync 但实现为纯同步（SqliteConnection + ExecuteNonQuery）——
    /// 本库约定「Async 后缀 = 真异步」（IMetricsCollector.RecordAsync 是 await using + OpenAsync），
    /// 故去掉后缀恢复约定，避免调用方误以为可 await/可并行。同理 GetEvents。</summary>
    public void Record(string taskId, int seq, AgentEvent evt)
    {
        try
        {
            var payload = JsonSerializer.Serialize(evt, evt.GetType(), JsonOpts);
            lock (_lock)
            {
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT OR REPLACE INTO task_events(task_id, seq, type, payload, schema_version, created_at)
                    VALUES ($task, $seq, $type, $payload, $schemaVersion, $ts);
                """;
                cmd.Parameters.AddWithValue("$task", taskId);
                cmd.Parameters.AddWithValue("$seq", seq);
                cmd.Parameters.AddWithValue("$type", evt.GetType().Name);
                cmd.Parameters.AddWithValue("$payload", payload);
                cmd.Parameters.AddWithValue("$schemaVersion", CurrentSchemaVersion);
                cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                cmd.ExecuteNonQuery();
            }
        }
        catch
        {
            // 持久化尽力而为：失败不阻塞任务（数据可接受丢失，SSE/渲染不受影响）
        }
    }

    /// <summary>按任务读取全部事件（seq 升序）。【10-14 P3-6】原名 GetEventsAsync，同 Record 去后缀（同步实现）。</summary>
    public IReadOnlyList<StoredEvent> GetEvents(string taskId)
    {
        var list = new List<StoredEvent>();
        try
        {
            lock (_lock)
            {
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT task_id, seq, type, payload, schema_version, created_at FROM task_events
                    WHERE task_id = $task ORDER BY seq ASC;
                """;
                cmd.Parameters.AddWithValue("$task", taskId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    list.Add(new StoredEvent(
                        reader.GetString(0), reader.GetInt32(1),
                        reader.GetString(2), reader.GetString(3),
                        reader.GetInt32(4), reader.GetInt64(5)));
            }
        }
        catch { /* 读取失败返回空列表（Dashboard 降级显示"无事件"） */ }
        return list;
    }

    /// <summary>清理早于 cutoff 的事件（启动时调用，默认 30 天）。返回删除条数。</summary>
    public int PruneOlderThan(int days)
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();
            lock (_lock)
            {
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM task_events WHERE created_at < $cutoff;";
                cmd.Parameters.AddWithValue("$cutoff", cutoff);
                return cmd.ExecuteNonQuery();
            }
        }
        catch { return 0; }
    }
}
