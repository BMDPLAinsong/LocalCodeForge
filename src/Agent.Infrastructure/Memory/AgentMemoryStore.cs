// ============================================================
// AgentMemoryStore.cs
// 模型级跨任务记忆（工程感知 P3）：
// 把模型在任务中发现的事实/决策持久化到 SQLite（workspace/agent_memory.db），
// 新任务开始按输入关键词检索相关记忆注入上下文——任务间真正"记住"。
// 确定性 SQLite 操作 + token 化倒排索引（Tokenize：中文串/标识符/文件名）。
// 【10-18 P2-4】可选语义通道：注入 IEmbedder（本机 nomic-embed-text）后，
//   写入同步向量（memory_vectors 表）、Search 在倒排不足 limit 时余弦补足——
//   模型调用只发生在可选通道（embedder 空/失败自动降级纯倒排），核心检索仍是确定性的。
// ============================================================

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Agent.Infrastructure.Memory;

/// <summary>
/// 模型级记忆存储（Add/Search/命中计数）。
/// </summary>
public sealed class AgentMemoryStore
{
    private readonly string _connectionString;
    private readonly IEmbedder? _embedder;
    // 【10-18 P2-4】向量存量懒回填：进程内只尝试一次（成败都不反复，防服务抖动反复打）。
    // 与 memory_terms 的 Initialize 内回填不同：embedding 是模型调用，放 Search 触发更稳
    // （启动不阻塞；首次语义检索慢几秒，后续增量由 Add 维护）。
    private bool _vectorBackfillAttempted;

    /// <summary>记忆命中记录。Count=同类 topic 归档次数（去重合并），时间用于过期判定。</summary>
    public sealed record MemoryHit(
        long Id, string Kind, string Topic, string Content, int HitCount,
        int Count = 1, string CreatedAt = "", string LastHitAt = "", string Feature = "");

