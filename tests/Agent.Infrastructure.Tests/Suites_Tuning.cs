// ============================================================
// Suites_Tuning.cs
// 模型端点与路由调权（三角色温度 / 时代隔离 / P2 agent_pro 自动调权）
// 区段：1948 / 1976 / 2004
// 依赖：using static AgentTests.TestHarness（Check / TestTmp）
// ============================================================
using static AgentTests.TestHarness;

namespace AgentTests;

public static class Suites_Tuning
{
    // ── 区段 1948：单模型三角色温度注入 ──
    public static void Run1948()
    {
        Console.WriteLine("== 单模型三角色温度注入 ==");
        {
            var opts = new Agent.Core.Models.AgentOptions(); // 默认：small 0.3 / large 0.7 / agent_pro 0.5 / 全局 0.2

            // 端点映射
            Check("GetEndpoint small", opts.GetEndpoint("small") == opts.Small);
            Check("GetEndpoint large", opts.GetEndpoint("large") == opts.Large);
            Check("GetEndpoint agent_pro", opts.GetEndpoint("agent_pro") == opts.AgentPro);
            Check("GetEndpoint classifier", opts.GetEndpoint("classifier") == opts.Classifier);
            Check("GetEndpoint 未知 → null", opts.GetEndpoint("unknown") is null);

            // 生效温度：端点显式配置优先
            Check("small 温度 0.3", Math.Abs(opts.GetEffectiveTemperature("small") - 0.3) < 1e-9);
            Check("large 温度 0.7", Math.Abs(opts.GetEffectiveTemperature("large") - 0.7) < 1e-9);
            Check("agent_pro 温度 0.5", Math.Abs(opts.GetEffectiveTemperature("agent_pro") - 0.5) < 1e-9);

            // 单模型三角色核心：三角色指向同一 tag（默认配置）
            var unified = new Agent.Core.Models.AgentOptions();
            Check("三角色同 tag", unified.Small.Model == unified.Large.Model
                && unified.Large.Model == unified.AgentPro.Model && unified.Small.Model == "law-unified");

            // 未配置端点温度 → 回退全局
            opts.Large = new Agent.Core.Models.ModelEndpoint { Model = "x", Temperature = null };
            Check("端点温度 null → 全局回退", Math.Abs(opts.GetEffectiveTemperature("large") - opts.Temperature) < 1e-9);
            Check("未知服务 → 全局回退", Math.Abs(opts.GetEffectiveTemperature("unknown") - opts.Temperature) < 1e-9);
        }
    }

    // ── 区段 1976：时代隔离：GetEpochBreakdownAsync ──
    public static void Run1976()
    {
        Console.WriteLine("== 时代隔离：GetEpochBreakdownAsync ==");
        {
            var mdb = Path.Combine(TestTmp, "metrics_epoch_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6] + ".db");
            var mc = new Agent.Infrastructure.Metrics.SqliteMetricsCollector(mdb);
            var epoch = new DateTimeOffset(2026, 10, 2, 15, 30, 0, TimeSpan.FromHours(8));
            var legacyAt = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.FromHours(8));
            var currentAt = new DateTimeOffset(2026, 10, 3, 10, 0, 0, TimeSpan.FromHours(8));

            Agent.Core.Models.TaskMetric Msg(string id, bool ok, DateTimeOffset at) => new()
            {
                TaskId = id, SessionId = "s", UserInput = "时代测试", Mode = "agent",
                RoutingTier = "Rule", ServiceId = "agent_pro", RoutingReason = "test",
                Success = ok, TotalMs = 1000, StartedAt = at, FinishedAt = at
            };
            mc.RecordAsync(Msg("t1", true, legacyAt), CancellationToken.None).GetAwaiter().GetResult();
            mc.RecordAsync(Msg("t2", false, legacyAt), CancellationToken.None).GetAwaiter().GetResult();
            mc.RecordAsync(Msg("t3", false, legacyAt), CancellationToken.None).GetAwaiter().GetResult();
            mc.RecordAsync(Msg("t4", true, currentAt), CancellationToken.None).GetAwaiter().GetResult();
            mc.RecordAsync(Msg("t5", true, currentAt), CancellationToken.None).GetAwaiter().GetResult();

            var (cur, leg) = mc.GetEpochBreakdownAsync("agent_pro", epoch, CancellationToken.None).GetAwaiter().GetResult();
            Check("30B 时代 2 条", cur.TotalCount == 2);
            Check("30B 时代全成功", cur.SuccessCount == 2);
            Check("4B 历史 3 条", leg.TotalCount == 3);
            Check("4B 历史 1 成功", leg.SuccessCount == 1);
        }
    }

