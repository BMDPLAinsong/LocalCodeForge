// ============================================================
// Suites_Routing.cs
// 批次5：tests 单文件治理拆分产物。
// 区段来源（原 Program.cs 行号）：634（路由修复 A+B）/645（LLM 异常升级兜底）/
//   756（TaskClassifier）/782（IntentOverride）/804（ExtractTargetFile）/825（读改混合）/
//   847（TryLoadFileContent）/869（写任务判定）/906（RoutingIntelligence）/933（读任务数据闭环）/
//   971（D2 特征分类+聚合）/1055（3A 决策落库）/1121（C 拒绝落库）/3100（RoutingRules 自检）/
//   3636（收敛判定）/3645（熔断语义 + cwd）。
// 块体原样搬移，只改顶层全局引用（TestTmp/testTmpRoot/Check/TryCompileRegex → using static TestHarness）。
// RoutingRuleCheck 类（原 3926 行）随 3100 区段迁入本文件顶层。
// ============================================================
using System.Text.Json;
using static AgentTests.TestHarness;

namespace AgentTests;

/// <summary>RoutingRules.json 单条规则的最小结构（RuleEngine.RuleDto 为 private，此处独立定义）。</summary>
sealed class RoutingRuleCheck
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "regex";
    public string Target { get; set; } = "small";
    public int Priority { get; set; }
    public string? Pattern { get; set; }
    public List<string>? Keywords { get; set; }
}

public static class Suites_Routing
{
    // ── 区段 634：路由层修复 A+B：写任务直连 + 空响应跳层（§14-3w） ──
    public static void Run634()
    {
        Console.WriteLine("== 路由层修复 A+B：写任务直连 + 空响应跳层（§14-3w） ==");
        {
            // B：空响应升级跳层——4B 空响应直接跳 large
            Check("small 空响应 → 直达 large", Agent.Infrastructure.AgentLoop.PromoteService("small") == "large");
            Check("agent_pro 空响应 → 直达 large", Agent.Infrastructure.AgentLoop.PromoteService("agent_pro") == "large");
            Check("large 空响应 → null（原层重试）", Agent.Infrastructure.AgentLoop.PromoteService("large") is null);

            // A：写任务判定（IsWriteTask 覆盖已测）——工程可写条件移除由 ModelRouter 运行时日志验证
            Check("写任务判定稳定", Agent.Infrastructure.Routing.IntentOverride.IsWriteTask("创建一个 hello.txt 文件，内容为 hello"));
        }
    }

    // ── 区段 645：10-08 真实失败收尾 B：LLM 异常升级兜底 ──
    public static void Run645()
    {
        Console.WriteLine("== 10-08 真实失败收尾 B：LLM 异常升级兜底 ==");
        {
            // B：AgentLoop catch 处异常（canceled/超时/连接）→ PromoteService 升级一次；
            // 最高层（large）无升级目标 → 判失败。Error 统一前缀 "LLM 调用失败："。
            Check("LLM 调用异常 → llm_error 类别（非 empty_response）",
                Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("LLM 调用失败：任务已取消") == "llm_error");
            Check("LLM 调用异常（超时型）→ llm_error 类别",
                Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("LLM 调用失败：请求超时") == "llm_error");
            Check("异常升级语义：large 已最高层无升级目标（判失败路径）",
                Agent.Infrastructure.AgentLoop.PromoteService("large") is null);
            Check("异常升级语义：small/agent_pro 可升 large（复用空响应链映射）",
                Agent.Infrastructure.AgentLoop.PromoteService("small") == "large"
                && Agent.Infrastructure.AgentLoop.PromoteService("agent_pro") == "large");
        }
    }

