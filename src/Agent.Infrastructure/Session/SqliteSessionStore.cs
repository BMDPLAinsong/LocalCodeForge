// 引入 SQLite 驱动
using Microsoft.Data.Sqlite;
// 引入会话模型
using Agent.Core.Models;
// 引入会话接口
using Agent.Core.Abstractions;

// 命名空间
namespace Agent.Infrastructure.Session;

/// <summary>
/// 基于 SQLite 的会话存储实现。
/// </summary>
public sealed class SqliteSessionStore : ISessionStore
{
    // 连接字符串
    private readonly string _connectionString;

    /// <summary>构造函数，接收数据库文件路径。</summary>
    public SqliteSessionStore(string dbPath)
    {
        // 拼接连接字符串
        _connectionString = $"Data Source={dbPath}";

        // 确保目录存在
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // 初始化表结构
        Initialize();
    }

    /// <summary>建表。</summary>
    private void Initialize()
    {
        // 打开连接
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        // 创建命令
        using var cmd = conn.CreateCommand();
        // 建表 SQL：turns 保存每轮对话
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS turns (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id  TEXT NOT NULL,
                role        TEXT NOT NULL,
                content     TEXT NOT NULL,
                created_at  INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_turns_session ON turns(session_id, id);
        """;
        // 执行
        cmd.ExecuteNonQuery();
    }

    /// <summary>追加一轮对话。</summary>
    public async Task AppendTurnAsync(string sessionId, ChatTurn turn, CancellationToken ct)
    {
        // 打开连接
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        // 创建插入命令
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO turns(session_id, role, content, created_at)
            VALUES ($sid, $role, $content, $ts);
        """;
        // 绑定参数
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$role", turn.Role);
        cmd.Parameters.AddWithValue("$content", turn.Content);
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        // 执行
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>读取最近 N 轮对话。</summary>
    public async Task<IReadOnlyList<ChatTurn>> GetHistoryAsync(string sessionId, int take, CancellationToken ct)
    {
        // 打开连接
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        // 查询命令：取最近 take 条，再按 id 升序
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT role, content FROM (
                SELECT id, role, content FROM turns
                WHERE session_id = $sid
                ORDER BY id DESC
                LIMIT $take
            ) ORDER BY id ASC;
        """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$take", take);

        // 结果列表
        var result = new List<ChatTurn>();
        // 读取
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ChatTurn(reader.GetString(0), reader.GetString(1)));
        }
        return result;
    }

    /// <summary>清空指定会话。</summary>
    public async Task ClearAsync(string sessionId, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM turns WHERE session_id = $sid;";
        cmd.Parameters.AddWithValue("$sid", sessionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>【10-13 批次1】生命周期清理：删除早于 N 天前的轮次（turns 表此前只增不清）。
    /// 会话语义：多轮只带最近 MaxHistoryTurns 轮，旧轮次无运行时价值；按 created_at 全局清理
    /// （不区分会话——过期会话历史一并回收）。返回删除条数；失败返回 0（fail-safe）。</summary>
    public int PruneOlderThan(int days)
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM turns WHERE created_at < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            return cmd.ExecuteNonQuery();
        }
        catch
        {
            return 0;
        }
    }
}