    // ── 区段 2004：P2 自动调权：GetServiceStatsSinceAsync + TrySuggestDropAgentPro ──
    public static void Run2004()
    {
        Console.WriteLine("== P2 自动调权：GetServiceStatsSinceAsync + TrySuggestDropAgentPro ==");
        {
            var mdb = Path.Combine(TestTmp, "metrics_tune_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6] + ".db");
            var mc = new Agent.Infrastructure.Metrics.SqliteMetricsCollector(mdb);
            var epoch = new DateTimeOffset(2026, 10, 2, 15, 0, 0, TimeSpan.FromHours(8));
            var legacyAt = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.FromHours(8));
            var currentAt = new DateTimeOffset(2026, 10, 3, 10, 0, 0, TimeSpan.FromHours(8));

            Agent.Core.Models.TaskMetric Msg(string id, bool ok, DateTimeOffset at) => new()
            {
                TaskId = id, SessionId = "s", UserInput = "P2 调权测试", Mode = "agent",
                RoutingTier = "Rule", ServiceId = "agent_pro", RoutingReason = "test",
                Success = ok, TotalMs = 1000, StartedAt = at, FinishedAt = at
            };
            // 4B 历史 2 条（1 成功）——应被时代过滤排除
            mc.RecordAsync(Msg("old1", true, legacyAt), CancellationToken.None).GetAwaiter().GetResult();
            mc.RecordAsync(Msg("old2", false, legacyAt), CancellationToken.None).GetAwaiter().GetResult();
            // 30B 时代 2 条（2 成功）——应被保留
            mc.RecordAsync(Msg("new1", true, currentAt), CancellationToken.None).GetAwaiter().GetResult();
            mc.RecordAsync(Msg("new2", true, currentAt), CancellationToken.None).GetAwaiter().GetResult();

            var since = mc.GetServiceStatsSinceAsync("agent_pro", "Rule", epoch, 10, CancellationToken.None).GetAwaiter().GetResult();
            Check("P2 时代过滤：仅 30B 2 条", since.TotalCount == 2);
            Check("P2 时代过滤：2 成功", since.SuccessCount == 2);
            var all = mc.GetServiceStatsAsync("agent_pro", "Rule", 10, CancellationToken.None).GetAwaiter().GetResult();
            Check("P2 对照全量：4 条", all.TotalCount == 4);
            Check("P2 对照全量：3 成功（含 4B）", all.SuccessCount == 3);

            // TrySuggestDropAgentPro 三态
            Check("P2 样本不足(<5)保持 agent_pro",
                !Agent.Infrastructure.Routing.RoutingIntelligence.TrySuggestDropAgentPro(
                    new Agent.Infrastructure.Routing.ServiceStats(4, 2, 1000), 5, 0.5, out _));
            Check("P2 成功率 0.4 <0.5 → 降级 large",
                Agent.Infrastructure.Routing.RoutingIntelligence.TrySuggestDropAgentPro(
                    new Agent.Infrastructure.Routing.ServiceStats(5, 2, 1000), 5, 0.5, out var r1)
                && r1.Contains("降级 large"));
            Check("P2 成功率 0.6 ≥0.5 → 保持 agent_pro",
                !Agent.Infrastructure.Routing.RoutingIntelligence.TrySuggestDropAgentPro(
                    new Agent.Infrastructure.Routing.ServiceStats(5, 3, 1000), 5, 0.5, out _));
        }
    }
}