    // ── 区段 756：TaskClassifier 编排判定（§14-3w） ──
    public static void Run756()
    {
        Console.WriteLine("== TaskClassifier 编排判定（§14-3w） ==");
        {
            bool Orc(string input) => Agent.Infrastructure.Orchestration.TaskClassifier.ShouldOrchestrate(input);

            Check("读取工程文件长描述 → 走 Agent（不编排）",
                !Orc("读取 src/Agent.Core/Models/AgentOptions.cs 中 ProjectRoot 属性附近的内容"));
            Check("构建验证任务 → 走 Agent（不编排）",
                !Orc("运行 dotnet build 检查工程能否构建"));
            Check("多文件项目 → 编排（强多文件词）",
                Orc("帮我做一个 C# 项目，包含订单模型、订单服务、主程序，多文件组织"));
            Check("学生管理系统多文件 → 编排（回归）",
                Orc("设计一个学生管理系统，使用C#多文件组织"));
            Check("天气方法 → 走 Agent（回归）",
                !Orc("写一个获取天气信息的方法"));
            Check("奇数偶数函数 → 走 Agent（回归）",
                !Orc("用 C# 写一个判断奇数偶数函数"));
            Check("订单明细项目 → 编排（系统/服务词）",
                Orc("用 C# 写一个订单明细项目，分 Order 模型、OrderItem 模型、OrderService 服务、Program 入口，多文件组织"));
            Check("添加注释到具体文件（长路径）→ 走 Agent（文件引用排除）",
                !Orc("在 src/Agent.Core/Models/AgentOptions.cs 中 ProjectRoot 属性定义之后添加一行注释：// P1 冒烟验证标记"));
            Check("带路径修改任务 → 走 Agent（文件引用排除）",
                !Orc("修改 src/Agent.Core/Models/AgentOptions.cs 中 ProjectWriteEnabled 属性的注释说明"));
            Check("含多文件词的工程任务 → 仍编排（强多文件词优先于文件引用）",
                Orc("做一个多文件的 C# 项目：src/Agent.Core/Models/AgentOptions.cs 和 Program.cs"));
        }
    }

    // ── 区段 782：IntentOverride 意图覆盖（§14-3w） ──
    public static void Run782()
    {
        Console.WriteLine("== IntentOverride 意图覆盖（§14-3w） ==");
        {
            Agent.Infrastructure.Routing.IntentType Ov(Agent.Infrastructure.Routing.IntentType i, string s) => Agent.Infrastructure.Routing.IntentOverride.Apply(i, s);

            Check("SimpleQA + 添加注释到文件 → CodeGenerate",
                Ov(Agent.Infrastructure.Routing.IntentType.SimpleQA, "在 src/Agent.Core/Models/AgentOptions.cs 中 ProjectRoot 属性定义之后添加一行注释：// 标记") == Agent.Infrastructure.Routing.IntentType.CodeGenerate);
            // 【A 补全】"加一行注释"（日志实测缺口：OperationVerbs 缺单字"加" → 误判 SimpleQA）
            Check("加一行注释 → SimpleQA 覆盖为 CodeGenerate",
                Ov(Agent.Infrastructure.Routing.IntentType.SimpleQA, "在 BigService.cs 的 Method1500 方法里加一行注释 // 已审查") == Agent.Infrastructure.Routing.IntentType.CodeGenerate);
            Check("SimpleQA + 修改代码 → CodeGenerate",
                Ov(Agent.Infrastructure.Routing.IntentType.SimpleQA, "修改 AgentOptions.cs 中 ProjectWriteEnabled 属性的注释说明") == Agent.Infrastructure.Routing.IntentType.CodeGenerate);
            Check("SimpleQA + 纯问答 → 保持 SimpleQA",
                Ov(Agent.Infrastructure.Routing.IntentType.SimpleQA, "今天天气怎么样") == Agent.Infrastructure.Routing.IntentType.SimpleQA);
            Check("SimpleQA + 操作词但无文件语境 → 保持 SimpleQA",
                Ov(Agent.Infrastructure.Routing.IntentType.SimpleQA, "帮我添加一些建议") == Agent.Infrastructure.Routing.IntentType.SimpleQA);
            Check("非 SimpleQA 原样返回",
                Ov(Agent.Infrastructure.Routing.IntentType.FileProcess, "读取 src/Agent.Core/Models/AgentOptions.cs") == Agent.Infrastructure.Routing.IntentType.FileProcess);
            Check("空输入保持原样",
                Ov(Agent.Infrastructure.Routing.IntentType.SimpleQA, "   ") == Agent.Infrastructure.Routing.IntentType.SimpleQA);
        }
    }

    // ── 区段 804：【目标文件注入】ExtractTargetFile 数据驱动提取（最小版） ──
    public static void Run804()
    {
        Console.WriteLine("== ExtractTargetFile 目标文件提取 ==");
        {
            string? Tf(string s) => Agent.Infrastructure.AgentLoop.ExtractTargetFile(s);

            Check("提取 BigService.cs",
                Tf("在 BigService.cs 的 Method1500 方法里加一行注释") == "BigService.cs");
            Check("提取 py 文件",
                Tf("修改 weather.py 里的超时参数") == "weather.py");
            Check("提取 csproj",
                Tf("给 OrderSystem.csproj 添加包引用") == "OrderSystem.csproj");
            Check("提取 html（10-12 双正则漂移修复）",
                Tf("index.html 读取并修复这个文件") == "index.html");
            Check("提取 htm",
                Tf("修复 page.htm 里的样式") == "page.htm");
            Check("无文件名 → null",
                Tf("帮我分析一下这个项目的架构") == null);
            Check("空输入 → null",
                Tf("   ") == null);
        }
    }

