// ============================================================
// Suites_Memory.cs
// 批次5：tests 单文件治理拆分产物。
// 区段来源（原 Program.cs 行号）：430（工程感知 P2 跨任务记忆）/463（P3 模型级记忆）/
//   522（任务归档记忆）/570（记忆治理收尾）/607（失败教训注入）/1732（记忆去重/过期）/
//   3728（记忆索引 LIKE 下推）/3762（文件状态记忆，含真 await）。
// 块体原样搬移，只改顶层全局引用（TestTmp/Check → using static TestHarness）。
// ============================================================
using static AgentTests.TestHarness;

namespace AgentTests;

public static class Suites_Memory
{
    // ── 区段 430：工程感知 P2：跨任务记忆（ProjectContextStore 签名缓存） ──
    public static void Run430()
    {
        Console.WriteLine("== 工程感知 P2：跨任务记忆（§14-3w P2） ==");
        {
            var ctxDb = Path.Combine(TestTmp, "project_context_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6] + ".db");
            var store = new Agent.Infrastructure.Project.ProjectContextStore(ctxDb);
            var projP2 = Path.Combine(TestTmp, "p2proj_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(Path.Combine(projP2, "src"));
            File.WriteAllText(Path.Combine(projP2, "Program.cs"), "// v1");
            File.WriteAllText(Path.Combine(projP2, "src", "A.cs"), "// a1");
            var sig = Agent.Infrastructure.Project.ProjectScanner.ComputeSignature(projP2);
            Check("签名含文件数 2", sig.FileCount == 2);
            Check("签名含目录数 1", sig.DirCount == 1);
            Check("首次查询 → 未命中",
                store.TryGet(projP2, sig.FileCount, sig.DirCount, sig.TotalBytes, sig.MaxMtimeTicks) is null);
            var scanP2 = Agent.Infrastructure.Project.ProjectScanner.Scan(projP2);
            Check("Scan 产出 CompactBlock（目录聚合）", scanP2.CompactBlock.Contains("目录文件数"));
            Check("CompactBlock 瘦身（不含具体文件名）", !scanP2.CompactBlock.Contains("A.cs"));
            Check("全量 ContextBlock 含具体文件名", scanP2.ContextBlock.Contains("A.cs"));
            store.Put(projP2, scanP2);
            var hitP2 = store.TryGet(projP2, sig.FileCount, sig.DirCount, sig.TotalBytes, sig.MaxMtimeTicks);
            Check("签名一致 → 缓存命中", hitP2 is not null && hitP2.CompactBlock.Contains("目录文件数"));
            Check("缓存命中返回 Overview", hitP2 is not null && hitP2.Overview.Contains("文件"));
            File.WriteAllText(Path.Combine(projP2, "src", "A.cs"), "// a2 changed");
            var sig2 = Agent.Infrastructure.Project.ProjectScanner.ComputeSignature(projP2);
            Check("文件修改 → 签名变化", sig2 != sig);
            Check("新签名 → 缓存未命中（自动重扫）",
                store.TryGet(projP2, sig2.FileCount, sig2.DirCount, sig2.TotalBytes, sig2.MaxMtimeTicks) is null);
            // 新签名下 Scan+Put → 再命中
            var scanP2b = Agent.Infrastructure.Project.ProjectScanner.Scan(projP2);
            store.Put(projP2, scanP2b);
            Check("重扫后 → 新签名命中",
                store.TryGet(projP2, sig2.FileCount, sig2.DirCount, sig2.TotalBytes, sig2.MaxMtimeTicks) is not null);
        }
    }

    // ── 区段 463：工程感知 P3：模型级记忆（AgentMemoryStore + RememberTool） ──
    public static void Run463()
    {
        Console.WriteLine("== 工程感知 P3：模型级记忆（§14-3w P3） ==");
        {
            var memDb = Path.Combine(TestTmp, "agent_memory_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6] + ".db");
            var mem = new Agent.Infrastructure.Memory.AgentMemoryStore(memDb);

            // Tokenize
            var toks = Agent.Infrastructure.Memory.AgentMemoryStore.Tokenize("读取 src/Agent.Core/Models/AgentOptions.cs 中 ProjectRoot 属性附近的内容");
            Check("Tokenize 含中文串", toks.Any(x => x.Contains("读取")) || toks.Any(x => x.Contains("属性附近")) || toks.Any(x => x.Contains("内容")));
            Check("Tokenize 含文件 token", toks.Any(x => x.Contains("AgentOptions")));
            Check("Tokenize 含路径 token", toks.Any(x => x.Contains("Agent.Core")));

            // Add + Search 命中
            mem.Add("model", "ProjectRoot 属性含义", "ProjectRoot 为 null 时沙箱模式，非 null 时工程模式。", "task_demo");
            mem.Add("model", "构建命令", "构建用 dotnet build LocalCodeForge.slnx，入口 src/Agent.Cli。");
            mem.Add("model", "天气 API", "wttr.in 无需 API Key，format=j1 返回 JSON。");
            var hits = mem.Search("读取 ProjectRoot 属性的代码", limit: 2);
            Check("Search 命中 ProjectRoot 记忆", hits.Any(h => h.Topic.Contains("ProjectRoot")));
            Check("Search 不命中无关记忆（limit 截断）", hits.All(h => !h.Topic.Contains("天气")));

            // 命中计数
            var h1 = hits.FirstOrDefault(h => h.Topic.Contains("ProjectRoot"));
            Check("命中计数 > 0", h1 is not null && h1.HitCount >= 1);

            // 【任务5】记忆注入特征过滤：kind=task 跨特征类剔除；kind=model 跨类降权保留
            var memF = Path.Combine(TestTmp, "agent_memory_f_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6] + ".db");
            var mf = new Agent.Infrastructure.Memory.AgentMemoryStore(memF);
            mf.Add("task", "天气任务归档", "wttr.in 天气获取成功。", "task_1", "ExternalCredential");
            mf.Add("model", "天气 API 决策", "wttr.in 无需 Key 可用。", null, "ExternalCredential");
            mf.Add("task", "学生系统任务归档", "C# 学生系统 4 文件构建通过。", "task_2", "MultiFile");
            // 特征类=MultiFile 检索"学生系统"：同类的 task 归档命中；跨类的 task 归档剔除（model 决策关键词不命中本就不返回）
            var fh = mf.Search("学生系统", limit: 10, feature: "MultiFile");
            Check("同特征类 task 记忆命中", fh.Any(h => h.Topic.Contains("学生系统")));
            Check("跨特征类 task 记忆剔除", fh.All(h => !h.Topic.Contains("天气任务归档")));
            Check("关键词不命中则 model 决策不注入", fh.All(h => !h.Topic.Contains("天气 API 决策")));
            // 降权验证：检索"天气" + feature=MultiFile——跨类 task 剔除；跨类 model 决策 topic 命中（score2→1）仍保留
            var fh2 = mf.Search("天气", limit: 10, feature: "MultiFile");
            Check("跨类检索：model 决策降权保留 + task 归档剔除",
                fh2.Any(h => h.Topic.Contains("天气 API 决策")) && !fh2.Any(h => h.Topic.Contains("天气任务归档")));

            // 二次检索命中数递增
            mem.Search("ProjectRoot 属性的含义", limit: 2);
            var hits2 = mem.Search("ProjectRoot 属性的含义", limit: 2);
            var h2 = hits2.FirstOrDefault(h => h.Topic.Contains("ProjectRoot"));
            Check("命中计数递增", h2 is not null && h2.HitCount >= 2);

            // 空/无效写入被忽略
            mem.Add("model", "   ", "内容");
            mem.Add("model", "主题", "  ");
            Check("空 topic/content 不写入", mem.Search("主题").Count == 0);

            // RememberTool 校验
            var tool = new Agent.Infrastructure.Tools.RememberTool(mem);
            var rEmpty = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse("{\"topic\":\"x\",\"content\":\" \"}").RootElement, CancellationToken.None).Result;
            Check("RememberTool 空 content 拒绝", rEmpty.Contains("不能为空"));
            var rOk = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse("{\"topic\":\"记忆工具验证\",\"content\":\"remember 工具写入正常\"}").RootElement, CancellationToken.None).Result;
            Check("RememberTool 写入成功", rOk.Contains("已记住"));
            Check("RememberTool 写入可检索", mem.Search("记忆工具验证").Any(h => h.Topic.Contains("记忆工具验证")));
        }
    }

    // ── 区段 522：工程感知 P3 补足：任务归档记忆（TaskMemoryExtractor） ──
    public static void Run522()
    {
        Console.WriteLine("== 工程感知 P3 补足：任务归档记忆（§14-3w P3） ==");
        {
            // 成功归档
            var (t1, c1) = Agent.Infrastructure.Memory.TaskMemoryExtractor.Extract(
                "帮我做一个 C# 订单系统项目，多文件组织\n第二行", true, new List<string>(), "orchestrator",
                "已创建 4 个文件，验证通过。");
            Check("成功 topic 去换行截断", t1.Contains("帮我做一个") && !t1.Contains("\n"));
            Check("成功 content 含完成标记", c1.Contains("任务完成（orchestrator）") && c1.Contains("已创建 4 个文件"));
            Check("成功 content 不含失败标记", !c1.Contains("任务失败"));

            // 失败归档：错误提炼
            var (t2, c2) = Agent.Infrastructure.Memory.TaskMemoryExtractor.Extract(
                "用 C# 写一个天气 API 调用", false, new List<string> { "模型连续返回空答案，任务终止。", "升级失败" }, "agent");
            Check("失败 topic 含输入", t2.Contains("天气 API"));
            Check("失败 content 含失败标记", c2.Contains("任务失败（agent）"));
            Check("失败 content 提炼错误教训", c2.Contains("模型连续返回空答案"));

            // 无错误信息的失败归档
            var (t3, c3) = Agent.Infrastructure.Memory.TaskMemoryExtractor.Extract(
                "读取某文件", false, new List<string>(), "agent");
            Check("无错误 → 未知错误", c3.Contains("未知错误"));

            // 长输入 topic 截断 + content 截断
            var longInput = new string('长', 100);
            var (t4, c4) = Agent.Infrastructure.Memory.TaskMemoryExtractor.Extract(
                longInput, false, new List<string> { new string('错', 500) }, "agent");
            Check("topic 截断到 40", t4.Length == 40);
            Check("content 截断到 200", c4.Length == 200);

            // 【10-13 阶段2】任务事实：写文件数入 content
            var (t5, c5) = Agent.Infrastructure.Memory.TaskMemoryExtractor.Extract(
                "写个倒计时程序", true, new List<string>(), "agent", "已完成。", 3);
            Check("成功写文件数入 content", c5.Contains("｜写3文件"));
            var (t6, c6) = Agent.Infrastructure.Memory.TaskMemoryExtractor.Extract(
                "写个倒计时程序", false, new List<string> { "编译错误" }, "agent", null, 2);
            Check("失败写文件数入 content", c6.Contains("｜写2文件"));
            var (t7, c7) = Agent.Infrastructure.Memory.TaskMemoryExtractor.Extract(
                "读个文件", true, new List<string>(), "agent", "读完了。", 0);
            Check("零写文件不拼接", !c7.Contains("写0文件") && !c7.Contains("｜写"));

            // 归档写入 store（kind=task）可检索
            var memDb2 = Path.Combine(TestTmp, "agent_memory2_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6] + ".db");
            var mem2 = new Agent.Infrastructure.Memory.AgentMemoryStore(memDb2);
            mem2.Add("task", "读取 AgentOptions 配置", "任务完成（agent）：已读取并总结。", "task_abc");
            var taskHits = mem2.Search("读取 AgentOptions 配置", limit: 3);
            Check("task 记忆可检索", taskHits.Any(h => h.Kind == "task" && h.Topic.Contains("AgentOptions")));
        }
    }

    // ── 区段 570：记忆治理收尾：自动过期清理 + 默认阈值 + 调权默认值 ──
    public static void Run570()
    {
        Console.WriteLine("== 记忆治理收尾：自动过期清理 + 默认阈值 ==");
        {
            // AgentOptions 默认治理阈值（任务启动时自动执行）
            var opt = new Agent.Core.Models.AgentOptions();
            Check("治理默认 30 天", opt.MemoryPruneDays == 30);
            Check("治理默认 1 命中（仅 0 命中可清）", opt.MemoryPruneMinHits == 1);

            // 【路由调权收尾】调权参数默认值（与 ModelRouter 原硬编码一致，行为零变化）
            Check("调权默认样本 3", opt.RoutingTuningMinSamples == 3);
            Check("写降级默认阈值 0.6", opt.RoutingDowngradeMinSuccessRate == 0.6);
            Check("读升级默认坏率 0.6", opt.RoutingReadUpgradeBadRate == 0.6);
            Check("读升级默认成功率 0.5", opt.RoutingReadUpgradeMinSuccessRate == 0.5);
            Check("Complex 降级默认样本 5", opt.RoutingDropAgentProMinSamples == 5);
            Check("Complex 降级默认阈值 0.5", opt.RoutingDropAgentProMinSuccessRate == 0.5);

            // PruneExpired 确定性验证：久未使用且 0 命中 → 删除；有命中 → 保留
            var memPPath = Path.Combine(TestTmp, "agent_memory_p_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6] + ".db");
            var memP = new Agent.Infrastructure.Memory.AgentMemoryStore(memPPath);
            memP.Add("task", "旧任务无命中", "很久以前的失败归档。", "old_task");
            memP.Add("model", "常用知识", "常用命中知识。", "old_model");
            memP.Search("常用知识", limit: 1);   // 命中一次 → hit_count=1

            // 模拟久未使用：把两条的 last_hit_at 都改为 35 天前（30 天阈值外）
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + memPPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE agent_memory SET created_at='2026-08-20 00:00:00', last_hit_at='2026-08-20 00:00:00';";
                cmd.ExecuteNonQuery();
            }

            var pruned = memP.PruneExpired(30, 1);   // 35 天前最后使用 且 hit_count<1 → 仅删"旧任务无命中"
            Check("过期 0 命中清理 1 条", pruned == 1);
            Check("常用知识（有命中）保留", memP.Search("常用知识", limit: 1).Count == 1);
            Check("旧任务 0 命中已清", memP.Search("旧任务无命中", limit: 1).Count == 0);
        }
    }

    // ── 区段 607：工程感知 P3 补足：失败教训注入（少走弯路） ──
    public static void Run607()
    {
        Console.WriteLine("== 工程感知 P3 补足：失败教训注入（少走弯路） ==");
        {
            var mem3 = new Agent.Infrastructure.Memory.AgentMemoryStore(
                Path.Combine(TestTmp, "agent_memory3_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6] + ".db"));

            // 空 → null
            Check("无命中 → null", Agent.Infrastructure.Memory.MemoryPromptBuilder.Build(new List<Agent.Infrastructure.Memory.AgentMemoryStore.MemoryHit>()) is null);

            // 失败记忆 → 警示组单列
            var failHit = new Agent.Infrastructure.Memory.AgentMemoryStore.MemoryHit(1, "task", "创建 hello.txt", "任务失败（agent）：达到最大步数 20，任务终止。", 2);
            var okHit = new Agent.Infrastructure.Memory.AgentMemoryStore.MemoryHit(2, "model", "ProjectRoot 属性", "ProjectRoot 为 null 时沙箱模式。", 1);
            var built = Agent.Infrastructure.Memory.MemoryPromptBuilder.Build(new List<Agent.Infrastructure.Memory.AgentMemoryStore.MemoryHit> { failHit, okHit });
            Check("失败教训独立警示组", built is not null && built.Contains("历史失败教训") && built.Contains("避免重蹈覆辙"));
            Check("失败内容含教训原文", built is not null && built.Contains("达到最大步数 20"));
            Check("参考组含 model 记忆", built is not null && built.Contains("历史参考") && built.Contains("ProjectRoot 为 null 时沙箱模式"));
            Check("失败与参考分节", built is not null && built.IndexOf("历史失败教训") < built.IndexOf("历史参考"));

            // 两条失败 → 失败计数强化
            var failHit2 = new Agent.Infrastructure.Memory.AgentMemoryStore.MemoryHit(3, "task", "创建 hello2.txt", "任务失败（agent）：模型连续返回空答案，任务终止。", 1);
            var built2 = Agent.Infrastructure.Memory.MemoryPromptBuilder.Build(new List<Agent.Infrastructure.Memory.AgentMemoryStore.MemoryHit> { failHit, failHit2 });
            Check("多条失败 → 计数强化", built2 is not null && built2.Contains("同类失败累计已记录 2 次"));

            // 只有参考 → 无警示组
            var built3 = Agent.Infrastructure.Memory.MemoryPromptBuilder.Build(new List<Agent.Infrastructure.Memory.AgentMemoryStore.MemoryHit> { okHit });
            Check("仅参考无警示组", built3 is not null && !built3.Contains("历史失败教训") && built3.Contains("历史参考"));
        }
    }

    // ── 区段 1732：记忆治理：去重合并 / forget / prune / stats ──
    public static void Run1732()
    {
        Console.WriteLine("== 记忆治理（去重/过期/forget） ==");
        {
            var govDbPath = Path.Combine(TestTmp, "mem_gov_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6] + ".db");
            var gov = new Agent.Infrastructure.Memory.AgentMemoryStore(govDbPath);

            // 去重合并：同 kind+topic → 一条，count 累加
            gov.Add("task", "创建一个 hello.txt 文件", "任务失败（agent）：第一次失败", "task_1");
            gov.Add("task", "创建一个 hello.txt 文件", "任务失败（agent）：第二次失败", "task_2");
            var afterMerge = gov.GetStats();
            Check("同 topic 合并为一条", afterMerge.Total == 1);
            var mergedHit = gov.List(10).FirstOrDefault();
            Check("合并后 count=2", mergedHit is not null && mergedHit.Count == 2);
            Check("合并后 content 为最新", mergedHit is not null && mergedHit.Content.Contains("第二次"));

            // 不同 topic → 独立
            gov.Add("model", "ProjectRoot 属性", "ProjectRoot 为 null 时沙箱模式。", null);
            Check("不同 topic 独立成条", gov.GetStats().Total == 2);

            // Search 命中带 count
            var s1 = gov.Search("创建一个 hello.txt 文件");
            Check("检索命中返回 count", s1.Count == 1 && s1[0].Count == 2);

            // BumpHit 刷新 last_hit_at（经 List 可见）
            gov.Add("model", "AgentLoop 升级链", "small 空响应直达 large。", null);
            var hitsBefore = gov.Search("AgentLoop 升级链");
            Check("BumpHit 后 last_hit_at 非空", hitsBefore.Count == 1 && hitsBefore[0].LastHitAt.Length > 0);

            // Forget
            var target = gov.List(10).First(m => m.Topic.Contains("ProjectRoot"));
            Check("forget 删除单条", gov.Forget(target.Id) && gov.GetStats().Total == 2);

            // PruneExpired：造一条旧数据（直接 SQL 更新 created_at 为 100 天前）
            gov.Add("task", "旧任务归档", "任务失败（agent）：很旧", "task_old");
            {
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                    "Data Source=" + govDbPath);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE agent_memory SET created_at = @old, last_hit_at = @old WHERE topic = @tp;
                """;
                cmd.Parameters.AddWithValue("@old", DateTime.Now.AddDays(-100).ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("@tp", "旧任务归档");
                cmd.ExecuteNonQuery();
            }
            var beforePrune = gov.GetStats().Total;
            var removedPrune = gov.PruneExpired(90, 2);
            Check("过期清理删除旧记录", removedPrune >= 1 && gov.GetStats().Total == beforePrune - 1);

            // Deduplicate：SQL 直插两条同 topic（绕过 Add 合并）造存量重复
            {
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                    "Data Source=" + govDbPath);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO agent_memory (kind, topic, content, created_at, count) VALUES ('task', '存量重复', '旧内容A', @at, 1);
                    INSERT INTO agent_memory (kind, topic, content, created_at, count) VALUES ('task', '存量重复', '旧内容B', @at, 2);
                """;
                cmd.Parameters.AddWithValue("@at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.ExecuteNonQuery();
            }
            var dedupRemoved = gov.Deduplicate();
            Check("dedup 合并存量重复", dedupRemoved == 1);
            var dedupHit = gov.List(50).FirstOrDefault(m => m.Topic == "存量重复");
            Check("dedup 后 count=3（1+2）", dedupHit is not null && dedupHit.Count == 3);
        }
    }

    // ── 区段 3728：10-13 批次3：记忆搜索 SQL LIKE 下推 + 通配符转义 ──
    public static void Run3728()
    {
        Console.WriteLine("== 10-13 记忆索引 ==");
        {
            var wsM = Path.Combine(Path.GetTempPath(), "law_mem_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(wsM);
            var storeM = new Agent.Infrastructure.Memory.AgentMemoryStore(Path.Combine(wsM, "m.db"));
            storeM.Add("task", "BigService 重构", "给 BigService.cs 的 Method1500 加了注释", "t1");
            storeM.Add("task", "PDF_RAG 分析", "项目结构分析完成", "t2");
            // 转义对照：无转义时 %Method1_500%（_ 通配单字符）会误命中 Method1x500；转义后 %Method1500% 精确匹配
            storeM.Add("task", "Method1x500 演练", "无关内容", "t3");
            var hit1 = storeM.Search("Method1500", 5);
            Check("记忆：下划线 token 精确命中（Method1500 不被 _ 通配误命中 Method1x500）", hit1.Any(h => h.Topic.Contains("BigService")) && !hit1.Any(h => h.Topic.Contains("Method1x500")));
            var hit2 = storeM.Search("PDF_RAG", 5);
            Check("记忆：topic 命中计分", hit2.Any(h => h.Topic.Contains("PDF_RAG")));
            var hit3 = storeM.Search("不存在的关键词xyz不存在", 5);
            Check("记忆：无命中返回空（LIKE 下推不误拉全表）", hit3.Count == 0);
            var hit4 = storeM.Search("加了注释", 5);
            Check("记忆：content 命中（倒排 content term 权重 1——'加了注释' token 精确召回）", hit4.Any(h => h.Content.Contains("Method1500")));
            var hit4b = storeM.Search("注释", 5);
            Check("记忆：倒排精确语义（10-17 ② 取代 LIKE 子串——'注释' 不再以子串模糊召回仅含'加了注释'的记忆）", hit4b.Count == 0);
            try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
            try { Directory.Delete(wsM, true); } catch { }
        }
    }

    // ── 区段 3762：10-13 阶段3：文件状态记忆（WriteFileTool 带 memoryStore，含真 await） ──
    public static async System.Threading.Tasks.Task Run3762()
    {
        Console.WriteLine("== 10-13 文件状态记忆 ==");
        {
            var ws3 = Path.Combine(Path.GetTempPath(), "law_mem_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(ws3, "proj"));
            var memDb = Path.Combine(ws3, "mem.db");
            var memStore = new Agent.Infrastructure.Memory.AgentMemoryStore(memDb);
            File.WriteAllText(Path.Combine(ws3, "proj", "a.txt"), "hello");
            var wf3 = new Agent.Infrastructure.Tools.WriteFileTool(
                ws3, Path.Combine(ws3, "proj"), true, "t1", null, null, memStore);
            var out3 = await wf3.InvokeAsync(System.Text.Json.JsonSerializer.SerializeToElement(
                new Dictionary<string, object?> { ["path"] = "a.txt", ["content"] = "hello world" }), default);
            Check("文件记忆：覆盖已存在文件成功", out3.StartsWith("已写入"));
            var hits3 = memStore.Search("a.txt", limit: 3);
            Check("文件记忆：topic=文件名命中", hits3.Count > 0 && hits3.Any(h => h.Kind == "file" && h.Topic == "a.txt"));
            Check("文件记忆：content 含改动信息", hits3.Any(h => h.Kind == "file" && h.Content.Contains("修改")));
            try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
            try { Directory.Delete(ws3, true); } catch { /* SQLite 连接池可能短暂占用，临时目录容错不删 */ }
        }
    }

    // ── 10-17 ② 记忆 token 化倒排断言（无标题，追加于 91 区段之后）──
    public static void RunMemoryTermsIndex()
    {
        Console.WriteLine("== 10-17 ② 记忆 token 化倒排索引 ==");
        var wsT = Path.Combine(Path.GetTempPath(), "law_terms_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(wsT);
        var db = Path.Combine(wsT, "m.db");
        var st = new Agent.Infrastructure.Memory.AgentMemoryStore(db);

        // ① Add 同步写倒排项
        st.Add("model", "AlphaService 配置", "AlphaService 初始化参数。", "t1");
        st.Add("model", "BetaService 调优", "BetaService 的 Method900 注释。", "t2");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + db))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM memory_terms;";
            Check("倒排表随 Add 落库（term 行 > 0）", Convert.ToInt64(cmd.ExecuteScalar()) > 0);
        }

        // ② token 精确匹配（倒排无 LIKE 子串歧义）
        var hitA = st.Search("AlphaService");
        Check("倒排命中 topic token", hitA.Any(h => h.Topic.Contains("AlphaService")));
        Check("倒排不误命中无关记忆", hitA.All(h => !h.Topic.Contains("BetaService")));

        // ③ topic 权重优先：同 term 双命中时 topic 行排在 content 行前（w=2 > w=1）
        st.Add("model", "共享术语 话题", "shared_term_xyz 在 topic 中。", "t3");
        st.Add("model", "其他话题", "shared_term_xyz 在 content 中。", "t4");
        var hitsW = st.Search("shared_term_xyz", limit: 10);
        var topicIdx = hitsW.FindIndex(h => h.Topic.Contains("共享术语"));
        var contentIdx = hitsW.FindIndex(h => h.Topic.Contains("其他话题"));
        Check("topic 权重优先于 content（同 term 双命中 topic 在前）",
            topicIdx >= 0 && contentIdx >= 0 && topicIdx < contentIdx);

        // ④ 合并更新重建索引：同 kind+topic 更新 content → 新 content 的 term 可检索
        st.Add("model", "AlphaService 配置", "AlphaService 初始化参数已改为 version2 配置项。", "t5");
        var hitV2 = st.Search("version2");
        Check("合并更新后新 content term 可检索", hitV2.Any(h => h.Topic.Contains("AlphaService")));

        // ⑤ 存量回填：模拟旧库（terms 清空 + 存量行）→ 新实例初始化触发全量回填 → 可检索
        using (var conn2 = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + db))
        {
            conn2.Open();
            using var ins = conn2.CreateCommand();
            ins.CommandText = """
                INSERT INTO agent_memory (kind, topic, content, created_at, count)
                VALUES ('model', 'LegacyRow 话题', 'legacy 存量行。', @at, 1);
            """;
            ins.Parameters.AddWithValue("@at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            ins.ExecuteNonQuery();
            using var del = conn2.CreateCommand();
            del.CommandText = "DELETE FROM memory_terms;";
            del.ExecuteNonQuery();
        }
        var st2 = new Agent.Infrastructure.Memory.AgentMemoryStore(db);   // terms 空 + 存量行 → 全量回填
        var hitLegacy = st2.Search("LegacyRow");
        Check("存量回填：无索引旧库初始化后倒排可检索", hitLegacy.Any(h => h.Topic.Contains("LegacyRow")));
        var hitAlpha = st2.Search("version2");
        Check("回填不丢已有行（version2 仍可检索）", hitAlpha.Any(h => h.Topic.Contains("AlphaService")));

        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
        try { Directory.Delete(wsT, true); } catch { /* SQLite 连接池可能短暂占用，临时目录容错不删 */ }
    }

    // ── 10-18 P2-4 记忆向量（语义）检索断言 ──
    // 可控 FakeEmbedder：含"注释"→[1,0,0,0]、含"天气"→[0,1,0,0]、其他→[0,0,1,0]。
    // 语义场景："改注释说明" 与 "加了注释" 记忆同方向（高余弦），而倒排 token 精确不命中 →
    // 只能由向量通道召回，直接验证语义补足通道。
    sealed class FakeEmbedder : Agent.Infrastructure.Memory.IEmbedder
    {
        public string Model => "fake-embed";
        public int Dimensions => 4;
        public float[]? Embed(string text)
        {
            if (text.Contains("注释")) return new[] { 1f, 0f, 0f, 0f };
            if (text.Contains("天气")) return new[] { 0f, 1f, 0f, 0f };
            return new[] { 0f, 0f, 1f, 0f };
        }
    }

    /// <summary>维度不符的 embedder：模拟模型漂移/服务异常 → 应整链路降级（向量不落库、检索纯倒排）。</summary>
    sealed class BadDimEmbedder : Agent.Infrastructure.Memory.IEmbedder
    {
        public string Model => "fake-bad";
        public int Dimensions => 4;
        public float[]? Embed(string text) => new float[3];   // 3 ≠ 4
    }

    public static void RunMemoryVectorSemantic()
    {
        Console.WriteLine("== 10-18 P2-4 记忆向量（语义）检索 ==");
        var wsV = Path.Combine(Path.GetTempPath(), "law_vec_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(wsV);
        var db = Path.Combine(wsV, "m.db");
        var fake = new FakeEmbedder();
        var mem = new Agent.Infrastructure.Memory.AgentMemoryStore(db, fake);

        // ① Add 同步落向量（dim/model/blob 长度）
        mem.Add("model", "BetaService 调优", "BetaService 的方法加了注释说明。", "t1");
        mem.Add("model", "天气接口", "天气查询用 wttr.in。", "t2");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + db))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*), SUM(dim), COUNT(DISTINCT model) FROM memory_vectors;";
            using var r = cmd.ExecuteReader();
            r.Read();
            Check("向量表随 Add 落库（2 行）", r.GetInt64(0) == 2);
            Check("向量维度 4", r.GetInt64(1) == 8);
            Check("向量模型记录", r.GetInt64(2) == 1);
            using var blobCmd = conn.CreateCommand();
            blobCmd.CommandText = "SELECT length(vector), dim, model FROM memory_vectors LIMIT 1;";
            using var rb = blobCmd.ExecuteReader();
            rb.Read();
            Check("向量 blob 长度 = dim×4（float32）", rb.GetInt64(0) == rb.GetInt64(1) * 4);
            Check("向量 model 名正确", rb.GetString(2) == "fake-embed");
        }

        // ② 倒排优先不抢占：精确命中时 top 第一仍是倒排精确条（向量不改变既有语义）
        var exactFirst = mem.Search("BetaService", limit: 2);
        Check("倒排精确命中优先（BetaService 在首）", exactFirst.Count > 0 && exactFirst[0].Topic.Contains("BetaService"));

        // ③ 语义兜底：倒排零命中 + 向量相近 → 召回（"改注释说明" 与 注释记忆同方向）
        var sem = mem.Search("改注释说明", limit: 1);
        Check("向量语义兜底：倒排无命中时召回注释记忆", sem.Count == 1 && sem[0].Topic.Contains("BetaService"));
        var sem2 = mem.Search("天气怎么查", limit: 1);
        Check("向量语义兜底：召回天气记忆", sem2.Count == 1 && sem2[0].Topic.Contains("天气"));
        var none = mem.Search("完全无关的查询词甲乙丙", limit: 1);
        Check("向量不强行召回（无方向匹配 → 空）", none.Count == 0);

        // ④ 合并更新重建向量：同 kind+topic 更新 content → 向量随 content 变化（新方向可语义召回）
        mem.Add("model", "BetaService 调优", "BetaService 的 Method900 加了注释说明。", "t3");
        var afterUpd = mem.Search("改注释说明", limit: 1);
        Check("合并更新后向量仍语义可召回", afterUpd.Count == 1 && afterUpd[0].Topic.Contains("BetaService"));

        // ⑤ 维度不符降级：BadDimEmbedder → 向量不落库、Search 纯倒排
        var dbBad = Path.Combine(wsV, "bad.db");
        var memBad = new Agent.Infrastructure.Memory.AgentMemoryStore(dbBad, new BadDimEmbedder());
        memBad.Add("model", "坏维度话题", "坏维度内容加了注释。", "t4");
        using (var connBad = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + dbBad))
        {
            connBad.Open();
            using var cnt = connBad.CreateCommand();
            cnt.CommandText = "SELECT COUNT(*) FROM memory_vectors;";
            Check("维度不符 → 向量不落库（降级）", Convert.ToInt64(cnt.ExecuteScalar()) == 0);
        }
        var badHit = memBad.Search("坏维度话题");
        Check("维度不符 → 倒排检索正常（fail-open）", badHit.Any(h => h.Topic.Contains("坏维度话题")));

        // ⑥ 存量懒回填幂等：无 embedder 写入存量 → 换带 embedder 实例首次 Search 触发回填 → 落库且不重复
        var dbBf = Path.Combine(wsV, "bf.db");
        var plain = new Agent.Infrastructure.Memory.AgentMemoryStore(dbBf);   // embedder null
        plain.Add("model", "存量话题A", "存量内容 A 有注释。", "t5");
        plain.Add("model", "存量话题B", "存量内容 B 无注释。", "t6");
        var bfMem = new Agent.Infrastructure.Memory.AgentMemoryStore(dbBf, fake);   // 带 embedder，存量无向量
        var bfHit = bfMem.Search("改注释说明", limit: 1);   // 触发回填 + 语义召回
        using (var connBf = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + dbBf))
        {
            connBf.Open();
            using var c1 = connBf.CreateCommand();
            c1.CommandText = "SELECT COUNT(*) FROM memory_vectors;";
            var afterFirst = Convert.ToInt64(c1.ExecuteScalar());
            Check("存量懒回填：首查后向量落库（2 条）", afterFirst == 2);
            _ = bfMem.Search("改注释说明", limit: 1);   // 二次 Search 不应重复回填
            using var c2 = connBf.CreateCommand();
            c2.CommandText = "SELECT COUNT(*) FROM memory_vectors;";
            Check("回填幂等：二次 Search 不重复生成", Convert.ToInt64(c2.ExecuteScalar()) == afterFirst);
        }
        Check("回填后可语义召回存量记忆", bfHit.Count == 1 && bfHit[0].Topic.Contains("存量话题"));

        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
        try { Directory.Delete(wsV, true); } catch { /* SQLite 连接池可能短暂占用，临时目录容错不删 */ }
    }
}
