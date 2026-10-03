using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Agent.Infrastructure.Routing;

/// <summary>
/// 【D2】任务特征类历史表现聚合器：读取 metrics.db（task_metrics），
/// 按 TaskFeatureClassifier 特征类聚合成功率 / 平均耗时 / 失败主因，
/// 供路由数据校准（高失败率特征类收紧重试）与 stats 归因报表使用。
/// 启动时加载一次进内存缓存；纯确定性 SQL 聚合，无模型调用。
/// </summary>
public sealed class TaskMetricsAggregator
{
    /// <summary>特征类历史表现（Total / Success / AvgMs / 失败主因 top）。</summary>
    public sealed record FeatureStats(
        int TotalCount,
        int SuccessCount,
        double AvgTotalMs,
        string TopFailureCause);

    private readonly string _metricsDbPath;
    private readonly object _lock = new();
    private Dictionary<TaskFeatureClassifier.Feature, FeatureStats>? _cache;
    private DateTime _loadedAt;

    public TaskMetricsAggregator(string metricsDbPath)
    {
        _metricsDbPath = metricsDbPath;
    }

    /// <summary>加载（幂等：已加载且 30 秒内不重载）。失败时返回空表（不影响主流程）。</summary>
    public void Load()
    {
        lock (_lock)
        {
            if (_cache is not null && DateTime.UtcNow - _loadedAt < TimeSpan.FromSeconds(30))
                return;
            _cache = Aggregate();
            _loadedAt = DateTime.UtcNow;
        }
    }

    /// <summary>取某特征类历史表现；未加载或无样本返回 null。</summary>
    public FeatureStats? Get(TaskFeatureClassifier.Feature feature)
    {
        lock (_lock)
        {
            if (_cache is null) return null;
            return _cache.TryGetValue(feature, out var s) ? s : null;
        }
    }

    private Dictionary<TaskFeatureClassifier.Feature, FeatureStats> Aggregate()
    {
        var result = new Dictionary<TaskFeatureClassifier.Feature, FeatureStats>();
        try
        {
            if (!File.Exists(_metricsDbPath)) return result;
            using var conn = new SqliteConnection($"Data Source={_metricsDbPath}");
            conn.Open();

            // 按特征类聚合：总量/成功/耗时
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT COUNT(*), SUM(CASE WHEN success = 1 THEN 1 ELSE 0 END),
                       COALESCE(AVG(total_ms), 0), SUM(CASE WHEN success = 0 THEN 1 ELSE 0 END)
                FROM task_metrics";
            using var r = cmd.ExecuteReader();
            r.Read();
            var total = r.GetInt64(0);
            var success = r.GetInt64(1);
            var fail = r.GetInt64(3);

            // 失败主因：按 error_summary 简单归因（与 D1 词表一致）
            var causeCounts = new Dictionary<string, int>();
            using (var rc = conn.CreateCommand())
            {
                rc.CommandText = "SELECT user_input, error_summary FROM task_metrics WHERE success = 0";
                using var rr = rc.ExecuteReader();
                while (rr.Read())
                {
                    var cause = CauseOf(rr.GetString(0), rr.IsDBNull(1) ? "" : rr.GetString(1));
                    causeCounts[cause] = causeCounts.TryGetValue(cause, out var n) ? n + 1 : 1;
                }
            }
            var topCause = causeCounts.Count > 0
                ? causeCounts.OrderByDescending(kv => kv.Value).First().Key
                : "无";

            // 全量放 Other，然后按任务逐条分类
            var perFeature = new Dictionary<TaskFeatureClassifier.Feature, (long total, long ok, long ms, Dictionary<string, int> causes)>();
            using (var rc = conn.CreateCommand())
            {
                rc.CommandText = "SELECT user_input, success, total_ms, error_summary FROM task_metrics";
                using var rr = rc.ExecuteReader();
                while (rr.Read())
                {
                    var input = rr.GetString(0);
                    var okFlag = rr.GetInt64(1) == 1;
                    var ms = rr.IsDBNull(2) ? 0L : rr.GetInt64(2);
                    var err = rr.IsDBNull(3) ? "" : rr.GetString(3);
                    var f = TaskFeatureClassifier.Classify(input);
                    if (!perFeature.TryGetValue(f, out var agg))
                        agg = (0, 0, 0, new Dictionary<string, int>());
                    agg.total++;
                    if (okFlag) agg.ok++;
                    agg.ms += ms;
                    if (!okFlag)
                    {
                        var cause = CauseOf(input, err);
                        agg.causes[cause] = agg.causes.TryGetValue(cause, out var n) ? n + 1 : 1;
                    }
                    perFeature[f] = agg;
                }
            }

            foreach (var (f, agg) in perFeature)
            {
                var top = agg.causes.Count > 0 ? agg.causes.OrderByDescending(kv => kv.Value).First().Key : "无";
                result[f] = new FeatureStats(
                    (int)agg.total, (int)agg.ok,
                    agg.total > 0 ? (double)agg.ms / agg.total : 0,
                    top);
            }
        }
        catch
        {
            // 聚合失败不阻断主流程
        }
        return result;
    }

    /// <summary>失败原因归因（与 D1 词表一致，纯文本确定性）。</summary>
    public static string CauseOf(string input, string error)
    {
        var s = (error ?? "") + " " + (input ?? "");
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"CS\d{4}|error CS|重复定义|类型重复", RegexOptions.IgnoreCase))
            return "编译/类型冲突";
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"401|403|API Key|密钥|凭据|api_key", RegexOptions.IgnoreCase))
            return "外部凭据";
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"超时|timeout|124|Console\.ReadLine|input\(\)", RegexOptions.IgnoreCase))
            return "超时/交互";
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"空答案|空内容|空响应", RegexOptions.IgnoreCase))
            return "模型空响应";
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"模板|疑似模板", RegexOptions.IgnoreCase))
            return "模板误判";
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"输出与预期|预期不一致|严格模式|语义", RegexOptions.IgnoreCase))
            return "语义/验证";
        return "其他";
    }
}