    // ── 区段 825：【2026-10-12 方案A】读改混合任务判定（L0.5 不降级依据：读+写动词同时命中） ──
    public static void Run825()
    {
        Console.WriteLine("== 读改混合任务判定（L0.5 排除降级） ==");
        {
            bool Mixed(string s) => Agent.Infrastructure.Routing.IntentOverride.IsWriteTask(s)
                && Agent.Infrastructure.Routing.IntentOverride.IsReadTask(s);
            bool PureWrite(string s) => Agent.Infrastructure.Routing.IntentOverride.IsWriteTask(s)
                && !Agent.Infrastructure.Routing.IntentOverride.IsReadTask(s);

            Check("读改混合（读取+修复）→ 混合",
                Mixed("index.html 读取这个文件，并对这个文件进行修复"));
            Check("读改混合（查看+修改）→ 混合",
                Mixed("查看 Program.cs 并修改其中的 Main 方法"));
            Check("读改混合（检查+加注释）→ 混合",
                Mixed("检查 BigService.cs 的 Method1500 并加一行注释"));
            Check("纯新建写任务（写一个）→ 非混合",
                !Mixed("写一个 C# 判断奇数偶数函数") && PureWrite("写一个 C# 判断奇数偶数函数"));
            Check("纯读任务（读取）→ 非混合",
                !Mixed("读取 src/Order.cs 的内容"));
            Check("纯写任务（创建文件）→ 非混合",
                !Mixed("创建一个 hello.txt 文件，内容为 hello"));
        }
    }

