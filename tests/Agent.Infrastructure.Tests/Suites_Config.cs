// ============================================================
// Suites_Config.cs
// 配置与上下文治理（AgentConfigValidator / 会话历史生命周期 /
// 工具结果注入归一 / 上下文折叠）
// 区段：3667 / 3690 / 3713 / 3750
// 依赖：using static AgentTests.TestHarness（Check）
// ============================================================
using static AgentTests.TestHarness;

namespace AgentTests;

public static class Suites_Config
{
    // ── 区段 3667：10-13 批次1：配置校验（AgentConfigValidator） ──
    public static void Run3667()
    {
        Console.WriteLine("== 10-13 配置校验 ==");
        {
            var ok = new Agent.Core.Models.AgentOptions();
            Check("配置：默认配置零错误", Agent.Core.Config.AgentConfigValidator.Validate(ok).Count == 0);

            var bad = new Agent.Core.Models.AgentOptions { ContextFoldThreshold = 10000, ContextToolResultBytes = 25000 };
            Check("配置：Fold < ToolResult → 报矛盾", Agent.Core.Config.AgentConfigValidator.Validate(bad).Any(x => x.Contains("ContextFoldThreshold")));

            var bad2 = new Agent.Core.Models.AgentOptions { MaxSteps = 0 };
            Check("配置：MaxSteps=0 → 报错", Agent.Core.Config.AgentConfigValidator.Validate(bad2).Any(x => x.Contains("MaxSteps")));

            var bad3 = new Agent.Core.Models.AgentOptions { RoutingDowngradeMinSuccessRate = 1.5 };
            Check("配置：路由率>1 → 报错", Agent.Core.Config.AgentConfigValidator.Validate(bad3).Any(x => x.Contains("RoutingDowngradeMinSuccessRate")));

            var bad4 = new Agent.Core.Models.AgentOptions();
            bad4.Small.Provider = "unknown";
            Check("配置：Provider 非法 → 报错", Agent.Core.Config.AgentConfigValidator.Validate(bad4).Any(x => x.Contains("Small.Provider")));

            var bad5 = new Agent.Core.Models.AgentOptions { OllamaHttpTimeoutMinutes = 0 };
            Check("配置：超时=0 → 报错", Agent.Core.Config.AgentConfigValidator.Validate(bad5).Any(x => x.Contains("OllamaHttpTimeoutMinutes")));
        }
    }

    // ── 区段 3690：10-13 批次1：会话历史生命周期（SqliteSessionStore.PruneOlderThan） ──
    public static void Run3690()
    {
        Console.WriteLine("== 10-13 会话历史生命周期 ==");
        {
            var wsS = Path.Combine(Path.GetTempPath(), "law_turns_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(wsS);
            var storeS = new Agent.Infrastructure.Session.SqliteSessionStore(Path.Combine(wsS, "s.db"));
            // 直接插入一条"10 天前"的旧轮次（AppendTurnAsync 恒写 UtcNow，构造过期轮次走原始 SQL）
            using (var conn0 = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(wsS, "s.db")}"))
            {
                conn0.Open();
                using var cmd0 = conn0.CreateCommand();
                cmd0.CommandText = "INSERT INTO turns(session_id, role, content, created_at) VALUES ('s1','user','old', $ts);";
                cmd0.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.AddDays(-10).ToUnixTimeSeconds());
                cmd0.ExecuteNonQuery();
            }
            storeS.AppendTurnAsync("s1", new Agent.Core.Models.ChatTurn("user", "new"), default).GetAwaiter().GetResult();
            var prunedS = storeS.PruneOlderThan(5);
            Check("会话清理：5 天保留期 → 只删 10 天前（1 条）", prunedS == 1);
            var histS = storeS.GetHistoryAsync("s1", 10, default).GetAwaiter().GetResult();
            Check("会话清理：清理后只剩新轮次", histS.Count == 1 && histS[0].Content == "new");
            try { Directory.Delete(wsS, true); } catch { }
        }
    }

    // ── 区段 3713：10-13 批次2：工具结果注入归一（ToolResultNormalizer 统一截断） ──
    public static void Run3713()
    {
        Console.WriteLine("== 10-13 注入归一 ==");
        {
            var short1 = Agent.Infrastructure.Context.ToolResultNormalizer.Truncate("abc");
            Check("归一：短内容原样返回", short1 == "abc");
            var long1 = Agent.Infrastructure.Context.ToolResultNormalizer.Truncate(new string('x', 1000), head: 10, tail: 5);
            Check("归一：超限头尾截断+总字符提示", long1.StartsWith(new string('x', 10)) && long1.Contains("共 1000 字符") && long1.EndsWith(new string('x', 5)));
            var headOnly = Agent.Infrastructure.Context.ToolResultNormalizer.Truncate(new string('y', 3000), head: 1500, tail: 0);
            Check("归一：tail=0 仅头 + 提示（Executor 预览语义）", headOnly.StartsWith(new string('y', 1500)) && headOnly.Contains("共 3000 字符") && !headOnly.Contains(new string('y', 1501)));
            var boundary = Agent.Infrastructure.Context.ToolResultNormalizer.Truncate(new string('z', 700), head: 400, tail: 300);
            Check("归一：恰好 head+tail 不截断", boundary.Length == 700);
            var empty = Agent.Infrastructure.Context.ToolResultNormalizer.Truncate("");
            Check("归一：空串原样", empty == "");
        }
    }

    // ── 区段 3750：10-13 上下文折叠（ContextFolder） ──
    public static void Run3750()
    {
        Console.WriteLine("== 10-13 上下文折叠 ==");
        var bigCode = new string('x', 9000) + "\npublic class OrderService {\n    public int Get() { return 1; }\n}\n" + new string('y', 3000);
        var folded = Agent.Infrastructure.Context.ContextFolder.FoldToolResult(bigCode, "OrderService.cs");
        Check("折叠：代码输出提取结构定义", folded.Contains("public class OrderService") && folded.Contains("[上下文折叠]"));
        Check("折叠：头尾保留", folded.Contains("-- 开头 --") && folded.Contains("-- 结尾（省略中间"));
        var foldedTxt = Agent.Infrastructure.Context.ContextFolder.FoldToolResult(bigCode, null);
        Check("折叠：未知类型走文本策略（无结构定义节）", !foldedTxt.Contains("-- 结构定义 --") && foldedTxt.Contains("[上下文折叠]"));
        Check("折叠：短内容不折叠", Agent.Infrastructure.Context.ContextFolder.FoldToolResult("short", "a.cs") == "short");
        var bigHtml = new string('h', 10000) + "\n<div class=\"box\">x</div>\n" + new string('i', 3000);
        Check("折叠：html 按代码策略（结构定义节可空但保留折叠头）", Agent.Infrastructure.Context.ContextFolder.FoldToolResult(bigHtml, "page.html").Contains("[上下文折叠]"));
    }
}
