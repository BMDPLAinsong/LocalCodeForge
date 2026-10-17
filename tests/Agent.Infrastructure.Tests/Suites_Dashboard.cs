// ============================================================
// Suites_Dashboard.cs
// Web Dashboard（TaskEventHub / TaskEventStore / CommandHub / DashboardHost 冒烟 /
// 安全 token / 依赖图谱）+ 3b Web 端回滚闭环
// 区段：3150 / 3535（均为 async：区段原文含顶层 await）
// 依赖：using static AgentTests.TestHarness（Check）
// ============================================================
using System.Text.Json;
using static AgentTests.TestHarness;

namespace AgentTests;

public static class Suites_Dashboard
{
    // ── 区段 3150：Dashboard P0 TaskEventHub + TaskEventStore + DashboardHost ──
    public static async Task Run3150()
    {
        Console.WriteLine("== Dashboard P0 TaskEventHub + TaskEventStore + DashboardHost（Events/ + Agent.Dashboard） ==");
        {
            // ── 1. TaskEventHub：订阅 → 发布 → 读取；退订后不再收到 ──
            var hub = new Agent.Infrastructure.Events.TaskEventHub();
            var (reader, handle) = hub.Subscribe();
            hub.Publish(new Agent.Core.Models.AgentEvent.Thinking("t1"));
            hub.Publish(new Agent.Core.Models.AgentEvent.Routing("small", "L0", "test", null));
            var got = new List<Agent.Core.Models.AgentEvent>();
            while (reader.TryRead(out var e)) got.Add(e);
            Check("hub 发布 → 订阅者收到 2 事件", got.Count == 2 && got[0] is Agent.Core.Models.AgentEvent.Thinking && got[1] is Agent.Core.Models.AgentEvent.Routing);
            handle.Dispose();
            var (reader2, _) = hub.Subscribe();
            hub.Publish(new Agent.Core.Models.AgentEvent.Thinking("t2"));
            Agent.Core.Models.AgentEvent? first = null;
            reader2.TryRead(out first);
            Check("退订后不再接收（第二个订阅者不受影响）", first is Agent.Core.Models.AgentEvent.Thinking && (first as Agent.Core.Models.AgentEvent.Thinking)!.Text == "t2");

            // 【10-14 P3-5 哨兵】CommandHub 无任务哨兵：CurrentTask = (0, null)——前端"空闲"显示依赖此契约
            var chkHub = new Agent.Core.CommandHub();
            var (chkSeq, chkInput) = chkHub.CurrentTask;
            Check("哨兵：CommandHub 初始 CurrentTask seq=0 / input=null / 队列空",
                chkSeq == 0 && chkInput is null && chkHub.Count == 0);

            // ── 1b. 【10-14 P2-2】wire 短名随类型声明（[WireName]）+ SSE/回放同源序列化 ──
            // ① 锁 wire 名：前端按短名分派渲染，短名漂移 = 事件渲染不出来
            var wireNames = new (Type T, string Name)[]
            {
                (typeof(Agent.Core.Models.AgentEvent.Thinking), "thinking"),
                (typeof(Agent.Core.Models.AgentEvent.ToolCall), "toolCall"),
                (typeof(Agent.Core.Models.AgentEvent.ToolResultEvent), "toolResultEvent"),
                (typeof(Agent.Core.Models.AgentEvent.FinalAnswer), "finalAnswer"),
                (typeof(Agent.Core.Models.AgentEvent.Error), "error"),
                (typeof(Agent.Core.Models.AgentEvent.Routing), "routing"),
                (typeof(Agent.Core.Models.AgentEvent.PlanGenerated), "planGenerated"),
                (typeof(Agent.Core.Models.AgentEvent.FileWritten), "fileWritten"),
            };
            Check("P2-2 wire 短名：8 类事件 [WireName] 标注齐全且与前端契约一致",
                wireNames.All(x => x.T.GetCustomAttributes(typeof(Agent.Core.Models.WireNameAttribute), false)
                    .OfType<Agent.Core.Models.WireNameAttribute>().FirstOrDefault()?.Name == x.Name));
            // ①b 验证宿主映射真的生效（反射构建的 WireTypeMap）：8 类逐一解析 + 未知类名兜底不吞
            Check("P2-2 宿主 wire 映射生效：8 类解析为契约短名",
                wireNames.All(x => Agent.Dashboard.DashboardHost.WireType(x.T.Name) == x.Name));
            Check("P2-2 宿主 wire 映射兜底：未知类名原样返回（不吞事件）",
                Agent.Dashboard.DashboardHost.WireType("SomeFutureEvent") == "SomeFutureEvent");
            // ② 锁同源序列化：SSE 现走"Serialize(evt, GetType(), TaskEventStore.JsonOpts) + 宿主补 type"
            //    （原为 ToWire 手工匿名对象 34 行——record 加字段时回放可见、SSE 不可见的分叉温床）
            var sseNode = JsonSerializer.SerializeToNode(
                new Agent.Core.Models.AgentEvent.ToolResultEvent("write_file", "ok", true),
                typeof(Agent.Core.Models.AgentEvent.ToolResultEvent),
                Agent.Infrastructure.Events.TaskEventStore.JsonOpts) as System.Text.Json.Nodes.JsonObject;
            Check("P2-2 SSE 同源序列化：ToolResultEvent → {name,output,success} 且 type 由宿主补",
                sseNode is not null
                && (string?)sseNode["name"] == "write_file"
                && (string?)sseNode["output"] == "ok"
                && (bool?)sseNode["success"] == true
                && !sseNode.ContainsKey("type"));

            // ── 2. TaskEventStore：记录 → 读取（seq 升序）→ Prune 清理 → JsonOpts 单一来源 ──
            var ws2 = Path.Combine(Path.GetTempPath(), "store_smoke_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws2);
            try
            {
                var store = new Agent.Infrastructure.Events.TaskEventStore(Path.Combine(ws2, "metrics.db"));
                var ev1 = new Agent.Core.Models.AgentEvent.ToolCall("write_file", System.Text.Json.JsonDocument.Parse("{\"path\":\"a.cs\"}").RootElement);
                var ev2 = new Agent.Core.Models.AgentEvent.FinalAnswer("done");
                store.Record("taskX", 0, ev1);
                store.Record("taskX", 1, ev2);
                store.Record("other", 0, ev1);
                var all = store.GetEvents("taskX");
                Check("store 记录 + 读取（seq 升序、跨任务隔离）",
                    all.Count == 2 && all[0].Seq == 0 && all[1].Seq == 1
                    && all[0].Type == nameof(Agent.Core.Models.AgentEvent.ToolCall)
                    && all[1].Type == nameof(Agent.Core.Models.AgentEvent.FinalAnswer)
                    && all[0].Payload.Contains("write_file"));
                var parsed = System.Text.Json.JsonSerializer.Deserialize<Agent.Core.Models.AgentEvent.ToolCall>(all[0].Payload, Agent.Infrastructure.Events.TaskEventStore.JsonOpts);
                Check("store payload 可反序列化回原事件（JsonOpts 单一来源）",
                    parsed is not null && parsed.Name == "write_file");
                var pruned = store.PruneOlderThan(1);
                Check("PruneOlderThan(1) 清理 1 秒前事件（刚写入的保留）", pruned == 0);
                var eventsApi = store.GetEvents("nope");
                Check("store 未写入任务 → 空列表", eventsApi.Count == 0);
            }
            finally
            {
                try { Directory.Delete(ws2, true); } catch { }
            }

            // ── CommandHub 单元（10-07 webUI 交互：终端/web 统一命令通道；10-08 P0：seq/快照/取消）──
            {
                var cmdHub = new Agent.Core.CommandHub();
                var sa = cmdHub.TrySubmit("a");
                var sb = cmdHub.TrySubmit("b");
                Check("CommandHub seq 递增（web 提交归属标识）", sa == 1 && sb == 2);
                Check("CommandHub Source 默认 terminal / web 显式（10-08 回显来源区分：web 提交需终端补回显，terminal 已由 ReadLine 回显）",
                    cmdHub.TrySubmit("w1", "web") == 3);
                var a = await cmdHub.NextAsync(CancellationToken.None);
                var b = await cmdHub.NextAsync(CancellationToken.None);
                Check("CommandHub FIFO：先提交先取回（终端输入与 web 提交同一队列）", a!.Value.Input == "a" && b!.Value.Input == "b");
                Check("CommandHub 条目含提交时间（/api/queue SubmittedAt 数据源）",
                    a!.Value.SubmittedAt != default && b!.Value.SubmittedAt != default);
                var w1 = await cmdHub.NextAsync(CancellationToken.None);
                Check("CommandHub Source 透传 web（10-08：web 提交 → CLI 补回显的判定依据）",
                    w1!.Value.Input == "w1" && w1.Value.Source == "web");
                cmdHub.TrySubmit(null);
                Check("CommandHub EOF null 透传（无头 stdin 关闭 → 主循环退出）",
                    (await cmdHub.NextAsync(CancellationToken.None))!.Value.Input is null);

                var big = new Agent.Core.CommandHub();
                for (var i = 0; i < 105; i++) big.TrySubmit("c" + i);
                Check("CommandHub 有界 100 DropOldest：超限丢最旧（105 条写入后首条为 c5）",
                    (await big.NextAsync(CancellationToken.None))!.Value.Input == "c5");
                Check("CommandHub Snapshot：顺序与 EOF 过滤（/api/queue 数据源）",
                    big.Snapshot().Count == 99 && big.Snapshot()[0].Input == "c6" && big.Snapshot()[^1].Input == "c104");
                var cancelCts = new CancellationTokenSource();
                cancelCts.Cancel();
                Check("CommandHub 取消 → null（不抛）", await new Agent.Core.CommandHub().NextAsync(cancelCts.Token) is null);
                var hub2 = new Agent.Core.CommandHub();
                var taskCts = new CancellationTokenSource();
                hub2.AttachTaskCancellation(taskCts);
                Check("CommandHub 取消当前任务：Attach 后 CancelCurrentTask → 令牌已取消（web POST /api/cancel 触发同一机制）",
                    hub2.CancelCurrentTask() && taskCts.IsCancellationRequested);
                hub2.DetachTaskCancellation();
                Check("CommandHub 取消：Detach 后无任务可取消 → false", !hub2.CancelCurrentTask());
                // ── 任务归属（10-08 P1）──
                var hub3 = new Agent.Core.CommandHub();
                hub3.AttachTaskInfo(7, "用 C# 写一个待办事项项目");
                var cur = hub3.CurrentTask;
                Check("CommandHub 当前任务归属：Attach(seq,input) 后 CurrentTask 返回（/api/current-task 数据源）",
                    cur.Seq == 7 && cur.Input == "用 C# 写一个待办事项项目");
                hub3.DetachTaskInfo();
                var cur2 = hub3.CurrentTask;
                Check("CommandHub 当前任务归属：Detach 后 Seq=0/Input=null（任务结束清除）",
                    cur2.Seq == 0 && cur2.Input is null);
                Check("AgentOptions.TaskTimeoutMinutes 默认 30（任务级整体超时配置，0=禁用）",
                    new Agent.Core.Models.AgentOptions().TaskTimeoutMinutes == 30);
                Check("AgentOptions.OllamaHttpTimeoutMinutes 默认 20（本地 Ollama HttpClient 超时，10-04 600s 腰斩实证后配置化加长）",
                    new Agent.Core.Models.AgentOptions().OllamaHttpTimeoutMinutes == 20);
                Check("use 命令解析（10-08 A）：裸 use / use --write / 纯空格缺路径 → false（命令层拦截，不进任务流——实证裸 use 漏进任务流跑 8 步）",
                    !Agent.Infrastructure.Project.UseCommandParser.TryParse("use", out _, out _)
                    && !Agent.Infrastructure.Project.UseCommandParser.TryParse("use --write", out _, out _)
                    && !Agent.Infrastructure.Project.UseCommandParser.TryParse("use   ", out _, out _));
                Check("use 命令解析（10-08 A）：带路径命中 + writeFlag 解析 + 普通输入不误判",
                    Agent.Infrastructure.Project.UseCommandParser.TryParse("use X:\\myproj", out var usePath1, out var useWrite1) && usePath1 == "X:\\myproj" && !useWrite1
                    && Agent.Infrastructure.Project.UseCommandParser.TryParse("use --write X:\\myproj", out var usePath2, out var useWrite2) && usePath2 == "X:\\myproj" && useWrite2
                    && Agent.Infrastructure.Project.UseCommandParser.IsUseCommand("use")
                    && !Agent.Infrastructure.Project.UseCommandParser.IsUseCommand("useful 命令")
                    && !Agent.Infrastructure.Project.UseCommandParser.IsUseCommand("使用Python写一个天气方法"));
                Check("run_command 超时档位（10-08 B）：沙箱 dotnet 验证命令 → 90s（对齐工程模式，防冷编译卡 30s 两次必败实证）；其余沙箱 30s 快速失败；工程模式 90s",
                    Agent.Infrastructure.Tools.RunCommandTool.ResolveTimeoutSeconds(null, "dotnet run") == 90
                    && Agent.Infrastructure.Tools.RunCommandTool.ResolveTimeoutSeconds(null, "python main.py") == 30
                    && Agent.Infrastructure.Tools.RunCommandTool.ResolveTimeoutSeconds("X:\\proj", "python main.py") == 90);
                Check("use 一步到位（10-08 C）：路径+任务拆分——最长存在前缀为路径，剩余为任务（含空格路径不误切）",
                    Agent.Infrastructure.Project.UseCommandParser.TrySplitPathTask("X:\\.NET AI\\LocalCodeForge 分析一下这个项目", out var useSplit1, out var useTask1)
                    && useSplit1 == "X:\\.NET AI\\LocalCodeForge" && useTask1 == "分析一下这个项目");
                Check("use 一步到位（10-08 C）：纯路径无任务 → false；路径打错回退最长存在祖先（盘根，归一化为 X:\\），剩余全当任务（边界文档注明）",
                    !Agent.Infrastructure.Project.UseCommandParser.TrySplitPathTask("X:\\.NET AI\\LocalCodeForge", out _, out _)
                    && Agent.Infrastructure.Project.UseCommandParser.TrySplitPathTask("X:\\NoSuchDirZZZ 任务", out var useSplit2, out var useTask2)
                    && useSplit2 == "X:\\" && useTask2 == "NoSuchDirZZZ 任务");
                Check("use 盘符归一化（10-08）：盘符形态 X: 补 \\ 为 X:\\（Path.GetFullPath(\"X:\") 盘符相对语义会解析成 cwd，静默错位）",
                    Agent.Infrastructure.Project.UseCommandParser.TrySplitPathTask("X:\\ 在X盘根目录新建一个空的test.txt 文件", out var useSplit3, out var useTask3)
                    && useSplit3 == "X:\\" && useTask3 == "在X盘根目录新建一个空的test.txt 文件");
                Check("会话写目标指代检测（10-08）：这个文件/该文件/刚建的文件 → true；新建文件（无指代）→ false",
                    Agent.Infrastructure.Project.ProjectPathDetector.HasReferentialPronoun("向这个文件里面写入一句内容")
                    && Agent.Infrastructure.Project.ProjectPathDetector.HasReferentialPronoun("读取一下该文件的内容")
                    && Agent.Infrastructure.Project.ProjectPathDetector.HasReferentialPronoun("刚建的文件里补一行")
                    && !Agent.Infrastructure.Project.ProjectPathDetector.HasReferentialPronoun("新建一个 notes.txt 文件"));
                Check("内容符合性·扩展提取（10-08）：内容：xxx 冒号形态与 写入一句 xxx 动词形态可提取（token 为归一化形态）；引号形态不受影响",
                    Agent.Infrastructure.Project.ContentConformance.ExtractRequirementTokens("向这个文件写入 内容：hello-e2e-20261008", Agent.Infrastructure.Project.RequirementScope.WithBareVerbs).Any(t => t == "hello e2e 20261008")
                    && Agent.Infrastructure.Project.ContentConformance.ExtractRequirementTokens("写入一句 上零休七，财富自由", Agent.Infrastructure.Project.RequirementScope.WithBareVerbs).Any(t => t.Contains("上零休七"))
                    && Agent.Infrastructure.Project.ContentConformance.ExtractRequirementTokens("写一个函数，内容：判断两个数相加", Agent.Infrastructure.Project.RequirementScope.WithBareVerbs).Any(t => t.Contains("判断两个数相加")));
                Check("完成门 B·本轮写入语义（10-08）：目标历史存在但本轮未写 → 失败（防假完成）；本轮写了目标 → 放行",
                    Agent.Infrastructure.Project.CompletionGate.Evaluate(
                        isWriteTask: true, wroteAny: true, writeRejected: false,
                        target: "X:\\_law_e2e_test.txt", targetIsFile: true,
                        requirementTokens: null,
                        writtenFiles: new[] { "X:\\other.txt" }) is not null
                    && Agent.Infrastructure.Project.CompletionGate.Evaluate(
                        isWriteTask: true, wroteAny: true, writeRejected: false,
                        target: "X:\\_law_e2e_test.txt", targetIsFile: true,
                        requirementTokens: null,
                        writtenFiles: new[] { "X:\\_law_e2e_test.txt" }) is null
                    && Agent.Infrastructure.Project.CompletionGate.Evaluate(
                        isWriteTask: true, wroteAny: true, writeRejected: false,
                        target: "X:\\_law_e2e_test.txt", targetIsFile: true,
                        requirementTokens: new[] { "hello-e2e-20261008" },
                        writtenFiles: new[] { "X:\\other.txt" }) is not null);
            }

            // ── 3. DashboardHost 冒烟：真起 Kestrel → HttpClient 验证 API ──
            var ws = Path.Combine(Path.GetTempPath(), "dash_smoke_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws);
            try
            {
                var dashHub = new Agent.Infrastructure.Events.TaskEventHub();
                var metrics = new Agent.Infrastructure.Metrics.SqliteMetricsCollector(Path.Combine(ws, "metrics.db"));
                var store = new Agent.Infrastructure.Events.TaskEventStore(Path.Combine(ws, "metrics.db"));
                store.Record("hist1", 0, new Agent.Core.Models.AgentEvent.Thinking("t"));
                store.Record("hist1", 1, new Agent.Core.Models.AgentEvent.Error("boom"));
                // 【10-08 ① 任务产物浏览】工作区预置产物文件——/api/files 断言数据源
                System.IO.File.WriteAllText(Path.Combine(ws, "sample.cs"), "public class Sample { }");
                // 【10-10 日志治理】内嵌服务显式日志级别（Warning）→ 启动 stdout 无 Kestrel 污染。
                // 捕获 Console.Out：StartAsync（Kestrel 启动日志发生窗口）→ 首个请求后恢复。
                var logCapture = new System.IO.StringWriter();
                var prevOut = System.Console.Out;
                System.Console.SetOut(logCapture);
                try
                {
                    var (url2, stop2) = await Agent.Dashboard.DashboardHost.StartAsync(
                        new Agent.Dashboard.DashboardOptions(ws, null, null, metrics, dashHub, store,
                            new Agent.Core.Models.AgentOptions(),
                            new Agent.Core.CommandHub(),
                            new Agent.Infrastructure.Memory.AgentMemoryStore(Path.Combine(ws, "agent_memory.db")),
                            new Agent.Infrastructure.Project.SymbolIndexStore(Path.Combine(ws, "symbol_index.db"))), CancellationToken.None);
                    using var probe = new HttpClient { BaseAddress = new Uri(url2 + "/") };
                    _ = await probe.GetStringAsync("");   // 触发一次请求，覆盖请求期日志窗口
                    await stop2();
                }
                finally { System.Console.SetOut(prevOut); }
                var captured = logCapture.ToString();
                Check("Dashboard 启动 stdout 无 Kestrel/Hosting 日志（显式日志级别 Warning，10-10 日志治理）",
                    !captured.Contains("Now listening") && !captured.Contains("Microsoft.Hosting.Lifetime")
                    && !captured.Contains("Application started") && !captured.Contains("Hosting environment"));

                // 【10-14 B2】注册抛错测试路由（仅测试期；StartAsync 前设置，断言后还原）
                Environment.SetEnvironmentVariable("LAW_DASH_TEST_THROW", "1");
                var (url, stop) = await Agent.Dashboard.DashboardHost.StartAsync(
                    new Agent.Dashboard.DashboardOptions(ws, null, null, metrics, dashHub, store,
                        new Agent.Core.Models.AgentOptions(),
                        new Agent.Core.CommandHub(),
                        new Agent.Infrastructure.Memory.AgentMemoryStore(Path.Combine(ws, "agent_memory.db")),
                        new Agent.Infrastructure.Project.SymbolIndexStore(Path.Combine(ws, "symbol_index.db"))), CancellationToken.None);
                using var http = new HttpClient { BaseAddress = new Uri(url + "/") };
                var page = await http.GetStringAsync("");
                Check("GET / 返回嵌入单页（含标题 + 回放 API + 过滤/滚动开关）",
                    page.Contains("LocalCodeForge") && page.Contains("/events") && page.Contains("showTaskEvents")
                    && page.Contains("fbtn") && page.Contains("autoScroll"));
                Check("P0-1 品牌一致性：页签与头部标题均为 LocalCodeForge（推广改名同步，无旧名残留）",
                    !page.Contains("LocalAI Workbench"));
                Check("P0-2 健康常显：header healthLine + fetchHealth 读取 /api/health（模型端点/工程模式/运行开关）",
                    page.Contains("id=\"healthLine\"") && page.Contains("function fetchHealth")
                    && page.Contains("沙箱模式（CLI use <路径> 启用工程感知）"));
                Check("P0-3 浏览器通知：notifyTaskDone 挂钩 finalAnswer/error + 提交时请求权限（纯前端，SSE 事件驱动）",
                    page.Contains("function notifyTaskDone") && page.Contains("new Notification(")
                    && page.Contains("Notification.requestPermission"));
                Check("P0-4 历史任务筛选+重跑：taskQ/taskStatus 过滤控件 + rerunTask 复用提交链路 + stopPropagation 防回放",
                    page.Contains("id=\"taskQ\"") && page.Contains("id=\"taskStatus\"")
                    && page.Contains("function rerunTask") && page.Contains("event.stopPropagation();rerunTask"));
                Check("嵌入单页含 SVG 依赖子图（layoutOneDegree 纯函数 + renderDepSvg + 节点点击联动 selectDepFile + 箭头 marker）",
                    page.Contains("function layoutOneDegree") && page.Contains("function renderDepSvg")
                    && page.Contains("function selectDepFile") && page.Contains("depArrowG") && page.Contains("DEP_NODE_MAX"));
                Check("命令条吸顶结构：cmdBar 直属 colL（sticky 滚动容器直接子级，10-10 P1 修复）——防回退成卡片内嵌",
                    page.Contains("<div class=\"colL\">\n  <div id=\"cmdBar\"")
                    && page.Contains("#cmdBar { position:sticky; top:var(--headH)"));
                Check("事件分组卡片（10-10 ①）：renderGrouped 统一渲染 + 分组函数 + 卡片结构 + boxKey 折叠隔离",
                    page.Contains("function renderGrouped") && page.Contains("function groupEvents")
                    && page.Contains("function toggleCard") && page.Contains("isCollapsed(boxKey, start)")
                    && page.Contains("className = \"taskcard\"") && page.Contains("tc-head") && page.Contains("tc-sep")
                    && page.Contains("renderGrouped(box, eventsCache, {") && page.Contains("boxKey: \"detail\""));
                Check("②③④ Markdown/工具配对/折叠挂载（10-10）：mdToHtml 转义先行 + buildUnits 配对 + renderPair 块 + 两级折叠",
                    page.Contains("function mdToHtml") && page.Contains("function escHtml")
                    && page.Contains("function buildUnits") && page.Contains("function renderPair")
                    && page.Contains("className = \"toolpair\"") && page.Contains("collapsedPairs")
                    && page.Contains("function togglePair") && page.Contains("onclick=\"expandMd(this)\""));
                Check("前端 JS 顶层闭合无漂移（10-10 修复：showTaskEvents 残留多余 } 导致 script SyntaxError，整页 JS 失效卡'加载中…'）——防替换边界错位再犯",
                    !page.Contains("});\n}\n}\n// ── stats ──"));
                Check("Monaco 集成（10-08）：loadMonaco 懒加载 + 本地 /vendor paths + 代码块 data-lang 升级 + diff 左右对比 + 文本视图回退",
                    page.Contains("function loadMonaco") && page.Contains("paths: { vs: \"/vendor/monaco/min/vs\" }")
                    && page.Contains("data-lang") && page.Contains("function monacoCode") && page.Contains("monaco-diff-host")
                    && page.Contains("createDiffEditor") && page.Contains("function renderDiffText") && page.Contains("d.diff")
                    && page.Contains("function diffLang"));
                Check("Monaco vendor 本地资源就位（10-08 全量 min/vs：源码树 vendor 存在，csproj Content 构建复制到 bin\\vendor 自包含）",
                    System.IO.File.Exists(System.IO.Path.GetFullPath(System.IO.Path.Combine(
                        System.AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                        "src", "Agent.Dashboard", "vendor", "monaco", "min", "vs", "loader.js"))));
                var vendor = await http.GetAsync("vendor/monaco/min/vs/loader.js");
                Check("GET /vendor Monaco loader 200（10-08 本地静态服务：UseStaticFiles 挂 bin\\vendor，Monaco 懒加载数据源）",
                    vendor.StatusCode == System.Net.HttpStatusCode.OK);
                var stats = await http.GetStringAsync("api/stats");
                Check("GET /api/stats 200 + JSON 结构（含全量对齐的 suspicious/featureBreakdown 两数组，10-14 P5-24；executor/routingDecisions/toolRejections 为 null 时按 JsonOpts 省略）",
                    stats.Contains("\"summary\"") && stats.Contains("totalCount")
                    && stats.Contains("\"suspicious\"") && stats.Contains("\"featureBreakdown\""));
                var tasks = await http.GetStringAsync("api/tasks?take=3");
                Check("GET /api/tasks 200 + 数组", tasks.StartsWith("["));

                // 【P0-4】历史任务过滤（端点内结构性过滤：q 关键词 / status / since）
                var t0 = DateTimeOffset.Now;
                await metrics.RecordAsync(new Agent.Core.Models.TaskMetric
                {
                    TaskId = "flt_fail", SessionId = "s", UserInput = "修复编译错误",
                    Mode = "agent_pro", RoutingTier = "L0", ServiceId = "agent_pro", RoutingReason = "test",
                    Success = false, IsSuspicious = false, IsE2E = false, RetryCount = 1,
                    TotalMs = 1000, PlannerMs = 100, ExecutorMs = 500, ValidatorMs = 400,
                    LintBlockingErrors = 1, LintWarningErrors = 0, ErrorSummary = "编译错误 CS1001",
                    StartedAt = t0.AddMinutes(-10), FinishedAt = t0.AddMinutes(-10).AddSeconds(5),
                }, CancellationToken.None);
                await metrics.RecordAsync(new Agent.Core.Models.TaskMetric
                {
                    TaskId = "flt_ok", SessionId = "s", UserInput = "生成 2048 小游戏",
                    Mode = "small", RoutingTier = "L0", ServiceId = "small", RoutingReason = "test",
                    Success = true, IsSuspicious = false, IsE2E = false, RetryCount = 0,
                    TotalMs = 2000, PlannerMs = 200, ExecutorMs = 1000, ValidatorMs = 800,
                    LintBlockingErrors = 0, LintWarningErrors = 0, ErrorSummary = "",
                    StartedAt = t0.AddMinutes(-8), FinishedAt = t0.AddMinutes(-8).AddSeconds(5),
                }, CancellationToken.None);
                await metrics.RecordAsync(new Agent.Core.Models.TaskMetric
                {
                    TaskId = "flt_susp", SessionId = "s", UserInput = "分析日志文件",
                    Mode = "large", RoutingTier = "L0", ServiceId = "large", RoutingReason = "test",
                    Success = true, IsSuspicious = true, IsE2E = false, RetryCount = 2,
                    TotalMs = 3000, PlannerMs = 300, ExecutorMs = 1500, ValidatorMs = 1200,
                    LintBlockingErrors = 0, LintWarningErrors = 0, ErrorSummary = "",
                    StartedAt = t0.AddMinutes(-6), FinishedAt = t0.AddMinutes(-6).AddSeconds(5),
                }, CancellationToken.None);
                var tasksQ = await http.GetStringAsync("api/tasks?take=50&q=" + Uri.EscapeDataString("编译错误"));
                Check("P0-4 GET /api/tasks q 过滤：关键词命中 user_input/error_summary（1 条，无过滤混入）",
                    tasksQ.Contains("flt_fail") && !tasksQ.Contains("flt_ok") && !tasksQ.Contains("flt_susp"));
                var tasksFail = await http.GetStringAsync("api/tasks?take=50&status=fail");
                Check("P0-4 GET /api/tasks status=fail 过滤：仅非成功且非可疑（1 条）",
                    tasksFail.Contains("flt_fail") && !tasksFail.Contains("flt_ok") && !tasksFail.Contains("flt_susp"));
                var tasksSusp = await http.GetStringAsync("api/tasks?take=50&status=susp");
                Check("P0-4 GET /api/tasks status=susp 过滤：仅可疑成功（1 条）",
                    tasksSusp.Contains("flt_susp") && !tasksSusp.Contains("flt_fail") && !tasksSusp.Contains("flt_ok"));
                var tasksSince = await http.GetStringAsync("api/tasks?take=50&since=" + Uri.EscapeDataString(t0.AddMinutes(-5).ToString("o")));
                Check("P0-4 GET /api/tasks since 过滤：只含 started_at ≥ 边界（无 flt_fail/flt_ok，仍含 flt_susp 需视边界）",
                    tasksSince.StartsWith("[") && !tasksSince.Contains("flt_fail") && !tasksSince.Contains("flt_ok"));
                var tasksNoF = await http.GetStringAsync("api/tasks?take=50&q=" + Uri.EscapeDataString("不存在的关键词xyz"));
                Check("P0-4 GET /api/tasks 过滤无命中 → 空数组", tasksNoF.Trim() == "[]");

                // 【10-17 ③】SQL 下推语义锁：过滤后 LIMIT（全量过滤再截断，非先取样本再内存过滤）
                await metrics.RecordAsync(new Agent.Core.Models.TaskMetric
                {
                    TaskId = "flt_q1", SessionId = "s", UserInput = "SQL下推验证 第二次",
                    Mode = "small", RoutingTier = "L0", ServiceId = "small", RoutingReason = "test",
                    Success = true, IsSuspicious = false, IsE2E = false, RetryCount = 0,
                    TotalMs = 100, PlannerMs = 10, ExecutorMs = 50, ValidatorMs = 40,
                    LintBlockingErrors = 0, LintWarningErrors = 0, ErrorSummary = "",
                    StartedAt = t0.AddMinutes(-4), FinishedAt = t0.AddMinutes(-4).AddSeconds(5),
                }, CancellationToken.None);
                await metrics.RecordAsync(new Agent.Core.Models.TaskMetric
                {
                    TaskId = "flt_q2", SessionId = "s", UserInput = "SQL下推验证 第三次",
                    Mode = "small", RoutingTier = "L0", ServiceId = "small", RoutingReason = "test",
                    Success = true, IsSuspicious = false, IsE2E = false, RetryCount = 0,
                    TotalMs = 100, PlannerMs = 10, ExecutorMs = 50, ValidatorMs = 40,
                    LintBlockingErrors = 0, LintWarningErrors = 0, ErrorSummary = "",
                    StartedAt = t0.AddMinutes(-3), FinishedAt = t0.AddMinutes(-3).AddSeconds(5),
                }, CancellationToken.None);
                var tasksQ2 = await http.GetStringAsync("api/tasks?take=1&q=" + Uri.EscapeDataString("SQL下推验证"));
                Check("10-17③ /api/tasks 过滤后 LIMIT：q 命中多行 take=1 只回最新 1 条（全量过滤再截断）",
                    tasksQ2.Contains("flt_q2") && !tasksQ2.Contains("flt_q1") && !tasksQ2.Contains("flt_fail"));
                var tasksQ2b = await http.GetStringAsync("api/tasks?take=1&q=" + Uri.EscapeDataString("SQL下推验证 第二次"));
                Check("10-17③ /api/tasks 过滤后 LIMIT：精确到旧一条仍可命中（非先取样本截断漏掉）",
                    tasksQ2b.Contains("flt_q1") && !tasksQ2b.Contains("flt_q2"));

                // 【P0-2】健康状态：模型端点 / 工程模式 / 运行开关（数据全来自注入 AgentOptions，无并行口径）
                var health = await http.GetStringAsync("api/health");
                Check("GET /api/health 200 + 结构（models 三角色 + executor + projectWritable + runtime 开关）",
                    health.Contains("\"models\"") && health.Contains("qwen2.5:1.5b") && health.Contains("law-unified")
                    && health.Contains("classifier") && health.Contains("\"projectWritable\":false")
                    && health.Contains("\"routing\":true") && health.Contains("strictOutput")
                    && health.Contains("\"taskTimeoutMinutes\":30"));
                var backups = await http.GetStringAsync("api/backups");
                Check("GET /api/backups 200 + 数组", backups.StartsWith("["));
                var files = await http.GetStringAsync("api/files");
                Check("GET /api/files 根列表 200 + children 结构（任务产物浏览）",
                    files.Contains("\"children\"") && files.Contains("sample.cs"));
                var fileBody = await http.GetStringAsync("api/files?path=sample.cs");
                Check("GET /api/files 文本文件 → {text:true, content}（预览内容）",
                    fileBody.Contains("\"text\":true") && fileBody.Contains("public class Sample"));
                var dl = await http.GetAsync("api/files?path=sample.cs&raw=true");
                Check("GET /api/files raw=true → 下载 200", dl.StatusCode == System.Net.HttpStatusCode.OK);
                var oob = await http.GetAsync("api/files?path=" + Uri.EscapeDataString("../../Windows/win.ini"));
                Check("GET /api/files 越界路径 → 400（PathBoundary 逐段判定拒绝）",
                    oob.StatusCode == System.Net.HttpStatusCode.BadRequest);
                var noDiff = await http.GetAsync("api/diff?taskId=x&rel=y");
                Check("无工程根 diff → 400 BadRequest", noDiff.StatusCode == System.Net.HttpStatusCode.BadRequest);

                // 【10-08 Monaco DiffEditor】/api/diff 附 original/modified 全文（非 tooLarge）——第三个实例带工程根 + 预置备份
                var projDir = Path.Combine(ws, "proj");
                Directory.CreateDirectory(projDir);
                var projFile = Path.Combine(projDir, "app.cs");
                System.IO.File.WriteAllText(projFile, "class A { int x; }\n");
                Agent.Infrastructure.Project.BackupService.Backup(ws, projFile, projDir, "diff1");
                System.IO.File.WriteAllText(projFile, "class A { int x; int y; }\n");
                var (url3, stop3) = await Agent.Dashboard.DashboardHost.StartAsync(
                    new Agent.Dashboard.DashboardOptions(ws, projDir, null, metrics, dashHub, store,
                        new Agent.Core.Models.AgentOptions(),
                        new Agent.Core.CommandHub(),
                        new Agent.Infrastructure.Memory.AgentMemoryStore(Path.Combine(ws, "agent_memory.db")),
                        new Agent.Infrastructure.Project.SymbolIndexStore(Path.Combine(ws, "symbol_index.db"))), CancellationToken.None);
                using var http3 = new HttpClient { BaseAddress = new Uri(url3 + "/") };
                var diffResp = await http3.GetStringAsync("api/diff?taskId=diff1&rel=app.cs");
                Check("GET /api/diff 附 original 全文（10-08 Monaco DiffEditor：备份全文）",
                    diffResp.Contains("\"original\":\"class A { int x; }"));
                Check("GET /api/diff 附 modified 全文（10-08：当前文件全文）",
                    diffResp.Contains("\"modified\":\"class A { int x; int y; }"));
                Check("GET /api/diff 附 diff 引擎结果（10-08：original/modified + diff 三字段）",
                    diffResp.Contains("\"diff\""));
                var healthProj = await http3.GetStringAsync("api/health");
                Check("GET /api/health 有工程根 → project 非空（projDir 实例）",
                    healthProj.Contains("proj") && healthProj.Contains("projectWritable"));
                await stop3();

                // 【10-10 Web 端回滚】端点注册 + 工程根守卫（有工程根的真实 dry-run/回滚行为由 BackupService 单元覆盖）
                var rbDry = await http.PostAsync("api/rollback/dry-run",
                    new StringContent("{\"taskId\":\"x\"}", System.Text.Encoding.UTF8, "application/json"));
                Check("POST /api/rollback/dry-run 无工程根 → 400（端点已注册）",
                    rbDry.StatusCode == System.Net.HttpStatusCode.BadRequest);
                var rbEmpty = await http.PostAsync("api/rollback/dry-run",
                    new StringContent("{\"taskId\":\"\"}", System.Text.Encoding.UTF8, "application/json"));
                Check("POST /api/rollback/dry-run 空 taskId → 400", rbEmpty.StatusCode == System.Net.HttpStatusCode.BadRequest);
                var rbExec = await http.PostAsync("api/rollback",
                    new StringContent("{\"taskId\":\"x\"}", System.Text.Encoding.UTF8, "application/json"));
                Check("POST /api/rollback 无工程根 → 400（端点已注册）",
                    rbExec.StatusCode == System.Net.HttpStatusCode.BadRequest);

                var evts = await http.GetStringAsync("api/tasks/hist1/events");
                Check("GET /api/tasks/{id}/events 200 + 平铺事件（后端补 type 短名，10-10 P0）",
                    evts.StartsWith("[") && evts.Contains("\"type\":\"thinking\"") && evts.Contains("boom"));
                var noEvts = await http.GetStringAsync("api/tasks/none/events");
                Check("GET /api/tasks/{id}/events 无事件 → 空数组", noEvts == "[]");
                // 【10-14 P3-6】SSE 建连首帧必须是 joined（非AgentEvent）——中途接入者据此知道
                // "实时流从此刻开始、此前历史走回放"。首帧写死在Subscribe 之后、事件循环之前。
                string joinedFrame;
                using (var sse = await http.GetStreamAsync("events"))
                using (var sseReader = new System.IO.StreamReader(sse))
                    joinedFrame = await sseReader.ReadLineAsync() ?? "";
                Check("GET /events 建连首帧 = joined 标记（中途接入提示，10-14 P3-6）",
                    joinedFrame.StartsWith("data: ") && joinedFrame.Contains("\"type\":\"joined\""));
                var noDeps = await http.GetAsync("api/deps");
                Check("无工程根 /api/deps → 400 BadRequest", noDeps.StatusCode == System.Net.HttpStatusCode.BadRequest);
                var cmdBad = await http.PostAsync("api/commands",
                    new StringContent("{\"input\":\"   \"}", System.Text.Encoding.UTF8, "application/json"));
                Check("POST /api/commands 空输入 → 400", cmdBad.StatusCode == System.Net.HttpStatusCode.BadRequest);
                var longInput = new string('x', 8001);
                var cmdLong = await http.PostAsync("api/commands",
                    new StringContent($"{{\"input\":\"{longInput}\"}}", System.Text.Encoding.UTF8, "application/json"));
                Check("POST /api/commands 超 8000 字符 → 400（10-14 P5-27 双端统一上限）",
                    cmdLong.StatusCode == System.Net.HttpStatusCode.BadRequest);
                // 【10-14 B2】全局异常处理中间件：抛错路由 → 中间件转 500 结构化 JSON（原为裸 500、无结构无日志）。
                var thrown = await http.GetAsync("api/__test_throw");
                var thrownBody = await thrown.Content.ReadAsStringAsync();
                Check("GET 抛错端点 → 500 结构化 JSON（10-14 B2 全局异常处理中间件）",
                    thrown.StatusCode == System.Net.HttpStatusCode.InternalServerError
                    && thrownBody.Contains("\"error\""));
                Environment.SetEnvironmentVariable("LAW_DASH_TEST_THROW", null);   // 还原（不泄漏到其他区段）
                var cmdOk = await http.PostAsync("api/commands",
                    new StringContent("{\"input\":\"stats\"}", System.Text.Encoding.UTF8, "application/json"));
                var cmdBody = await cmdOk.Content.ReadAsStringAsync();
                Check("POST /api/commands 合法命令 → 202 {queued:true} 且含 seq/pending（web 提交归属+位置）",
                    cmdOk.StatusCode == System.Net.HttpStatusCode.Accepted
                    && cmdBody.Contains("queued") && cmdBody.Contains("seq") && cmdBody.Contains("pending"));
                var qResp = await http.GetAsync("api/queue");
                var qBody = await qResp.Content.ReadAsStringAsync();
                Check("GET /api/queue → 200 且结构 {pending, items[{seq,input,submittedAt}]}（队列可见性）",
                    qResp.StatusCode == System.Net.HttpStatusCode.OK
                    && qBody.Contains("\"pending\"") && qBody.Contains("\"items\"") && qBody.Contains("\"submittedAt\""));
                var cancelResp = await http.PostAsync("api/cancel", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
                Check("POST /api/cancel 无运行中任务 → 409 {error}（无任务时明确拒绝）",
                    cancelResp.StatusCode == System.Net.HttpStatusCode.Conflict);
                var curResp = await http.GetAsync("api/current-task");
                var curBody = await curResp.Content.ReadAsStringAsync();
                Check("GET /api/current-task 无任务 → 200 {running:false}（任务归属端点）",
                    curResp.StatusCode == System.Net.HttpStatusCode.OK && curBody.Contains("\"running\":false"));
                await stop();

                // ── ③ 安全 token 加固：带 AuthToken 的迷你实例 → 无 token 401 / 带 token 200 ──
                var authWs = Path.Combine(Path.GetTempPath(), "dash_auth_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(authWs);
                var authMetrics = new Agent.Infrastructure.Metrics.SqliteMetricsCollector(Path.Combine(authWs, "metrics.db"));
                var authStore = new Agent.Infrastructure.Events.TaskEventStore(Path.Combine(authWs, "metrics.db"));
                var (authUrl, authStop) = await Agent.Dashboard.DashboardHost.StartAsync(
                    new Agent.Dashboard.DashboardOptions(authWs, null, null, authMetrics,
                        new Agent.Infrastructure.Events.TaskEventHub(), authStore, new Agent.Core.Models.AgentOptions(),
                        new Agent.Core.CommandHub(),
                        new Agent.Infrastructure.Memory.AgentMemoryStore(Path.Combine(authWs, "agent_memory.db")),
                        new Agent.Infrastructure.Project.SymbolIndexStore(Path.Combine(authWs, "symbol_index.db")),
                        "secret-token-123"),
                    CancellationToken.None);
                try
                {
                    using var authHttp = new HttpClient { BaseAddress = new Uri(authUrl + "/") };
                    var noTok = await authHttp.GetAsync("api/stats");
                    Check("③ AuthToken 鉴权：无 token 请求 → 401（中间件拒绝）",
                        noTok.StatusCode == System.Net.HttpStatusCode.Unauthorized);
                    var badTok = await authHttp.GetAsync("api/stats?token=wrong");
                    Check("③ AuthToken 鉴权：错误 token → 401", badTok.StatusCode == System.Net.HttpStatusCode.Unauthorized);
                    var okTok = await authHttp.GetAsync("api/stats?token=secret-token-123");
                    Check("③ AuthToken 鉴权：正确 token → 200", okTok.StatusCode == System.Net.HttpStatusCode.OK);
                    var hdrReq = new HttpRequestMessage(HttpMethod.Get, "api/stats");
                    hdrReq.Headers.Add("X-Dashboard-Token", "secret-token-123");
                    var okHeader = await authHttp.SendAsync(hdrReq);
                    Check("③ AuthToken 鉴权：header X-Dashboard-Token → 200（fetch 包装兼容）",
                        okHeader.StatusCode == System.Net.HttpStatusCode.OK);
                }
                finally { await authStop(); }

                // ── 4. /api/deps 工程依赖图谱：真工程（含类型级引用）+ 索引 → 概览 + 单文件依赖 ──
                var depWs = Path.Combine(Path.GetTempPath(), "dash_deps_" + Guid.NewGuid().ToString("N"));
                var depProj = Path.Combine(depWs, "proj");
                Directory.CreateDirectory(depProj);
                try
                {
                    File.WriteAllText(Path.Combine(depProj, "Customer.cs"),
                        "public class Customer { public int Id { get; set; } public string Name { get; set; } }\n");
                    File.WriteAllText(Path.Combine(depProj, "OrderService.cs"),
                        "public class OrderService { public Customer Find(int id) { return new Customer(); } }\n");
                    File.WriteAllText(Path.Combine(depProj, "Program.cs"),
                        "public class Program { static void Main() { var s = new OrderService(); var c = s.Find(1); } }\n");
                    var depStore = new Agent.Infrastructure.Project.SymbolIndexStore(Path.Combine(depWs, "symbol_index.db"));
                    Check("deps 冒烟：符号索引可建（3 个类型级符号）", depStore.BuildAndSave(depProj) >= 3);
                    var (depUrl, depStop) = await Agent.Dashboard.DashboardHost.StartAsync(
                        new Agent.Dashboard.DashboardOptions(depWs, depProj, null,
                            new Agent.Infrastructure.Metrics.SqliteMetricsCollector(Path.Combine(depWs, "metrics.db")),
                            new Agent.Infrastructure.Events.TaskEventHub(),
                            new Agent.Infrastructure.Events.TaskEventStore(Path.Combine(depWs, "metrics.db")),
                            new Agent.Core.Models.AgentOptions(),
                            new Agent.Core.CommandHub(),
                            new Agent.Infrastructure.Memory.AgentMemoryStore(Path.Combine(depWs, "agent_memory.db")),
                            new Agent.Infrastructure.Project.SymbolIndexStore(Path.Combine(depWs, "symbol_index.db"))),
                        CancellationToken.None);
                    using var depHttp = new HttpClient { BaseAddress = new Uri(depUrl + "/") };
                    var overview = await depHttp.GetStringAsync("api/deps");
                    Check("GET /api/deps 概览：files/edges/topReverse 结构 + 3 文件 + 依赖边 ≥1",
                        overview.Contains("\"files\"") && overview.Contains("\"topReverse\"")
                        && overview.Contains("OrderService.cs") && overview.Contains("Program.cs"));
                    var single = await depHttp.GetStringAsync("api/deps?path=OrderService.cs");
                    Check("GET /api/deps?path=OrderService.cs → direct 含 Customer.cs（类型级引用检测）",
                        single.Contains("Customer.cs") && single.Contains("reverse"));
                    var tuning = await depHttp.GetStringAsync("api/tuning");
                    Check("GET /api/tuning 200：三态 + 阈值结构（写降级/读升级/Complex 降级）",
                        tuning.Contains("writeDowngrade") && tuning.Contains("readUpgrade")
                        && tuning.Contains("complexDrop") && tuning.Contains("thresholds"));
                    var mem = await depHttp.GetStringAsync("api/memory?take=5");
                    Check("GET /api/memory 200：stats + rows 结构", mem.Contains("\"stats\"") && mem.Contains("\"rows\""));
                    await depStop();
                }
                finally
                {
                    try { Directory.Delete(depWs, true); } catch { }
                }
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }
    }

    // ── 区段 3535：3b Web 端回滚（dry-run → rollback 闭环） ──
    public static async Task Run3535()
    {
        Console.WriteLine("== 3b Web 端回滚（dry-run → rollback 闭环） ==");
        {
            var rbWs = Path.Combine(Path.GetTempPath(), "dash_rb_" + Guid.NewGuid().ToString("N"));
            var rbProj = Path.Combine(Path.GetTempPath(), "dash_rb_proj_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rbWs);
            Directory.CreateDirectory(rbProj);
            try
            {
                var rbFile = Path.Combine(rbProj, "Order.cs");
                File.WriteAllText(rbFile, "class Order { public int Id { get; set; } }");
                var rbOld = File.ReadAllText(rbFile);
                Agent.Infrastructure.Project.BackupService.Backup(rbWs, rbFile, rbProj, "rbT1");
                File.WriteAllText(rbFile, "class Order { public int Id { get; set; } public double Amount { get; set; } }");
                var rbNew = Path.Combine(rbProj, "TaxService.cs");
                File.WriteAllText(rbNew, "class TaxService { }");
                Agent.Infrastructure.Project.BackupService.MarkNewFile(rbWs, rbNew, rbProj, "rbT1");

                var (rbUrl, rbStop) = await Agent.Dashboard.DashboardHost.StartAsync(
                    new Agent.Dashboard.DashboardOptions(rbWs, rbProj, null,
                        new Agent.Infrastructure.Metrics.SqliteMetricsCollector(Path.Combine(rbWs, "metrics.db")),
                        new Agent.Infrastructure.Events.TaskEventHub(),
                        new Agent.Infrastructure.Events.TaskEventStore(Path.Combine(rbWs, "metrics.db")),
                        new Agent.Core.Models.AgentOptions(),
                        new Agent.Core.CommandHub(),
                        new Agent.Infrastructure.Memory.AgentMemoryStore(Path.Combine(rbWs, "agent_memory.db")),
                        new Agent.Infrastructure.Project.SymbolIndexStore(Path.Combine(rbWs, "symbol_index.db"))),
                    CancellationToken.None);
                using var rbHttp = new HttpClient { BaseAddress = new Uri(rbUrl + "/") };

                var rbDry = await rbHttp.PostAsync("api/rollback/dry-run",
                    new StringContent("{\"taskId\":\"rbT1\"}", System.Text.Encoding.UTF8, "application/json"));
                Check("3b dry-run 200 + 清单含恢复 Order.cs / 删除 TaxService.cs",
                    rbDry.StatusCode == System.Net.HttpStatusCode.OK
                    && rbDry.Content.ReadAsStringAsync().Result.Contains("Order.cs")
                    && rbDry.Content.ReadAsStringAsync().Result.Contains("TaxService.cs"));
                Check("3b dry-run 未执行改动（文件仍为新内容）",
                    File.ReadAllText(rbFile).Contains("Amount"));

                var rbExec = await rbHttp.PostAsync("api/rollback",
                    new StringContent("{\"taskId\":\"rbT1\"}", System.Text.Encoding.UTF8, "application/json"));
                Check("3b rollback 200 + 恢复 1 / 删除 1",
                    rbExec.StatusCode == System.Net.HttpStatusCode.OK
                    && rbExec.Content.ReadAsStringAsync().Result.Contains("\"restored\":1")
                    && rbExec.Content.ReadAsStringAsync().Result.Contains("\"removedNew\":1"));
                Check("3b 回滚生效：Order.cs 复原 + TaxService.cs 已删除",
                    File.ReadAllText(rbFile) == rbOld && !File.Exists(rbNew));

                await rbStop();
            }
            finally
            {
                try { Directory.Delete(rbWs, true); } catch { }
                try { Directory.Delete(rbProj, true); } catch { }
            }
        }
    }

    // ── 批次7 ⑩ 事件 schema 版本：task_events 版本列 + 老库迁移（无标题，追加于 91 区段之后）──
    public static async Task RunEventSchemaVersion()
    {
        var ws = Path.Combine(TestTmp, "ev-schema");
        Directory.CreateDirectory(ws);
        var run = Guid.NewGuid().ToString("N");   // 10-15 幂等修复：每次运行唯一化，防复跑残留冲突

        // 1. 新库：建表默认带 schema_version=1，写入/读取往返携带版本
        var db1 = Path.Combine(ws, "new_" + run + ".db");
        var store1 = new Agent.Infrastructure.Events.TaskEventStore(db1);
        store1.Record("t1", 0, new Agent.Core.Models.AgentEvent.Thinking("x"));
        var evs1 = store1.GetEvents("t1");
        Check("事件 schema：常量版本=1", Agent.Infrastructure.Events.TaskEventStore.CurrentSchemaVersion == 1);
        Check("事件 schema：新库写入记录版本=1",
            evs1.Count == 1 && evs1[0].SchemaVersion == Agent.Infrastructure.Events.TaskEventStore.CurrentSchemaVersion);

        // 2. 老库迁移：手工建旧 schema（无 schema_version 列）+ 塞一条历史数据 → Initialize 补列 →
        //    历史数据默认 1、新写入带 1
        var db2 = Path.Combine(ws, "old_" + run + ".db");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db2}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE task_events (
                    task_id     TEXT NOT NULL,
                    seq         INTEGER NOT NULL,
                    type        TEXT NOT NULL,
                    payload     TEXT NOT NULL,
                    created_at  INTEGER NOT NULL,
                    PRIMARY KEY (task_id, seq)
                );
                INSERT INTO task_events(task_id, seq, type, payload, created_at)
                VALUES ('legacy', 0, 'Thinking', '{"text":"old"}', 1);
            """;
            cmd.ExecuteNonQuery();
        }
        var store2 = new Agent.Infrastructure.Events.TaskEventStore(db2);   // Initialize 触发迁移
        var legacy = store2.GetEvents("legacy");
        Check("事件 schema：老库迁移后历史数据版本默认 1",
            legacy.Count == 1 && legacy[0].SchemaVersion == 1);
        store2.Record("t2", 0, new Agent.Core.Models.AgentEvent.Thinking("new"));
        var newEvs = store2.GetEvents("t2");
        Check("事件 schema：迁移后新写入记录版本=1",
            newEvs.Count == 1 && newEvs[0].SchemaVersion == Agent.Infrastructure.Events.TaskEventStore.CurrentSchemaVersion);
    }
}