    // ── 区段 847：【2026-10-12 方案B】目标文件内容注入（TryLoadFileContent） ──
    public static void Run847()
    {
        Console.WriteLine("== TryLoadFileContent 目标文件内容注入 ==");
        {
            var baseDir = Path.Combine(TestTmpRoot, "tlfc_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(baseDir);
            try
            {
                File.WriteAllText(Path.Combine(baseDir, "target.txt"), "hello 内容注入");
                Check("命中已存在文件 → 返回内容",
                    Agent.Infrastructure.AgentLoop.TryLoadFileContent(baseDir, "target.txt", out var c1)
                    && c1 == "hello 内容注入");
                Check("文件不存在 → false",
                    !Agent.Infrastructure.AgentLoop.TryLoadFileContent(baseDir, "missing.cs", out _));
                File.WriteAllText(Path.Combine(baseDir, "big.txt"), new string('x', 300 * 1024));
                Check("超大文件（>200KB）→ false（位置指引兜底）",
                    !Agent.Infrastructure.AgentLoop.TryLoadFileContent(baseDir, "big.txt", out _));
            }
            finally
            {
                Directory.Delete(baseDir, true);
            }
        }
    }

    // ── 区段 869：IntentOverride 写任务判定（§14-3w 直连 large） ──
    public static void Run869()
    {
        Console.WriteLine("== IntentOverride 写任务判定（§14-3w 直连 large） ==");
        {
            bool Wr(string s) => Agent.Infrastructure.Routing.IntentOverride.IsWriteTask(s);

            Check("添加注释到文件 → 写任务",
                Wr("在 src/Agent.Core/Models/AgentOptions.cs 中 ProjectRoot 属性定义之后添加一行注释：// 标记"));
            Check("修改代码 → 写任务",
                Wr("修改 AgentOptions.cs 中 ProjectWriteEnabled 属性的注释说明"));
            Check("创建新文件 → 写任务",
                Wr("创建一个 Program.cs 文件"));
            Check("新建文件 → 写任务（10-08 词表补全：缺'新建'致写任务误判 SimpleQA 幻觉完成）",
                Wr("在X盘根目录新建一个空的 _law_e2e_test.txt 文件"));
            Check("纯问答 → 非写任务",
                !Wr("今天天气怎么样"));
            Check("读取文件 → 非写任务（读保持 small）",
                !Wr("读取 src/Agent.Core/Models/AgentOptions.cs"));
            // 【A 补全】"加一行注释"（日志实测缺口：OperationVerbs 缺单字"加" → 误判 SimpleQA）
            Check("加一行注释 → 写任务（词表补全）",
                Wr("在 BigService.cs 的 Method1500 方法里加一行注释 // 已审查"));
            Check("列出目录 → 非写任务",
                !Wr("列出 src/Agent.Core/Models 目录"));
            Check("操作词但无文件语境 → 非写任务",
                !Wr("帮我添加一些建议"));

            // ── 方案 B：写动词词表补全（写/更改/改/生成…）──
            Check("写一个…脚本 → 写任务",
                Wr("写一个名为pyst.py的脚本，脚本任务是打印 hello world"));
            Check("更改这个脚本 → 写任务",
                Wr("更改这个脚本，将里面内容改成判断整数偶数的方法"));
            Check("生成一个Python脚本 → 写任务",
                Wr("生成一个Python脚本"));
            Check("改为…方法 → 写任务",
                Wr("将脚本内容改为判断整数偶数的方法"));
            Check("纯打印无语境 → 非写任务",
                !Wr("打印 hello"));
        }
    }

    // ── 区段 906：数据闭环：RoutingIntelligence 调权阈值（保守三条件） ──
    public static void Run906()
    {
        Console.WriteLine("== 数据闭环 RoutingIntelligence（历史表现回流调权） ==");
        {
            bool Suggest(Agent.Infrastructure.Routing.ServiceStats cand,
                         Agent.Infrastructure.Routing.ServiceStats cur,
                         out string? reason)
                => Agent.Infrastructure.Routing.RoutingIntelligence.TrySuggestDowngrade(
                    cand, cur, minSamples: 3, minSuccessRate: 0.6, out reason);

            // 1) 样本不足 → 不降级
            Check("样本不足（2 条）→ 不降级",
                !Suggest(new(2, 2, 50_000), new(1, 1, 600_000), out _));
            // 2) 成功率低 → 不降级
            Check("成功率低（3 条 1 成功）→ 不降级",
                !Suggest(new(3, 1, 50_000), new(3, 3, 600_000), out _));
            // 3) 不省时（candidate 平均 ≥ current 平均）→ 不降级
            Check("不省时 → 不降级",
                !Suggest(new(4, 3, 700_000), new(4, 3, 600_000), out _));
            // 4) 达标（样本足 + 高成功率 + 显著省时）→ 降级
            Check("达标 → 降级",
                Suggest(new(5, 4, 80_000), new(5, 4, 600_000), out var reason4)
                && reason4!.Contains("成功率"));
            // 5) 达标且当前无历史 → 降级
            Check("当前无历史 + 候选达标 → 降级",
                Suggest(new(4, 3, 80_000), new(0, 0, 0), out _));
        }
    }

    // ── 区段 933：读任务数据闭环：IsReadTask + TrySuggestReadTier ──
    public static void Run933()
    {
        Console.WriteLine("== 读任务数据闭环（small → agent_pro 升级判定） ==");
        {
            // IsReadTask：读动词 + 代码语境
            Check("读取文件 → 读任务", Agent.Infrastructure.Routing.IntentOverride.IsReadTask("读取 src/Order.cs 的内容"));
            Check("分析代码 → 读任务", Agent.Infrastructure.Routing.IntentOverride.IsReadTask("分析一下这个函数的逻辑"));
            Check("查看结构 → 读任务", Agent.Infrastructure.Routing.IntentOverride.IsReadTask("看看这个类的结构"));
            Check("纯问答 → 非读任务", !Agent.Infrastructure.Routing.IntentOverride.IsReadTask("什么是依赖注入"));
            Check("写任务（修改）→ 非读任务", !Agent.Infrastructure.Routing.IntentOverride.IsReadTask("修改 Order.cs 的价格属性"));
            Check("无代码语境 → 非读任务", !Agent.Infrastructure.Routing.IntentOverride.IsReadTask("看看天气"));

            bool SuggestRead(Agent.Infrastructure.Routing.ServiceStats pro,
                             Agent.Infrastructure.Routing.ServiceStats small,
                             out string? reason)
                => Agent.Infrastructure.Routing.RoutingIntelligence.TrySuggestReadTier(
                    pro, small, minSamples: 3, badRate: 0.6, minSuccessRate: 0.5, out reason);

            // 1) small 样本不足 → 保持 small
            Check("small 样本不足 → 不升级",
                !SuggestRead(new(5, 4, 80_000), new(2, 1, 300_000), out _));
            // 2) small 成功率高（≥0.6）→ 保持 small
            Check("small 成功率高 → 不升级",
                !SuggestRead(new(5, 4, 80_000), new(5, 4, 300_000), out _));
            // 3) agent_pro 样本不足 → 保持 small
            Check("agent_pro 样本不足 → 不升级",
                !SuggestRead(new(2, 2, 80_000), new(5, 2, 300_000), out _));
            // 4) agent_pro 成功率低 → 保持 small
            Check("agent_pro 成功率低 → 不升级",
                !SuggestRead(new(4, 1, 80_000), new(5, 2, 300_000), out _));
            // 5) agent_pro 不省时 → 保持 small
            Check("agent_pro 不省时 → 不升级",
                !SuggestRead(new(5, 4, 350_000), new(5, 2, 300_000), out _));
            // 6) 全达标（small 差 + pro 足/达标/省时）→ 升级 agent_pro
            Check("达标 → 升级 agent_pro",
                SuggestRead(new(5, 4, 80_000), new(5, 2, 300_000), out var r6)
                && r6!.Contains("偏低"));
        }
    }

    // ── 区段 971：D2：任务特征分类 + 历史聚合（路由数据校准） ──
    public static void Run971()
    {
        Console.WriteLine("== D2 TaskFeatureClassifier + TaskMetricsAggregator ==");
        {
            // 分类（确定性关键词）
            Check("工程增量：在工程中新增类 → ProjectIncremental",
                Agent.Infrastructure.Routing.TaskFeatureClassifier.Classify("在工程中新增一个 ShippingService 类") ==
                Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.ProjectIncremental);
            Check("工程增量：修改已有文件加方法 → ProjectIncremental",
                Agent.Infrastructure.Routing.TaskFeatureClassifier.Classify("在 OrderService.cs 中新增一个方法") ==
                Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.ProjectIncremental);
            Check("外部凭据：查天气 → ExternalCredential",
                Agent.Infrastructure.Routing.TaskFeatureClassifier.Classify("写一个获取天气的方法") ==
                Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.ExternalCredential);
            Check("外部凭据：查 IP 天气 → ExternalCredential",
                Agent.Infrastructure.Routing.TaskFeatureClassifier.Classify("根据IP地址获取天气信息") ==
                Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.ExternalCredential);
            Check("读/查：读取工程文件 → ReadQuery",
                Agent.Infrastructure.Routing.TaskFeatureClassifier.Classify("读取 Order.cs 的内容") ==
                Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.ReadQuery);
            Check("多文件：分模型服务入口 → MultiFile",
                Agent.Infrastructure.Routing.TaskFeatureClassifier.Classify("做一个学生管理系统，分模型、服务、入口，多文件组织") ==
                Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.MultiFile);
            Check("单文件：写一个 C# 函数 → SingleFile",
                Agent.Infrastructure.Routing.TaskFeatureClassifier.Classify("用 C# 写一个判断奇数偶数的函数") ==
                Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.SingleFile);
            Check("其他：纯闲聊 → Other",
                Agent.Infrastructure.Routing.TaskFeatureClassifier.Classify("你好") ==
                Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.Other);

            // 聚合器：临时 sqlite 造 metrics → 成功率/失败主因
            var aggDir = Path.Combine(TestTmp, "d2agg_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(aggDir);
            var aggDb = Path.Combine(aggDir, "metrics.db");
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={aggDb}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE task_metrics(
                        task_id TEXT, session_id TEXT, user_input TEXT, mode TEXT, routing_tier TEXT,
                        service_id TEXT, routing_reason TEXT, success INTEGER, is_suspicious INTEGER,
                        retry_count INTEGER, total_ms INTEGER, planner_ms INTEGER, executor_ms INTEGER,
                        validator_ms INTEGER, lint_blocking_errors INTEGER, lint_warning_errors INTEGER,
                        error_summary TEXT, started_at REAL, finished_at REAL)";
                cmd.ExecuteNonQuery();
                var ins = conn.CreateCommand();
                ins.CommandText = @"INSERT INTO task_metrics
                    (user_input, success, total_ms, error_summary) VALUES (@i, @s, @t, @e)";
                foreach (var (input, ok, ms, err) in new[]
                {
                    ("在工程中新增一个 ShippingService 类", 0, 120_000, "error CS0101: 重复定义"),
                    ("在工程中修改 OrderService.cs", 0, 150_000, "error CS0101: 重复定义"),
                    ("在工程中新增 TaxService", 0, 90_000, "提供的文件路径不存在"),
                    ("写一个获取天气的方法", 1, 60_000, ""),
                    ("写一个获取天气的方法", 1, 55_000, ""),
                    ("用 C# 写一个判断奇偶的函数", 1, 30_000, ""),
                })
                {
                    ins.Parameters.Clear();
                    ins.Parameters.AddWithValue("@i", input);
                    ins.Parameters.AddWithValue("@s", ok);
                    ins.Parameters.AddWithValue("@t", ms);
                    ins.Parameters.AddWithValue("@e", err);
                    ins.ExecuteNonQuery();
                }
            }
            var agg = new Agent.Infrastructure.Routing.TaskMetricsAggregator(aggDb);
            agg.Load();
            var projStats = agg.Get(Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.ProjectIncremental);
            Check("聚合：工程增量 3 条 0 成功",
                projStats is { TotalCount: 3, SuccessCount: 0 });
            Check("聚合：工程增量失败主因=编译/类型冲突",
                projStats!.TopFailureCause == "编译/类型冲突");
            var extStats = agg.Get(Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.ExternalCredential);
            Check("聚合：外部凭据 2 条 2 成功",
                extStats is { TotalCount: 2, SuccessCount: 2 });
            var singleStats = agg.Get(Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.SingleFile);
            Check("聚合：单文件 1 条 1 成功",
                singleStats is { TotalCount: 1, SuccessCount: 1 });
            var otherStats = agg.Get(Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.Other);
            Check("聚合：无其他类样本 → null",
                otherStats is null);
        }
    }

    // ── 区段 1055：3A：RoutingDecisionStore 路由决策落库 ──
    public static void Run1055()
    {
        Console.WriteLine("== 3A RoutingDecisionStore 路由决策落库 ==");
        {
            var rdDir = Path.Combine(TestTmp, "rd_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(rdDir);
            var rdDb = Path.Combine(rdDir, "metrics.db");
            var store = new Agent.Infrastructure.Routing.RoutingDecisionStore(rdDb);

            // 记录 3 条不同决策
            var ctNone = CancellationToken.None;
            store.RecordAsync(
                new Agent.Infrastructure.Routing.RoutingDecision(
                    Agent.Infrastructure.Routing.RoutingDecision.Large,
                    Agent.Infrastructure.Routing.RoutingTier.Rule,
                    "写任务直连 large"), "帮我写一个订单系统", null, ctNone).Wait();
            store.RecordAsync(
                new Agent.Infrastructure.Routing.RoutingDecision(
                    Agent.Infrastructure.Routing.RoutingDecision.Small,
                    Agent.Infrastructure.Routing.RoutingTier.Rule,
                    "查询类任务"), "看看有哪些文件", null, ctNone).Wait();
            // 超长输入截断断言
            var longInput = new string('x', 500);
            store.RecordAsync(
                new Agent.Infrastructure.Routing.RoutingDecision(
                    Agent.Infrastructure.Routing.RoutingDecision.Small,
                    Agent.Infrastructure.Routing.RoutingTier.SmallModel,
                    "意图 SimpleQA", Agent.Infrastructure.Routing.IntentType.SimpleQA),
                longInput, null, ctNone).Wait();

            var rdSum = store.GetSummaryAsync(ctNone).Result;
            Check("落库：总数 3", rdSum.Total == 3);
            Check("落库：按服务 large=1 small=2",
                rdSum.ByService.FirstOrDefault(x => x.Service == "large").Count == 1
                && rdSum.ByService.FirstOrDefault(x => x.Service == "small").Count == 2);
            Check("落库：按特征类 写任务=单文件生成 读任务=读/查",
                rdSum.ByFeature.FirstOrDefault(x => x.Feature == "单文件生成").Count >= 1
                && rdSum.ByFeature.FirstOrDefault(x => x.Feature == "读/查").Count >= 1);

            // 截断验证：直接查库
            using var rdConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={rdDb}");
            rdConn.Open();
            using var rdCmd = rdConn.CreateCommand();
            rdCmd.CommandText = "SELECT user_input, input_len FROM routing_decisions WHERE input_len = 200";
            using var rdReader = rdCmd.ExecuteReader();
            var truncated = rdReader.Read();
            Check("落库：超长输入截断到 200", truncated && rdReader.GetString(0).Length == 200);

            // 【10-10 真实口径下沉】路由决策 E2E 源头隔离：ModelRouter 判定开关（RecordDecisionAsync 的唯一守卫，
            // E2E 物理上不落库 → 未来 3B 调权按特征类/服务分布时不会踩 E2E 污染坑；orchestrator 任务本就不经
            // ModelRouter，决策表仅 agent 模式任务产生记录）。
            var rdEnvBefore = Environment.GetEnvironmentVariable("LAW_E2E");
            try
            {
                Environment.SetEnvironmentVariable("LAW_E2E", "1");
                Check("路由决策守卫：LAW_E2E=1 → 判定 E2E 运行（跳过落库）",
                    Agent.Infrastructure.Routing.ModelRouter.IsE2ERun);
                Environment.SetEnvironmentVariable("LAW_E2E", null);
                Check("路由决策守卫：LAW_E2E 清除 → 判定非 E2E（正常落库）",
                    !Agent.Infrastructure.Routing.ModelRouter.IsE2ERun);
            }
            finally
            {
                Environment.SetEnvironmentVariable("LAW_E2E", rdEnvBefore);
            }
        }
    }

    // ── 区段 1121：C：ToolRejectionStore 工具拒绝事件落库 ──
    public static void Run1121()
    {
        Console.WriteLine("== C ToolRejectionStore 工具拒绝事件落库 ==");
        {
            var trDir = Path.Combine(TestTmp, "tr_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(trDir);
            var trDb = Path.Combine(trDir, "metrics.db");
            var trStore = new Agent.Infrastructure.Metrics.ToolRejectionStore(trDb);
            var ctNone = CancellationToken.None;

            // 1) Store 单元：记录 2 类 3 条 + 聚合
            trStore.RecordAsync("write_file", "pd_conflict", "src/OrderService.cs", "t1", "错误：检测到类型重复定义：Order。", ctNone).Wait();
            trStore.RecordAsync("write_file", "pd_conflict", "src/OrderService.cs", "t1", "错误：第 2 次因类型重复被拒绝。", ctNone).Wait();
            trStore.RecordAsync("edit_file", "old_not_found", "src/A.cs", "t1", "错误：找不到 old_string。", ctNone).Wait();
            var trSum = trStore.GetSummaryAsync(ctNone).Result;
            Check("拒绝落库：总数 3", trSum.Total == 3);
            Check("拒绝落库：按类别 pd_conflict=2 old_not_found=1",
                trSum.ByCategory.FirstOrDefault(x => x.Category == "pd_conflict").Count == 2
                && trSum.ByCategory.FirstOrDefault(x => x.Category == "old_not_found").Count == 1);
            Check("拒绝落库：按工具 write_file=2 edit_file=1",
                trSum.ByTool.FirstOrDefault(x => x.Tool == "write_file").Count == 2
                && trSum.ByTool.FirstOrDefault(x => x.Tool == "edit_file").Count == 1);

            // 2) WriteFileTool 带 store：P-D 拒绝 → 事件落库（补度量盲区）
            var ws2 = Path.Combine(trDir, "ws");
            var proj2 = Path.Combine(trDir, "proj");
            Directory.CreateDirectory(ws2);
            Directory.CreateDirectory(Path.Combine(proj2, "src"));
            File.WriteAllText(Path.Combine(proj2, "src", "Order.cs"),
                "using System;\nclass Order\n{\n    public int Id { get; set; }\n}\n");
            var wRw2 = new Agent.Infrastructure.Tools.WriteFileTool(ws2, proj2, true, "t2", trStore);
            var json = $"{{\"path\":\"src/OrderService.cs\",\"content\":{JsonSerializer.Serialize("using System;\nclass Order\n{\n}\nclass OrderService\n{\n}\n")}}}";
            var trArgs = JsonSerializer.Deserialize<JsonElement>(json);
            var ret = wRw2.InvokeAsync(trArgs, ctNone).GetAwaiter().GetResult();
            Check("带 store 的 P-D 拒绝 → 仍返回拒绝",
                ret.Contains("类型重复定义"));
            var trSum2 = trStore.GetSummaryAsync(ctNone).Result;
            Check("带 store 的 P-D 拒绝 → 事件落库（pd_conflict 增至 3）",
                trSum2.ByCategory.FirstOrDefault(x => x.Category == "pd_conflict").Count == 3);

            // 3) 不带 store（tests 既有构造）→ 静默不炸
            var wNoStore = new Agent.Infrastructure.Tools.WriteFileTool(ws2);
            var ret2 = wNoStore.InvokeAsync(trArgs, ctNone).GetAwaiter().GetResult();
            Check("不带 store 构造 → 拒绝正常返回（无异常）",
                ret2.Contains("越界") || ret2.Contains("类型重复定义") || ret2.Contains("已写入"));
        }
    }

    // ── 区段 3100：配置结构自检：RoutingRules.json ──
    public static void Run3100()
    {
        Console.WriteLine("== 配置结构自检：RoutingRules.json ==");
        {
            // RoutingRules.json 只复制到 Infrastructure 输出目录（tests 输出无），回溯仓库根读取源文件
            var rulesJson = Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "src", "Agent.Infrastructure", "Routing", "RoutingRules.json");
            Check("RoutingRules.json 存在（仓库根回溯）", File.Exists(rulesJson));

            List<RoutingRuleCheck>? rules = null;
            if (File.Exists(rulesJson))
            {
                try
                {
                    var root = JsonDocument.Parse(File.ReadAllText(rulesJson)).RootElement;
                    rules = JsonSerializer.Deserialize<List<RoutingRuleCheck>>(
                        root.GetProperty("rules").GetRawText(),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch
                {
                    // 解析失败 → rules 保持 null，下方断言暴露
                }
            }
            Check("JSON 可解析且含 rules 数组", rules is not null);
            if (rules is not null)
            {
                Check("规则数 == 8（当前基线）", rules.Count == 8);
                Check("target 全合法（small/agent_pro/large）",
                    rules.All(r => r.Target is "small" or "agent_pro" or "large"));
                Check("无重名规则（防死规则回归）",
                    rules.Select(r => r.Name).Distinct().Count() == rules.Count);
                Check("priority 全 > 0", rules.All(r => r.Priority > 0));
                Check("type 全合法",
                    rules.All(r => r.Type is "regex" or "keyword_any" or "prefix_any"
                        or "length_min" or "length_max" or "length" or "after_failure"));
                Check("regex 规则 Pattern 非空且可编译",
                    rules.Where(r => r.Type == "regex")
                        .All(r => !string.IsNullOrEmpty(r.Pattern) && TryCompileRegex(r.Pattern!)));
                Check("keyword_any 规则 Keywords 非空",
                    rules.Where(r => r.Type == "keyword_any")
                        .All(r => r.Keywords is { Count: > 0 }));
            }
        }
    }

    // ── 区段 3636：10-10 方案4 读任务收敛判定 ──
    public static void Run3636()
    {
        Console.WriteLine("== 10-10 方案4 读任务收敛判定 ==");
        Check("收敛：无工具 + 长总结(≥30) → true", Agent.Infrastructure.AgentLoop.IsReadQueryConverged(false, "本项目是一个基于 .NET 的本地 AI 工作台，提供智能代理系统，用于执行代码生成、分析等任务，以下是简要分析："));
        Check("收敛：有工具 + 长总结 → false（工具步不算收敛）", !Agent.Infrastructure.AgentLoop.IsReadQueryConverged(true, "本项目是一个基于 .NET 的本地 AI 工作台，提供智能代理系统，用于执行代码生成、分析等任务，以下是简要分析："));
        Check("收敛：无工具 + 短过渡文本(<30) → false", !Agent.Infrastructure.AgentLoop.IsReadQueryConverged(false, "让我继续读取文件。"));
        Check("收敛：无工具 + 空白 → false", !Agent.Infrastructure.AgentLoop.IsReadQueryConverged(false, "   "));
        Check("收敛：无工具 + null → false", !Agent.Infrastructure.AgentLoop.IsReadQueryConverged(false, null));
        Check("收敛：自定义阈值 100 生效", !Agent.Infrastructure.AgentLoop.IsReadQueryConverged(false, "这是一个较短的总结文本长度不足一百字符。", 100) && Agent.Infrastructure.AgentLoop.IsReadQueryConverged(false, new string('好', 101), 100));
    }

    // ── 区段 3645：10-10 新问题1+2：熔断语义失败判定 / run_command 目录基准 ──
    public static void Run3645()
    {
        Console.WriteLine("== 10-10 新问题 熔断语义失败 + run_command 目录基准 ==");
        Check("熔断失败：错误：文件过大 → true", Agent.Infrastructure.AgentLoop.IsToolSemanticFailure("错误：文件过大（230146 bytes）。请先调用 find_symbol。"));
        Check("熔断失败：错误：路径越界 → true", Agent.Infrastructure.AgentLoop.IsToolSemanticFailure("错误：路径越界。"));
        Check("熔断失败：正常输出 → false", !Agent.Infrastructure.AgentLoop.IsToolSemanticFailure("已写入文件，共 100 字符。"));
        Check("熔断失败：空串 → false", !Agent.Infrastructure.AgentLoop.IsToolSemanticFailure(""));
        Check("cwd：沙箱(无工程) → workspaceRoot", Agent.Infrastructure.Tools.RunCommandTool.ResolveWorkingDirectory(@"X:\run", null, false) == Path.GetFullPath(@"X:\run"));
        Check("cwd：只读工程模式 → workspaceRoot（写基准一致）", Agent.Infrastructure.Tools.RunCommandTool.ResolveWorkingDirectory(@"X:\run", @"X:\proj", false) == Path.GetFullPath(@"X:\run"));
        Check("cwd：可写工程模式 → projectRoot", Agent.Infrastructure.Tools.RunCommandTool.ResolveWorkingDirectory(@"X:\run", @"X:\proj", true) == Path.GetFullPath(@"X:\proj"));
        Check("cwd：可写工程但无工程根 → workspaceRoot", Agent.Infrastructure.Tools.RunCommandTool.ResolveWorkingDirectory(@"X:\run", null, true) == Path.GetFullPath(@"X:\run"));
    }
}
