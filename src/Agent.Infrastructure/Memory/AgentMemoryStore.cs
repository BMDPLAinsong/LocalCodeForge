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

    /// <summary>记忆命中记录。Count=同类 topic 归档次数（去重合并），时间用于过期判定。</summary>
    public sealed record MemoryHit(
        long Id, string Kind, string Topic, string Content, int HitCount,
        int Count = 1, string CreatedAt = "", string LastHitAt = "", string Feature = "");

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
                hit_count   INTEGER NOT NULL DEFAULT 0,
                last_hit_at TEXT,
                count       INTEGER NOT NULL DEFAULT 1,
                feature     TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_memory_topic ON agent_memory(topic);
        """;
        cmd.ExecuteNonQuery();

        // 兼容旧库：补 last_hit_at / count / feature 列（PRAGMA 检测 → ALTER）
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(agent_memory);";
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var reader = pragma.ExecuteReader();
            while (reader.Read())
                cols.Add(reader.GetString(1));
            if (!cols.Contains("last_hit_at"))
            {
                using var a1 = conn.CreateCommand();
                a1.CommandText = "ALTER TABLE agent_memory ADD COLUMN last_hit_at TEXT;";
                a1.ExecuteNonQuery();
            }
            if (!cols.Contains("count"))
            {
                using var a2 = conn.CreateCommand();
                a2.CommandText = "ALTER TABLE agent_memory ADD COLUMN count INTEGER NOT NULL DEFAULT 1;";
                a2.ExecuteNonQuery();
            }
            if (!cols.Contains("feature"))
            {
                using var a3 = conn.CreateCommand();
                a3.CommandText = "ALTER TABLE agent_memory ADD COLUMN feature TEXT;";
                a3.ExecuteNonQuery();
            }
        }
    }

    /// <summary>
    /// 写入一条记忆（记忆治理·去重内建）：同 kind+topic 已存在 → 合并更新
    /// （content 取最新、count+1、last_hit_at 刷新），否则插入新行。
    /// 任务归档同类失败自动合并为一条，count 承载真实次数——记忆不膨胀。
    /// </summary>
    public void Add(string kind, string topic, string content, string? sourceTask = null, string? feature = null)
    {
        if (string.IsNullOrWhiteSpace(topic) || string.IsNullOrWhiteSpace(content))
            return;
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            var topicTrim = topic.Trim();
            var contentTrim = content.Trim();
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            using (var find = conn.CreateCommand())
            {
                find.CommandText = "SELECT id FROM agent_memory WHERE kind = @kind AND topic = @topic LIMIT 1;";
                find.Parameters.AddWithValue("@kind", kind);
                find.Parameters.AddWithValue("@topic", topicTrim);
                var existId = find.ExecuteScalar();

                if (existId is not null)
                {
                    using var upd = conn.CreateCommand();
                    upd.CommandText = """
                        UPDATE agent_memory
                        SET content = @content, count = count + 1, last_hit_at = @at,
                            feature = COALESCE(@feature, feature)
                        WHERE id = @id;
                    """;
                    upd.Parameters.AddWithValue("@content", contentTrim);
                    upd.Parameters.AddWithValue("@at", now);
                    upd.Parameters.AddWithValue("@feature", (object?)feature ?? DBNull.Value);
                    upd.Parameters.AddWithValue("@id", Convert.ToInt64(existId));
                    upd.ExecuteNonQuery();
                    return;
                }
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO agent_memory (kind, topic, content, source_task, created_at, last_hit_at, count, feature)
                VALUES (@kind, @topic, @content, @task, @at, @at, 1, @feature);
            """;
            cmd.Parameters.AddWithValue("@kind", kind);
            cmd.Parameters.AddWithValue("@topic", topicTrim);
            cmd.Parameters.AddWithValue("@content", contentTrim);
            cmd.Parameters.AddWithValue("@task", sourceTask ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@at", now);
            cmd.Parameters.AddWithValue("@feature", (object?)feature ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // DB 不可用不阻塞任务
        }
    }

    /// <summary>
    /// 按输入检索相关记忆（关键词命中计分，top limit，命中数递增）。
    /// 【任务5 记忆注入过滤】feature（TaskFeatureClassifier 特征类）非空时：
    ///   kind=task 的任务归档记忆若 feature 不匹配 → 剔除（强特征相关，避免跨类污染）；
    ///   其余（kind=model 决策/经验）不匹配 → 降权（score-1），仍可注入（决策可能跨任务复用）。
    /// 关键词：中文连续串（≥2 字）、英文/数字标识符（≥3 字符）、文件路径 token。
    /// </summary>
    public List<MemoryHit> Search(string input, int limit = 3, string? feature = null)
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
            cmd.CommandText = """
                SELECT id, kind, topic, content, hit_count, count, created_at, COALESCE(last_hit_at, ''), COALESCE(feature, '')
                FROM agent_memory;
            """;
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
                    // 【任务5】特征过滤：任务归档记忆跨特征类剔除；决策记忆跨类降权
                    var memFeature = reader.IsDBNull(8) ? "" : reader.GetString(8);
                    var kind = reader.GetString(1);
                    if (!string.IsNullOrEmpty(feature) && !string.IsNullOrEmpty(memFeature)
                        && !memFeature.Equals(feature, StringComparison.OrdinalIgnoreCase))
                    {
                        if (kind.Equals("task", StringComparison.OrdinalIgnoreCase))
                            continue;
                        score -= 1;
                        if (score <= 0) continue;
                    }
                    hits.Add((
                        new MemoryHit(reader.GetInt64(0), kind, topic, content,
                            reader.GetInt32(4), reader.IsDBNull(5) ? 1 : reader.GetInt32(5),
                            reader.GetString(6), reader.GetString(7), memFeature),
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
                UPDATE agent_memory
                SET hit_count = hit_count + 1, last_hit_at = @at
                WHERE id = @id;
                SELECT hit_count FROM agent_memory WHERE id = @id;
            """;
            cmd.Parameters.AddWithValue("@at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.Parameters.AddWithValue("@id", id);
            var scalar = cmd.ExecuteScalar();
            return scalar is null ? 0 : Convert.ToInt32(scalar);
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    /// <summary>记忆统计。</summary>
    public sealed record MemoryStats(int Total, int ModelCount, int TaskCount, int TotalHits);

    /// <summary>列出最近 N 条（按创建时间倒序）。</summary>
    public List<MemoryHit> List(int limit = 50)
    {
        var result = new List<MemoryHit>();
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, kind, topic, content, hit_count, count, created_at, COALESCE(last_hit_at, '')
                FROM agent_memory ORDER BY id DESC LIMIT $limit;
            """;
            cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new MemoryHit(
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetInt32(4), reader.IsDBNull(5) ? 1 : reader.GetInt32(5),
                    reader.GetString(6), reader.GetString(7)));
            }
        }
        catch (SqliteException)
        {
        }
        return result;
    }

    /// <summary>记忆统计（总数/按 kind 分布/总命中）。</summary>
    public MemoryStats GetStats()
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*),
                       SUM(CASE WHEN kind = 'model' THEN 1 ELSE 0 END),
                       SUM(CASE WHEN kind = 'task' THEN 1 ELSE 0 END),
                       SUM(hit_count)
                FROM agent_memory;
            """;
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return new MemoryStats(0, 0, 0, 0);
            int Sum(int i) => reader.IsDBNull(i) ? 0 : reader.GetInt32(i);
            return new MemoryStats(Sum(0), Sum(1), Sum(2), Sum(3));
        }
        catch (SqliteException)
        {
            return new MemoryStats(0, 0, 0, 0);
        }
    }

    /// <summary>删除单条记忆。</summary>
    public bool Forget(long id)
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM agent_memory WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>
    /// 过期清理：创建/最近命中早于 maxDays 天 且 命中数 &lt; minHits（不常用且久未用）→ 删除。
    /// 返回删除条数。
    /// </summary>
    public int PruneExpired(int maxDays, int minHits)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-Math.Max(1, maxDays)).ToString("yyyy-MM-dd HH:mm:ss");
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM agent_memory
                WHERE COALESCE(last_hit_at, created_at) < $cutoff
                  AND hit_count < $minHits;
            """;
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            cmd.Parameters.AddWithValue("$minHits", Math.Max(0, minHits));
            return cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    /// <summary>
    /// 历史重复清理（旧库可能已有同 kind+topic 多行）：保留 id 最小行，
    /// count 合并为 SUM，删除其余。Add 已内建合并，此方法仅治理存量。
    /// </summary>
    public int Deduplicate()
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            // 找出重复组：kind+topic 出现 ≥2 次
            var groups = new List<(string Kind, string Topic, long KeepId)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT kind, topic, MIN(id) AS keep_id, COUNT(*) AS c
                    FROM agent_memory
                    GROUP BY kind, topic
                    HAVING c > 1;
                """;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    groups.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
            }

            var removed = 0;
            foreach (var g in groups)
            {
                // 合并 count 到保留行
                using (var merge = conn.CreateCommand())
                {
                    merge.CommandText = """
                        UPDATE agent_memory
                        SET count = (SELECT SUM(count) FROM agent_memory WHERE kind = @kind AND topic = @topic)
                        WHERE id = @keep;
                    """;
                    merge.Parameters.AddWithValue("@kind", g.Kind);
                    merge.Parameters.AddWithValue("@topic", g.Topic);
                    merge.Parameters.AddWithValue("@keep", g.KeepId);
                    merge.ExecuteNonQuery();
                }
                using (var del = conn.CreateCommand())
                {
                    del.CommandText = """
                        DELETE FROM agent_memory
                        WHERE kind = @kind AND topic = @topic AND id != @keep;
                    """;
                    del.Parameters.AddWithValue("@kind", g.Kind);
                    del.Parameters.AddWithValue("@topic", g.Topic);
                    del.Parameters.AddWithValue("@keep", g.KeepId);
                    removed += del.ExecuteNonQuery();
                }
            }
            return removed;
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