    /// <summary>构造函数，接收数据库文件路径。embedder 可空：null = 纯 token 倒排（兼容旧调用/测试）。</summary>
    public AgentMemoryStore(string dbPath, IEmbedder? embedder = null)
    {
        _connectionString = $"Data Source={dbPath}";
        _embedder = embedder;
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
            -- 【10-17 ②】token 化倒排索引：term → memory_id 映射（weight 2=topic / 1=content，
            -- 同 (memory_id, term) 只存高权重；Search 按输入 Tokenize 精确匹配，替代 LIKE 子串下推——
            -- 无通配符歧义（Method1500 不被 _ 误匹配）、索引可命中（LIKE '%kw%' 无法用普通索引）。
            CREATE TABLE IF NOT EXISTS memory_terms (
                memory_id INTEGER NOT NULL,
                term      TEXT NOT NULL,
                weight    INTEGER NOT NULL,
                PRIMARY KEY (memory_id, term)
            );
            CREATE INDEX IF NOT EXISTS idx_terms_term ON memory_terms(term);
            -- 【10-18 P2-4】记忆向量列：float32 小端 blob（dim*4 字节）+ 模型名。
            -- 与 memory_terms 互补：倒排 = 精确匹配（主通道），向量 = 语义兜底
            -- （Search 倒排不足 limit 时以余弦补足，不改变倒排语义与既有断言）。
            -- 全量读入内存算余弦（记忆量 ≤ 数百条 × 768 维 ≈ 数百 KB，无需 ANN 索引——
            -- 数据量级下全量扫描即确定性最优，零新增依赖）。
            CREATE TABLE IF NOT EXISTS memory_vectors (
                memory_id  INTEGER PRIMARY KEY,
                vector     BLOB NOT NULL,
                dim        INTEGER NOT NULL,
                model      TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
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

        // 【10-17 ②】存量回填：旧库已有 agent_memory 行但 memory_terms 为空 → 全量重建索引。
        // （新增行由 Add/合并更新增量维护；此判断只在"表空且有存量"时触发一次，幂等。）
        using (var chk = conn.CreateCommand())
        {
            chk.CommandText = "SELECT (SELECT COUNT(*) FROM agent_memory), (SELECT COUNT(*) FROM memory_terms);";
            using var r = chk.ExecuteReader();
            if (r.Read() && r.GetInt32(0) > 0 && r.GetInt32(1) == 0)
            {
                var toIndex = new List<(long Id, string Topic, string Content)>();
                using (var rows = conn.CreateCommand())
                {
                    rows.CommandText = "SELECT id, topic, content FROM agent_memory;";
                    using var rr = rows.ExecuteReader();
                    while (rr.Read())
                        toIndex.Add((rr.GetInt64(0), rr.GetString(1), rr.GetString(2)));
                }
                foreach (var row in toIndex)
                    IndexMemoryRow(conn, row.Id, row.Topic, row.Content);
            }
        }
    }

    /// <summary>
    /// 重建单条记忆的倒排项（同一连接内调用）：先删旧项再按 topic/content 拆词插入。
    /// weight = 2（topic）/ 1（content）；同 term 双字段命中取 2（topic 优先，与旧评分 if/else 语义一致）。
    /// </summary>
    private static void IndexMemoryRow(SqliteConnection conn, long id, string topic, string content)
    {
        using var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM memory_terms WHERE memory_id = $id;";
        del.Parameters.AddWithValue("$id", id);
        del.ExecuteNonQuery();

        var terms = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Tokenize(topic))
            terms[t] = 2;
        foreach (var t in Tokenize(content))
        {
            if (!terms.ContainsKey(t))
                terms[t] = 1;
        }
        foreach (var kv in terms)
        {
            using var ins = conn.CreateCommand();
            ins.CommandText = "INSERT OR REPLACE INTO memory_terms(memory_id, term, weight) VALUES ($id, $term, $weight);";
            ins.Parameters.AddWithValue("$id", id);
            ins.Parameters.AddWithValue("$term", kv.Key);
            ins.Parameters.AddWithValue("$weight", kv.Value);
            ins.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 【10-18 P2-4】写入/更新单条记忆的向量（同一连接内调用，upsert）。
    /// 尽力而为：embedder 为空/服务失败/维度不符 → 静默跳过（写入不阻塞，检索自动降级纯倒排）。
    /// </summary>
    private void TryIndexVector(SqliteConnection conn, long id, string topic, string content)
    {
        if (_embedder is null)
            return;
        try
        {
            var v = _embedder.Embed(topic + " " + content);
            if (v is null || v.Length != _embedder.Dimensions)
                return;
            var blob = new byte[v.Length * 4];
            Buffer.BlockCopy(v, 0, blob, 0, blob.Length);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO memory_vectors(memory_id, vector, dim, model, updated_at)
                VALUES ($id, $vec, $dim, $model, $at)
                ON CONFLICT(memory_id) DO UPDATE SET
                    vector = excluded.vector, dim = excluded.dim,
                    model = excluded.model, updated_at = excluded.updated_at;
            """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$vec", blob);
            cmd.Parameters.AddWithValue("$dim", v.Length);
            cmd.Parameters.AddWithValue("$model", _embedder.Model);
            cmd.Parameters.AddWithValue("$at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // 向量尽力而为：失败不影响记忆写入与倒排检索
        }
    }

    /// <summary>
    /// 【10-18 P2-4】存量懒回填：agent_memory 有行而 memory_vectors 为空 → 全量生成向量。
    /// 幂等判定仿 memory_terms（表空且有存量才触发）；进程内只尝试一次（成功与否都不反复）。
    /// </summary>
    private void EnsureVectorBackfill(SqliteConnection conn)
    {
        if (_embedder is null || _vectorBackfillAttempted)
            return;
        _vectorBackfillAttempted = true;
        try
        {
            using var chk = conn.CreateCommand();
            chk.CommandText = "SELECT (SELECT COUNT(*) FROM agent_memory), (SELECT COUNT(*) FROM memory_vectors);";
            using var r = chk.ExecuteReader();
            if (!r.Read() || r.GetInt32(0) == 0 || r.GetInt32(1) > 0)
                return;

            var rows = new List<(long Id, string Topic, string Content)>();
            using (var q = conn.CreateCommand())
            {
                q.CommandText = "SELECT id, topic, content FROM agent_memory;";
                using var rr = q.ExecuteReader();
                while (rr.Read())
                    rows.Add((rr.GetInt64(0), rr.GetString(1), rr.GetString(2)));
            }
            foreach (var row in rows)
                TryIndexVector(conn, row.Id, row.Topic, row.Content);
        }
        catch
        {
            // 回填尽力而为：失败则本次进程保持纯倒排（下次进程启动再试）
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
                    // 【10-17 ②】content 可能已更新 → 重建倒排项
                    IndexMemoryRow(conn, Convert.ToInt64(existId), topicTrim, contentTrim);
                    // 【10-18 P2-4】content 更新 → 同步重建向量（topic 不变，embed(topic+content) 语义一致）
                    TryIndexVector(conn, Convert.ToInt64(existId), topicTrim, contentTrim);
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
            cmd.CommandText = """
                INSERT INTO agent_memory (kind, topic, content, source_task, created_at, last_hit_at, count, feature)
                VALUES (@kind, @topic, @content, @task, @at, @at, 1, @feature);
                SELECT last_insert_rowid();
            """;
            var newId = Convert.ToInt64(cmd.ExecuteScalar());
            // 【10-17 ②】同步倒排索引（与记忆写入同事务连接，失败不阻塞写入）
            try
            {
                IndexMemoryRow(conn, newId, topicTrim, contentTrim);
            }
            catch (SqliteException)
            {
                // 索引尽力而为：检索可能漏命中，但写入与任务执行不受影响
            }
            // 【10-18 P2-4】同步向量（同连接，尽力而为）
            TryIndexVector(conn, newId, topicTrim, contentTrim);
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
            // 【10-17 ②】token 化倒排：memory_terms 精确匹配（无 LIKE 通配符歧义——
            // Method1500 不再被 _ 误匹配 Method1x500；term 索引可命中，非全表扫描）。
            // 每 (memory_id, term) 取高权重（topic=2 优先），同一记忆多 term 命中求和——与旧评分
            // （topic.Contains +2 else content +1，同词不重复计）等价。
            var scores = new Dictionary<long, int>();
            using (var idxCmd = conn.CreateCommand())
            {
                var sql = new StringBuilder(
                    "SELECT memory_id, SUM(w) AS score FROM (SELECT memory_id, MAX(weight) AS w FROM memory_terms WHERE term IN (");
                for (var i = 0; i < keywords.Count; i++)
                {
                    if (i > 0) sql.Append(", ");
                    sql.Append("$k").Append(i);
                    idxCmd.Parameters.AddWithValue("$k" + i, keywords[i]);
                }
                sql.Append(") GROUP BY memory_id, term) GROUP BY memory_id ORDER BY score DESC;");
                idxCmd.CommandText = sql.ToString();
                using var r = idxCmd.ExecuteReader();
                while (r.Read())
                    scores[r.GetInt64(0)] = r.GetInt32(1);
            }
            if (scores.Count == 0)
            {
                // 【10-18 P2-4】倒排零命中 → 纯向量语义兜底（含存量懒回填触发；阈值过滤方向不符）
                var semantic = SearchSemanticCore(input, limit, feature, new HashSet<long>());
                foreach (var hit in semantic)
                {
                    var newCount = BumpHit(hit.Id);
                    semantic = semantic.Select(h => h.Id == hit.Id ? h with { HitCount = newCount } : h).ToList();
                }
                return semantic;
            }

            var ids = scores.Keys.ToList();
            using (var rowCmd = conn.CreateCommand())
            {
                var rowSql = new StringBuilder(
                    "SELECT id, kind, topic, content, hit_count, count, created_at, COALESCE(last_hit_at, ''), COALESCE(feature, '') FROM agent_memory WHERE id IN (");
                for (var i = 0; i < ids.Count; i++)
                {
                    if (i > 0) rowSql.Append(", ");
                    rowSql.Append("$id").Append(i);
                    rowCmd.Parameters.AddWithValue("$id" + i, ids[i]);
                }
                rowSql.Append(");");
                rowCmd.CommandText = rowSql.ToString();
                using var reader = rowCmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetInt64(0);
                    var kind = reader.GetString(1);
                    var topic = reader.GetString(2);
                    var content = reader.GetString(3);
                    int score = scores[id];
                    // 【任务5】特征过滤：任务归档记忆跨特征类剔除；决策记忆跨类降权
                    var memFeature = reader.IsDBNull(8) ? "" : reader.GetString(8);
                    if (!string.IsNullOrEmpty(feature) && !string.IsNullOrEmpty(memFeature)
                        && !memFeature.Equals(feature, StringComparison.OrdinalIgnoreCase))
                    {
                        if (kind.Equals("task", StringComparison.OrdinalIgnoreCase))
                            continue;
                        score -= 1;
                        if (score <= 0) continue;
                    }
                    hits.Add((
                        new MemoryHit(id, kind, topic, content,
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

        // 【10-18 P2-4】向量语义兜底：倒排命中不足 limit 时，用 embedding 余弦补足（倒排优先——
        // 精确命中 ≥ limit 行为与 10-17 ② 完全一致；仅"精确不够"时引入语义召回，且不重复已命中 id）。
        if (top.Count < limit)
        {
            var extra = SearchSemanticCore(input, limit - top.Count, feature,
                top.Select(h => h.Id).ToHashSet());
            top.AddRange(extra);
        }

        foreach (var hit in top)
        {
            var newCount = BumpHit(hit.Id);
            top = top.Select(h => h.Id == hit.Id ? h with { HitCount = newCount } : h).ToList();
        }

        return top;
    }

    /// <summary>【10-18 P2-4】语义召回最小余弦阈值：低于此值视为方向不符（正交/无关），不强行召回。
    /// 0.5 能干净区分 FakeEmbedder 的命中(1.0)/正交(0.0)；nomic-embed-text 真实语义相近通常 ≥0.5。
    /// 阈值随 embedding 模型特性可校准（数据驱动，不写死进提示词）。</summary>
    private const float MinSemanticCos = 0.5f;

    /// <summary>
    /// 【10-18 P2-4】向量语义检索通道（Search 的兜底补充，不单独对外）：
    /// 输入 embedding → 与 memory_vectors 全量余弦（量小全内存）→ 阈值过滤 → top need，排除已倒排命中 id。
    /// feature 过滤与倒排同语义（task 类跨特征剔除；model 类不匹配仅跳过——此处无降权概念，直接不补）。
    /// embedder 空/失败/维度漂移/向量库空 → 空结果（Search 保持纯倒排，fail-open）。
    /// </summary>
    private List<MemoryHit> SearchSemanticCore(string input, int need, string? feature, HashSet<long> exclude)
    {
        if (_embedder is null || need <= 0)
            return new List<MemoryHit>();
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            EnsureVectorBackfill(conn);

            var qv = _embedder.Embed(input);
            if (qv is null || qv.Length != _embedder.Dimensions)
                return new List<MemoryHit>();

            // 全量读向量（记忆量小：数百条 × 768 维 ≈ 数百 KB）
            var vecs = new Dictionary<long, float[]>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT memory_id, dim, vector FROM memory_vectors;";
                using var rr = cmd.ExecuteReader();
                while (rr.Read())
                {
                    var id = rr.GetInt64(0);
                    var dim = rr.GetInt32(1);
                    if (dim != _embedder.Dimensions)
                        continue; // 维度漂移（换模型）→ 忽略，防错位
                    var blob = (byte[])rr[2];
                    var vec = new float[dim];
                    Buffer.BlockCopy(blob, 0, vec, 0, Math.Min(blob.Length, dim * 4));
                    vecs[id] = vec;
                }
            }
            if (vecs.Count == 0)
                return new List<MemoryHit>();

            var scored = new List<(long Id, float Cos)>();
            foreach (var kv in vecs)
            {
                if (exclude.Contains(kv.Key))
                    continue;
                scored.Add((kv.Key, Cosine(qv, kv.Value)));
            }
            scored.Sort((a, b) => b.Cos.CompareTo(a.Cos));
            // 阈值过滤：余弦低于 MinSemanticCos = 方向不符（正交/无关），不强行召回
            var picked = scored.Where(x => x.Cos >= MinSemanticCos).Take(need).Select(x => x.Id).ToList();
            if (picked.Count == 0)
                return new List<MemoryHit>();

            var result = new List<MemoryHit>();
            using (var rowCmd = conn.CreateCommand())
            {
                var sql = new StringBuilder(
                    "SELECT id, kind, topic, content, hit_count, count, created_at, COALESCE(last_hit_at, ''), COALESCE(feature, '') FROM agent_memory WHERE id IN (");
                for (var i = 0; i < picked.Count; i++)
                {
                    if (i > 0) sql.Append(", ");
                    sql.Append("$id").Append(i);
                    rowCmd.Parameters.AddWithValue("$id" + i, picked[i]);
                }
                sql.Append(");");
                rowCmd.CommandText = sql.ToString();
                using var reader = rowCmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetInt64(0);
                    var memFeature = reader.IsDBNull(8) ? "" : reader.GetString(8);
                    if (!string.IsNullOrEmpty(feature) && !string.IsNullOrEmpty(memFeature)
                        && !memFeature.Equals(feature, StringComparison.OrdinalIgnoreCase)
                        && reader.GetString(1).Equals("task", StringComparison.OrdinalIgnoreCase))
                        continue;
                    result.Add(new MemoryHit(id, reader.GetString(1), reader.GetString(2), reader.GetString(3),
                        reader.GetInt32(4), reader.IsDBNull(5) ? 1 : reader.GetInt32(5),
                        reader.GetString(6), reader.GetString(7), memFeature));
                }
            }
            return result;
        }
        catch (SqliteException)
        {
            return new List<MemoryHit>();
        }
    }

    /// <summary>余弦相似度（向量等长；零向量 → 0）。</summary>
    private static float Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        if (na <= 0 || nb <= 0)
            return 0f;
        return (float)(dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
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

    /// <summary>【10-13 批次3】LIKE 通配符转义（% _ \）——随 10-17 ② 倒排索引落地不再被 Search 使用；
    /// 保留供未来 LIKE 语义查询（如管理员模糊检索）复用。</summary>
    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
