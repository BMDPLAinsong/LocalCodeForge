// ============================================================
// AgentMemoryStore.cs
// 模型级跨任务记忆（工程感知 P3）：
// 把模型在任务中发现的事实/决策持久化到 SQLite（workspace/agent_memory.db），
// 新任务开始按输入关键词检索相关记忆注入上下文——任务间真正"记住"。
// 确定性 SQLite 操作 + 关键词匹配（Tokenize：中文串/标识符/文件名），无模型调用。
// ============================================================

using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Agent.Infrastructure.Memory;

/// <summary>
/// 模型级记忆存储（Add/Search/命中计数）。
/// </summary>
public sealed class AgentMemoryStore
{
    private readonly string _connectionString;

    /// <summary>记忆命中记录。</summary>
    public sealed record MemoryHit(long Id, string Kind, string Topic, string Content, int HitCount);

    /// <summary>构造函数，接收数据库文件路径。</summary>
    public AgentMemoryStore(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        Initialize();
    }

    /// <summary>建表。</summary>
    private void Initialize()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS agent_memory (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                kind        TEXT NOT NULL,
                topic       TEXT NOT NULL,
                content     TEXT NOT NULL,
                source_task TEXT,
                created_at  TEXT NOT NULL,
                hit_count   INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_memory_topic ON agent_memory(topic);
        """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>写入一条记忆。</summary>
    public void Add(string kind, string topic, string content, string? sourceTask = null)
    {
        if (string.IsNullOrWhiteSpace(topic) || string.IsNullOrWhiteSpace(content))
            return;
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO agent_memory (kind, topic, content, source_task, created_at)
                VALUES (@kind, @topic, @content, @task, @at);
            """;
            cmd.Parameters.AddWithValue("@kind", kind);
            cmd.Parameters.AddWithValue("@topic", topic.Trim());
            cmd.Parameters.AddWithValue("@content", content.Trim());
            cmd.Parameters.AddWithValue("@task", sourceTask ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // DB 不可用不阻塞任务
        }
    }

    /// <summary>
    /// 按输入检索相关记忆（关键词命中计分，top limit，命中数递增）。
    /// 关键词：中文连续串（≥2 字）、英文/数字标识符（≥3 字符）、文件路径 token。
    /// </summary>
    public List<MemoryHit> Search(string input, int limit = 3)
    {
        var keywords = Tokenize(input);
        if (keywords.Count == 0)
            return new List<MemoryHit>();

        var hits = new List<(MemoryHit Hit, int Score)>();
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, kind, topic, content, hit_count FROM agent_memory;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var topic = reader.GetString(2);
                var content = reader.GetString(3);
                int score = 0;
                foreach (var kw in keywords)
                {
                    if (topic.Contains(kw, StringComparison.OrdinalIgnoreCase)) score += 2;
                    else if (content.Contains(kw, StringComparison.OrdinalIgnoreCase)) score += 1;
                }
                if (score > 0)
                {
                    hits.Add((
                        new MemoryHit(reader.GetInt64(0), reader.GetString(1), topic, content, reader.GetInt32(4)),
                        score));
                }
            }
        }
        catch (SqliteException)
        {
            return new List<MemoryHit>();
        }

        var top = hits
            .OrderByDescending(h => h.Score)
            .ThenByDescending(h => h.Hit.HitCount)
            .Take(limit)
            .Select(h => h.Hit)
            .ToList();

        foreach (var hit in top)
        {
            var newCount = BumpHit(hit.Id);
            top = top.Select(h => h.Id == hit.Id ? h with { HitCount = newCount } : h).ToList();
        }

        return top;
    }

    /// <summary>命中计数 +1，返回更新后的计数。</summary>
    public int BumpHit(long id)
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE agent_memory SET hit_count = hit_count + 1 WHERE id = @id;
                SELECT hit_count FROM agent_memory WHERE id = @id;
            """;
            cmd.Parameters.AddWithValue("@id", id);
            var scalar = cmd.ExecuteScalar();
            return scalar is null ? 0 : Convert.ToInt32(scalar);
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    /// <summary>
    /// 确定性关键词提取：中文连续串（≥2 字）、英文/数字标识符（≥3 字符，去路径边界）、
    /// 文件扩展名 token（如 AgentOptions.cs → AgentOptions、cs）。
    /// </summary>
    public static List<string> Tokenize(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new List<string>();

        var result = new List<string>();

        // 中文连续串（≥2 字）
        foreach (Match m in Regex.Matches(input, @"[\u4e00-\u9fff]{2,}"))
            result.Add(m.Value);

        // 文件/标识符 token（≥3 字符，去尾点/连字符）
        foreach (Match m in Regex.Matches(input, @"[a-zA-Z0-9_][a-zA-Z0-9_\.\-]{2,}"))
        {
            var v = m.Value.TrimEnd('.', '-');
            if (v.Length >= 3)
                result.Add(v);
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
    }
}
