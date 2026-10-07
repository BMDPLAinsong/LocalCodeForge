// ============================================================
// Events / TaskEventStore.cs
// 任务事件持久化（10-07，Dashboard 详情回放地基）。
//   · 旁路追加：事件循环在 eventHub.Publish 之后 RecordAsync——渲染/hub 行为不变，
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

/// <summary>一条已持久化的事件记录。</summary>
public sealed record StoredEvent(string TaskId, int Seq, string Type, string Payload, long CreatedAt);

public sealed class TaskEventStore
{
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

    /// <summary>建表 + 索引（幂等）。表与 metrics.db 同库（复用连接基础设施，无额外库文件）。</summary>
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
                    task_id     TEXT NOT NULL,
                    seq         INTEGER NOT NULL,
                    type        TEXT NOT NULL,
                    payload     TEXT NOT NULL,
                    created_at  INTEGER NOT NULL,
                    PRIMARY KEY (task_id, seq)
                );
                CREATE INDEX IF NOT EXISTS idx_task_events_task ON task_events(task_id);
            """;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>追加一条事件（fail-safe：异常静默吞掉，绝不影响任务执行/渲染）。</summary>
    public void RecordAsync(string taskId, int seq, AgentEvent evt)
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
                    INSERT OR REPLACE INTO task_events(task_id, seq, type, payload, created_at)
                    VALUES ($task, $seq, $type, $payload, $ts);
                """;
                cmd.Parameters.AddWithValue("$task", taskId);
                cmd.Parameters.AddWithValue("$seq", seq);
                cmd.Parameters.AddWithValue("$type", evt.GetType().Name);
                cmd.Parameters.AddWithValue("$payload", payload);
                cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                cmd.ExecuteNonQuery();
            }
        }
        catch
        {
            // 持久化尽力而为：失败不阻塞任务（数据可接受丢失，SSE/渲染不受影响）
        }
    }

    /// <summary>按任务读取全部事件（seq 升序）。</summary>
    public IReadOnlyList<StoredEvent> GetEventsAsync(string taskId)
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
                    SELECT task_id, seq, type, payload, created_at FROM task_events
                    WHERE task_id = $task ORDER BY seq ASC;
                """;
                cmd.Parameters.AddWithValue("$task", taskId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    list.Add(new StoredEvent(
                        reader.GetString(0), reader.GetInt32(1),
                        reader.GetString(2), reader.GetString(3), reader.GetInt64(4)));
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
