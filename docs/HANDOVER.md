# LocalAiWorkbench 项目交接文档

> 撰写日期：2026-09-22（基于源码通读 + 真实构建验证 + workspace 运行数据核对）
> 状态更新（截至 10-01 22:10）：15:47 修复编译错误（构建 0 错误）；18:10 修复「规则 F 冲突」打通 C# 多文件；20:30-22:10 工程感知 P0-P3 全量落地 + 路由 A+B + 终止条件 1+2+3（见下方状态块）；
> **10-01 跨任务文件连续性 A+B+C（§14-3w 补足）：** 用户测试日志暴露"接着更改上一个脚本"失效（任务2 read_file pyst.py 文件不存在 → 模型被迫重建而非更改，自定义改动丢失）。**方案 A（核心）· run 级工作目录**：一次 CLI 运行 = 一个工作空间——工具根从 task_&lt;id&gt; 子目录改为 run 根（workspace），task 目录不再创建（避免空壳误导 list_dir），taskId 仅作记忆归档溯源；打印改"[工作目录] &lt;run&gt;（任务归属 task_xxx）"；orchestrator/agentLoop 两分支 RunAsync/AgentRequest 全部传 run 根。**方案 B · 写动词词表补全**：IntentOverride.OperationVerbs 加 写/更改/改/改为/改变/变更/换成/重写/生成/追加/覆盖/清空/格式化/打印/输出（"写一个…脚本""更改这个脚本"此前未命中 L0.5 直连 large）。**方案 C · 豁免归一化**：OutputVerifier.IsTaskExpectedOutput 比较前 NormalizeForMatch（小写 + 非字母数字折叠空白 + 折叠连续空白，中文保留）——"Hello, World!" ↔ "hello world" 匹配，模板误判根治（此前 pyst.py hello 输出被 [检测] 模板误判）。tests 136→143（B 词表 5 项 + C 归一化 2 项）143/143。**端到端复测实证（abc_verify）**：任务1 "写一个名为 pyst.py 的脚本" → `[工作目录]…（任务归属 task_028c7232）` `[L0.5-写任务] 写任务 → 直连 large` → write_file → 完成（无模板误判）；任务2 "更改 pyst.py 脚本…" → 同 run 根 `read_file pyst.py → print("Hello, World!")` 命中 → 完成"已改为判断整数偶数的脚本：新增 is_even 方法…运行验证通过"——**真更改而非重建**。已提交 02522bf。
> **10-02 数据闭环·路由反馈（§14-3w 候选 C 落地）：** 用户确认推进"路由反馈闭环"（针对 large CPU 推理慢 10-15 分钟/任务；agent_pro 读任务保留逐层）。**复用现有数据，不新建表**——task_metrics 已记录 service_id/routing_tier/success/total_ms（无需 routing_decisions 表）。改动：①`IMetricsCollector` 加 `GetServiceStatsAsync(serviceId, routingTier, take)` 返回 ServiceStats(TotalCount/SuccessCount/AvgTotalMs)（SqliteMetricsCollector 子查询 LIMIT 后聚合）；②新建 `Routing/RoutingIntelligence.cs`（纯静态可测）`TrySuggestDowngrade`——保守三条件：候选样本 ≥3（不足不冒险）、成功率 ≥0.6、平均耗时 < 当前服务（省时才有意义），否则保持默认；③`ModelRouter` L0.5 写任务分支——注入 IMetricsCollector，决策前查 small/large 历史（tier=Rule），满足三条件 → 降级 small（日志"[数据闭环] L0.5 写任务降级 small（…）"），否则保持 large（现状不劣化）；查询异常 catch → 保持 large。**防打地鼠设计**：数据不足/成功率低/不省时一律保持已验证的 large 直连；降级后失败由升级链（small→large 跳层）兜底，最坏 = 多一轮 small 空响应立即升级，无长期劣化。tests 143→148（阈值 5 项：样本不足/成功率低/不省时/达标/当前无历史）148/148。**复测实证（loop_verify）**：写任务 `[L0.5-写任务] 写任务 → 直连 large`（当前 small 写任务样本不足，正确保持）→ 完成"已创建 hello.txt…验证通过"一次成功、无异常。后续 small 写任务历史（force_code_simple 路径）累积达标后自动降级。已提交 3c5eeae。
> **10-02 项目进度复盘（当前基线，最后更新）：** 本地 AI 编码助手内核强化进入**工程助手化收尾期**。**当前基线**：main @ 2c45859（**47 commits**）、构建 0 错误、tests **143/143**、工作区干净。**10-01 晚间 → 10-02 增量**：跨任务文件连续性 A+B+C（02522bf）——run 级工作目录（工具根 task 子目录 → run 根，任务间产物共享，"接着更改上一个脚本"真更改而非重建）+ 写动词词表补全 15 词（写/更改/改/生成…，写任务稳定 L0.5 直连 large）+ 豁免归一化（Hello, World! ↔ hello world 匹配，模板误判根治）。**主线全程回顾（工程化/数据结构性手段，放弃提示词工程）**：①P0 只读感知（ProjectScanner/use/双根）→ ②P1 可写+备份回滚+路径边界+测试 X 盘 → ③P1 端到端补完（文件引用排除/IntentOverride/备份根归一化）→ ④P2 工程级记忆（project_context.db + CompactBlock 瘦身 + 签名失效重扫）→ ⑤P3 模型级记忆（agent_memory.db + remember + 任务归档 + 失败教训警示注入）→ ⑥路由 A+B（写任务直连 large + 空响应跳层）→ ⑦终止条件 1+2+3（模板降级/循环熔断/收尾校准）→ ⑧跨任务文件连续性 A+B+C。**当前能力五层**：①路由（L0 查询词/L0.5 写任务直连 large/L1 九规则/L2 意图+确定性覆盖）；②工程感知（只读/可写回滚/工程缓存/模型记忆，双根越界保护）；③跨任务连续性（run 级工作目录 + 任务归档记忆 + 失败教训注入）；④任务安全（环境约束注入/免 Key 强制/交互拦截/路径边界/模板检测/循环熔断）；⑤SQLite 数据层（sessions/metrics/project_context/agent_memory 四库）。**遗留观察（未改，待决策）**：①读任务 small 空响应仍逐层升级（agent_pro 读取可胜任，保留）；②large CPU 推理慢（单任务 10-15 分钟）——数据闭环候选；③记忆治理（forget/列表/过期）未做；④agent_pro「工程感知中介大脑」定位保留，工程理解/规划增强为未来扩展点。**下一步候选（按价值排序）**：①路由反馈闭环（routing_decisions 表 + 失败率聚合自动调权——用户曾先做 A+B，C 记为后续）；②记忆治理（forget/过期/去重）；③agent_pro 工程理解专项（读代码→规划→改代码的中介链路）。
> **10-01 项目进度复盘（截至 10-01 22:10 基线快照，已过时）：** 本地 AI 编码助手内核持续强化中——**本轮 10-01 主线：从「任务沙箱生成器」演进为「本地工程助手」**，工程化/数据结构性手段推进（放弃提示词工程，防打地鼠）。**基线快照**：main @ 08ee9d1（39 commits）、构建 0 错误、tests 136/136、CLI 全链路复测通过（写任务一次成功 + 工程读取 P0/P2/P3 全命中）。**主线演进**：P0 只读感知（ProjectScanner + use/--project + 双根）→ P1 可写+备份回滚（--write + BackupService + run_command 路径边界 + 测试 X 盘）→ P1 端到端补完（TaskClassifier 文件引用排除/IntentOverride/备份根归一化/写任务直连 large）→ **P2 工程级记忆**（project_context.db + CompactBlock 注入瘦身 + 签名失效重扫）→ **P3 模型级记忆**（agent_memory.db + remember 工具 + 任务归档 + 失败教训警示注入）→ 路由层修复 A+B（写任务直连 large + 空响应跳层）→ 终止条件修复 1+2+3（模板降级/循环熔断/收尾校准）。**当前能力**：①路由三层漏斗（L0 查询词/L0.5 写任务直连 large/L1 九规则/L2 意图+确定性覆盖）；②工程感知四层（只读感知/可写回滚/工程缓存/模型记忆，双根越界保护）；③任务安全（环境约束注入/免 Key 强制/交互拦截/run_command 路径边界/模板与占位检测/循环熔断）；④SQLite 三库（sessions/metrics/project_context/agent_memory）+ 失败归档自动提炼。**遗留观察（未改，待决策）**：①读任务 small 空响应仍逐层升级（agent_pro 有时可完成读取，保留逐层）；②模板检测/熔断为保险机制，模型正常时不触发；③large CPU 推理慢（单任务 10-15 分钟）——数据闭环（路由决策入库 + 失败率聚合自动调权）为下一候选；④agent_pro 定位「工程感知中介大脑」，未来工程理解/规划能力扩展点。**下一步候选**：路由反馈闭环（routing_decisions 表 + 失败率自动调权）/ 记忆治理（forget/列表/过期）/ 工程理解增强（agent_pro 专项）。
> **10-01 终止条件修复 1+2+3：模板降级 + 循环熔断 + 收尾校准（§14-3w）：** 解决"任务已达成却不收尾/模板误判死循环"（A+B 复测暴露）。**方案 1 模板检测降级**：IsTaskExpectedOutput（任务输入含输出行 → 豁免 IsTemplateOutput）已生效，实证复现"创建 hello.txt 输出 hello"不再判模板、任务一次成功；修正错误断言（多行输出本就不判模板，IsTemplateOutput 仅单行判定）+ 补组合实证断言。**方案 2 循环熔断**：AgentLoop 字段 `_toolSignatures` + `BuildCycleSignature`（public static：工具名+首参+输出指纹前 60 字符）——同一签名第二次成功执行 = 重复无进展 → 日志"[熔断]" + 以工具验证结果强制 FinalAnswer 收尾。**方案 3 收尾校准**：Hello World 检测提示文案双向引导（若任务已达成直接总结收尾，否则补实现重验）。tests 131→136（组合实证/熔断签名 4 项/多行判定）136/136。**复测实证（cycle_verify）**：`[L0.5-写任务] 写任务 → 直连 large` → `[完成] 已创建 hello.txt，内容为 hello，已验证通过。` 一次成功收尾。已提交 8b16fa6。
> **10-01 路由层修复 A+B：写任务直连 + 空响应跳层（§14-3w）：** 路由层"规则 ≠ 能力匹配"诊断后的确定性修复。**方案 A**：ModelRouter L0.5 去掉 `ProjectWriteEnabled` 条件——`IntentOverride.IsWriteTask(input)` 即直连 large（全模式，含沙箱写任务；读任务保持 small），日志改"[L0.5-写任务] 写任务 → 直连 large"。**方案 B**：`AgentLoop.PromoteService`（空响应升级链）改为 public static 且 **small/agent_pro 空响应直达 large**（4B 空响应高发无意义，跳过中间层；large 空返回 null 走原层重试）。tests 122→126（PromoteService 跳层 3 项 + 写任务判定 1 项）126/126。**复测实证（router_ab_verify）**：沙箱写任务日志"[L0.5-写任务] 写任务 → 直连 large"[路由] Rule → large——绕链消失、无 small/agent_pro 空响应。**暴露独立新问题（未改，待方案确认）**：large 完成任务后最终回答被模板检测判"输出疑似模板内容"→ 不视为最终答案 → 重路由重做 → write_file/read_file/type 死循环直至达最大步数（hello.txt 已写入验证却无 FinalAnswer）。属终止条件/模板检测层，非路由层。已提交 230d397。
> **10-01 工程感知 P3 补足：失败教训注入（少走弯路）：** 归档记忆累积后让模型少走弯路——检索注入格式化升级。新建 `Memory/MemoryPromptBuilder.cs`（确定性静态）：命中记忆分两组注入——**历史失败教训（⚠ 警示组）**（kind=task 且 content 以"任务失败"开头，单列"⚠ 历史失败教训（请参考并避免重蹈覆辙）"）+ **历史参考**；同类失败 ≥2 次附"（同类失败已记录 N 次）"强化。`AgentLoop.LoadRelevantMemory` 改走 Builder（命中日志保留）。tests 115→122（空命中→null/失败警示组单列/失败与参考分节/计数强化/仅参考无警示 7 项）122/122。**复测实证（p3_avod_verify）**：上轮归档的失败任务（hello.txt 达最大步数）在同类任务中被检索命中——日志"[记忆] 历史记忆命中 1 条，已注入上下文"，模型（经升级链至 large）执行路径收敛。已提交 9efacee。
> **10-01 工程感知 P3 补足：任务归档记忆（§14-3w P3）：** 任务完成/失败自动提炼入库（kind=task，含失败教训）——补足模型不自发 remember 时的记忆积累。改动：①新建 `Memory/TaskMemoryExtractor.cs`（纯静态确定性提炼：topic=输入去换行截断 40 字；成功 content="任务完成（mode）：<最终交付截断 200>"；失败 content="任务失败（mode）：<错误消息拼接截断 200>"）；②`Program.cs`——orchestrator/agentLoop 两分支事件循环收集 Error 消息与 FinalAnswer，任务结束后 `ArchiveTask`（TaskMemoryExtractor.Extract → AgentMemoryStore.Add(kind=task, source=task_<id>)），归档失败不阻塞任务。tests 105→115（成功/失败提炼、未知错误兜底、topic/content 截断、task 记忆可检索 10 项）115/115。**复测实证（p3_archive_verify）**：失败任务（达最大步数）自动归档——`kind=task | topic=创建一个 hello.txt 文件，内容为 hello | content=任务失败（agent）：达到最大步数 20，任务终止。` 已提交 dcdf69a。
> **10-01 工程感知 P3：模型级记忆（§14-3w P3）：** 模型历史发现/决策持久化 SQLite（`workspace/agent_memory.db`），任务间真正"记住"。改动：①新建 `Memory/AgentMemoryStore.cs`——表 agent_memory（kind model=模型发现/task 预留、topic/content/source_task/hit_count），Add 写入、Search 关键词匹配（Tokenize：中文连续串≥2 字 + 标识符/文件 token≥3 字符，topic 命中计 2 分/content 计 1 分，按分数+历史命中数取 top3，命中后 hit_count+1 并回填返回）；②新建 `Tools/RememberTool.cs`——模型 ReAct 中可调 `remember(topic, content)` 主动存发现/决策（空值拒绝）；③DI 注册 store（agent_memory.db 与 sessions.db 同目录）+ RememberTool 入工具集；④`AgentLoop`——ctor 注入 store，任务开始 `LoadRelevantMemory(input)` 检索命中则注入"## 历史记忆（跨任务…）"块（日志"[记忆] 历史记忆命中 N 条"），与工程上下文块共存。**修复运行时 bug**：AddWithValue 传 null 报"Value must be set"（改 DBNull.Value）；Search 返回 HitCount 是 BumpHit 前旧值（BumpHit 改返回新计数 + with 回填）。tests 94→105（Tokenize/Add/Search 命中与截断/命中计数递增/空值拒绝/RememberTool 11 项）105/105。**复测实证（p3_verify2）**：预置记忆后任务日志"[记忆] 历史记忆命中 1 条，已注入上下文"，与 P2 工程上下文缓存命中共存；任务正常完成。已提交 288ed76。
> **10-01 工程感知 P2：跨任务记忆（§14-3w P2）：** 工程上下文缓存到 SQLite（`workspace/project_context.db`），跨任务/跨 CLI 重启复用，并引入注入瘦身。改动：①新建 `Project/ProjectContextStore.cs`——内存缓存 + SQLite 持久化（表 project_context：Overview/ContextBlock/CompactBlock + 文件系统签名 FileCount/DirCount/TotalBytes/MaxMtimeTicks + scan_version v1）；②`ProjectScanner`——ScanResult 增加 CompactBlock（**注入瘦身版**：概要+目录聚合树，模型需要文件详情时 list_dir/read_file 自取，token 从 ~1.5K 降至 ~0.3K）+ 新增 `ComputeSignature`（轻量 Walk 取 4 元签名，不构建树）；③`AgentLoop`——注入改 LoadProjectCompactBlock：ComputeSignature → TryGet 命中复用缓存块（日志"[工程上下文] 缓存命中"），miss 才 Scan+Put；④`Program.cs use`——缓存命中打印"（工程上下文命中缓存，未重新扫描）"；⑤DI 注册 store（project_context.db 与 sessions.db 同目录）。**修复一个入库 bug**：初始 CREATE 表缺 compact_block 列（后补 INSERT/SELECT 时漏改 DDL）→ INSERT 失败被吞、DB 恒空——Initialize 补列 + PRAGMA 迁移 ALTER。tests 83→94（ComputeSignature/缓存往返/签名失效/CompactBlock 瘦身 11 项）94/94。**复测实证（p2_verify2）**：use 两次——第二次"命中缓存未重新扫描"；两个读取任务注入均"[工程上下文] 缓存命中（签名一致），复用 CompactBlock"（不再每任务重扫重建），任务模型正确回答工程结构。**签名失效保证正确性**：工程文件被改 → 签名变化 → 自动重扫（tests 断言）。已提交 077b6ed。
> **10-01 写任务直连 large 优化（§14-3w 补充）：** 本地 small/agent_pro（4B）对"读+改"两步工具链生成不稳、常空响应（每层空等 10-20 分钟再升级），端到端 25-40 分钟。修复：ModelRouter 新增 **L0.5 覆盖**——工程可写（--write）+ `IntentOverride.IsWriteTask`（操作动词+文件/代码语境，读/查询任务不含写动词不受影响）→ 直接路由 large，跳过 small/agent_pro 两轮空响应。读任务保持 small（已验证能完成）。IntentOverride 重构：`Apply` 复用 `IsWriteTask`（public static，可测）。tests 76→83（IsWriteTask 7 项：添加注释/修改/创建=写，问答/读取/列出/无语境=非写）。**复测实证（p1_smoke6）**：`use --write` + "添加注释"任务日志 `[路由] Rule → large`（L0.5 直连，无 small/agent_pro 空响应），large 直接 read_file → edit_file → dotnet build（0 警告 0 错误）→ rollback 恢复原样；单次端到端 25-40 分钟 → **约 10-15 分钟**（省两轮 4B 空响应）。已提交 3205a45。
> **10-01 P1 端到端补完（§14-3w 补充）：** 模型实改工程文件 + 备份 + rollback 全链路走通。此前"添加注释"任务被 orchestrator 卡死（长度规则）、被 SimpleQA 意图误导（不调工具）、rollback 找不到备份（目录错位）——三处全部工程化修复：①**TaskClassifier 文件引用排除**（§14-3w 补充，tests 64→67）：长度规则前加正则 `[\w\-\.]+\.[a-z]{1,5}` 命中→走 AgentLoop，带路径的修改/读取任务不再误入编排（含"多文件"强词的工程任务仍编排，强词优先）；②**IntentOverride 意图覆盖**（tests 67→73，新建 `Routing/IntentOverride.cs`）：0.8B 分类器判 SimpleQA + 操作动词 + 文件/代码语境 → 确定性强制 CodeGenerate，防模型问答模式不调工具；③**force_code_edit 词表加"注释"**（RoutingRules.json）：L1 规则直接拦截"添加注释"类任务；④**BackupService 备份根归一化**（tests 73→76）：写工具传任务目录（runs/&lt;run&gt;/task_xxx）、rollback 传 run 目录，GetDirectoryName 差一层导致 rollback 找不到备份——新增 GetRunRoot（task_* 取父，否则自身）统一三函数。**端到端实证（p1_smoke5）**：`use --write` → "在 AgentOptions.cs 中 ProjectRoot 属性定义之后添加一行注释"走 L1 force_code_edit→small（空响应升级 agent_pro→large）→ large 模型 edit_file 实改工程文件（44→61 字符）→ read_file + dotnet build 验证（0 警告 0 错误）→ `rollback src/Agent.Core/Models/AgentOptions.cs` 恢复原样（git status 干净，注释残留 False）。备份落点 runs/&lt;run&gt;/.backup/&lt;HHmmss&gt;/相对路径。**观察**：本地 small/agent_pro 对两步工具链（read 定位+edit 精确替换）生成不稳，常空响应需升级 large 才执行（升级链兜底生效）；单次端到端约 25-40 分钟（CPU 推理）。已提交 b8c04ff（连同 IntentOverride/备份归一化）。
> **10-01 TaskClassifier 编排判定修正（§14-3w）：** 修复工程模式端到端卡死的根因——读取/构建类任务误入 orchestrator。三处数据修正（纯规则，非提示词）：①查询词表加 `读取`/`检查`（读取工程文件、构建验证类任务强制走 agent）；②项目词表移除 `工程`（与 complex_local 路由词表一致，此前 `运行 dotnet build 检查工程能否构建` 命中"工程"误入编排）；③长度阈值 40→60（带路径的读取类长描述不再触发长度规则）。验证：构建 0 错误、tests 57→64（TaskClassifier 7 项回归断言）64/64；端到端复测实证——`读取 src/Agent.Core/Models/AgentOptions.cs 中 ProjectRoot 属性附近的内容` 走 force_file_ops→small，模型 read_file 工程文件并正确回答（null=沙箱/非 null=工程模式），此前该任务在 orchestrator 卡死 15 分钟；`运行 dotnet build 检查工程能否构建` 走 L2 SimpleQA→small（此前误入编排）。**新观察（未改）**：L2 SimpleQA 意图可能抑制工具调用——build 任务路由正确但模型未实际执行 run_command。已提交 a253004。
> **10-01 测试临时目录迁移 X 盘（§14-3v）：** 用户要求不在 C 盘跑测试。tests/Agent.Infrastructure.Tests 临时目录由 `Path.GetTempPath()`（C:\Users\...\Temp\lawcheck\tests_*）迁移至 **`X:\.NET AI\LocalAiWorkbench\workspace\test-tmp\<PID>`**（workspace 已 gitignore，不入库；按进程隔离防并发互删；4 处测试块统一替换）。已创建测试目录并清理 C 盘 Temp 旧残留。验证：构建 0 错误、tests 57/57，目录落点实证在 X 盘（test-tmp\28608 等）。CLI 模型任务运行目录本就在 X 盘 workspace\runs，不受影响。已提交 d7a62d8。
> **10-01 §14-3t 端到端复测（§14-3u）：** CLI 工程模式实跑「查看 C:\Windows 目录」——模型先后尝试 `run_command dir C:\Windows`（→ 新边界拒绝：路径越界，退出码 126）与 `list_dir C:/Windows`（→ 文件工具越界拒绝），并正确理解限制（"只能访问工作区根目录下的文件"）。**越界双通道（run_command + list_dir）均拦截生效**。附带观察（非本补丁缺陷）：`dotnet build` 类任务被 complex_local 误判 orchestrator → Planner 空输出卡住（已知路由误判，待修）。
> **10-01 工程模式 run_command 路径边界（§14-3t）：** 修复 P0 验证观察 5——模型可用 run_command 绕过文件工具越界保护（dir C:\Windows）。RunCommandTool 新增 projectRoot 参数，工程模式（use/--project 后）启用 CheckPathBoundary：①命令含 `..\` 或 `../` 片段直接拒绝（防穿越）；②显式盘符绝对路径（含引号包裹的空格路径）经 Path.GetFullPath 解析，必须落在任务根 或 工程根 内，否则拒绝并返回非 0 退出码（下游正常判失败）。关键设计：检查在 NormalizePython **之前**（工具注入的系统 python.exe 绝对路径豁免，不会误拒 `python main.py` 的归一化结果）；cwd 保持任务目录不变（不引入语义变化）；沙箱模式（projectRoot=null）保持现状。验证：构建 0 错误、tests 52→57（盘符越界拒绝/穿越拒绝/工程根内绝对路径放行/相对路径放行/沙箱不受影响）57/57；已提交 ffe0f3e。
> **10-01 工程感知 P0 验证 + P1 实施（§14-3s）：** P0 系统验证 6 项——①use 概览稳定（63 文件/17 目录/入口 slnx+2×Program.cs/依赖识别）；②工程问答「解决方案/入口」→ agent_pro **零工具调用**直接基于注入的工程数据块回答（slnx + src\Agent.Cli\Program.cs + 三项目结构）；③list_dir "." → 完整工程根目录（双根生效）；④read_file 工程文件成功；⑤越界观察：list_dir/read_file 有路径保护，但模型可用 run_command dir C:\Windows 绕过（run_command 无路径限制=设计边界，工程模式风险点记录，未改）；⑥路由误判观察：读取类任务被 complex_local 误判走 orchestrator → Planner 空输出卡死（读取类应走 agent/force_file_ops，未改）。P1 实施：新建 Project/BackupService.cs（备份到 runs/<run>/.backup/<HHmmss>/相对路径 + ListBackups + Rollback 恢复最近备份）；AgentOptions.ProjectWriteEnabled（--write 授权，仅 ProjectRoot 非 null 生效）；WriteFileTool/EditFileTool/CreateDirTool 三写工具双根（工程可写时以工程根为基准，越界=任务根 或 工程根 内；写工程根内已存在文件前自动备份；TryAutoCreateCsproj 基准同步调整）；Program.cs：use 支持 --write 前缀 + rollback 命令（无参列备份/带参恢复）+ --write 启动参数与 --project 组合；DependencyInjection.AddAgent 加 projectWriteEnabled。验证：构建 0 错误、tests 42→52（新增 10 项 P1 确定性断言：沙箱越界拒绝/工程只读写任务目录不触碰工程/可写创建/覆盖自动备份/内容变更/edit 备份/rollback 恢复/路径穿越拒绝）52/52 全过；已提交 e03ead2。
> **10-01 OutputVerifier 抽取（§14-3e）：双路径检查链（凭据/stderr 剥离/结构/通用失败+双容错/占位）收敛为 Quality/OutputVerifier.cs 公共类单一来源（AgentLoop 1002→891、Orchestrator 966→801），构建 0 错误，28 项确定性回归全部通过**
> **10-01 Ollama 自动启动（§14-3f）：健康检查检测到 Ollama 未启动时，自动执行 `ollama serve`（后台常驻）+ 轮询就绪（1s×15），并注入 OLLAMA_MODELS 模型库根（默认库空时回退 X:\ollama\models，实测 10 模型全恢复）；端到端实测通过**
> **10-01 环境约束注入（§14-3g）：ExternalTaskDetector 任务类型识别（外部数据任务：天气/IP 定位/汇率等）→ 生成阶段注入环境约束数据块（免 Key + timeout=8 + 快速失败），AgentLoop / Orchestrator / ExecutorService 三路径单一来源；复测：天气任务成功（106s，模型直接采用 ip-api.com+wttr.in+timeout=8）对比此前失败（253s 挂死）**
> **10-01 写入时 timeout 静态检查（§14-3h）：WriteFileTool 对 .py 写入强制外部 HTTP 调用补 timeout=8（无 timeout 补、已带/跨行/非 py 不动），兜底"模型漏写 timeout"形态；复测：天气任务再次成功（115s，两处 requests.get 均 timeout=8）**
> **10-01 P0 可持续化（§14-3i）：git init 本机仓库（已有 Git 配置，基线 9affee6 + P0 9dc15b5）；ovtest 33 断言收敛为正式工程 tests/Agent.Infrastructure.Tests（入 slnx，dotnet run 一键回归）；死配置清理（RoutingRules 重名→complex_local/complex_cloud、删未接线的 EnableOrchestration、Core.csproj 残留 SK Ollama 包引用、slnx 空 folder）；冒烟质数任务路由正常**
> **10-01 P1 交互/守护强化（§14-3j）：新建 Tools/PyCodeGuard.cs（EnsureHttpTimeout 从 WriteFileTool 移入单一来源 + DetectInteractive 交互拦截：.py input / .cs Console.ReadLine 写入即拒绝，防 30s 挂死）；WriteFileTool/EditFileTool 共用；约束块通用化（去天气专用表述）；汇率任务回归成功（免 Key+timeout+快速报错）；C# HttpClient 结论：默认 100s 超时，30s kill 更早触发，无需扩展**
> **10-01 P2 内核强化（§14-3k）：IsPlaceholderOutput 校准（标题行 :/= 结尾不计占位，fib 形态不再误判，纯标题壳仍判）；AgentLoop 空响应升级链（small→agent_pro→large 强制升级，最高层才终止）；metrics 归因面板（%TEMP%\lawcheck\qmetrics2.py，失败按 空响应/凭据/超时/结构/网络/工具/其他 归因）——发现：19 失败中空响应 10 条（升级链覆盖）、新类型"修复输出与原文相同"×2 与"规划失败"×2 待决策**
> **10-01 A+B 升级链收官（§14-3l）：ExecutorService.FixFileAsync 检测"输出与原文相同"→升级 small→agent_pro→large 重试（每层至多一次）；PlannerService 第 2 次解析失败升级 Large 重试——冒烟实证：学生管理系统任务 Planner 输出截断→AgentPro 重试仍截断→升级 Large 一次成功（4 文件，dotnet run 退出码 0）**
> **10-01 P1-4 Prompt 矛盾清理（§14-3m）：ExecutorService SystemPrompt 删除 requests 硬禁令（改为外部任务按[环境约束]可用 requests+timeout）；PlannerService requests→urllib 替换条件化（外部任务保留）——禁令与约束块/参考实现（本身用 requests）矛盾消除；汇率任务复测成功（1 USD=7.2 CNY）**
> **10-01 agent_pro 上下文治理（§14-3o）：agent_pro 定位确认保留（未来工程感知的中介大脑，不做一刀切）。根治空响应根因=上下文膨胀（recovery 承接时历史含大段 stdout/stderr → 4B 上下文饱和 → 空输出；agent_pro 14 条失败 11 条空响应全在 recovery 承接后）。改动 1：recovery 重路由前重建精简上下文（删中间工具往返，保留需求+环境约束+失败摘要，模型自行 read_file 取文件）；改动 2：工具输出注入截断——仅 run_command（头 400+尾 300，判定仍用完整输出），read_file 保留完整（edit_file 精确匹配依赖完整文件内容；首版全截断实测破坏修复循环，已修正）。实证：同一天气任务修复前 recovery→agent_pro 连续空答案终止；修复后 agent_pro 主动 list_dir/read_file/运行诊断持续试错至 20 步上限——空响应形态消除。任务最终失败归因 wttr.in 键名领域知识（方案 2 已定不注入，接受模型能力边界）。新观察（未改）：agent_pro 不空响应后「反复失败不升级 large」（升级链只认空响应/输出相同）→ recovery 计数升级候选。**
> **10-01 工程感知 P0 实施（§14-3r）：新建 Project/ProjectScanner.cs（确定性扫描：文件树/入口/依赖识别，排除 obj/bin/.git/node_modules/workspace 等，深度≤6/树≤300 行/文件≤2000 截断，产出工程上下文数据块+CLI 概览）；AgentOptions.ProjectRoot（null=沙箱模式默认）；ListDirTool/ReadFileTool 双根（projectRoot ?? workspaceRoot，越界检查=任务根 或 工程根 内）；DependencyInjection.AddAgent 加 projectRoot 参数；AgentLoop 任务开始注入工程数据块；Program.cs 新增 use &lt;路径&gt; 交互命令 + --project 启动参数。冒烟：use 显示工程概览（62 文件/入口 slnx+Program.cs/依赖 4 csproj）；工程问答「列出 src 子目录」→ 模型 list_dir 真实工程目录并正确回答。构建 0 错误、tests 42/42。写工具仍锁任务目录（可写修改为 P1）。**
> **10-01 工程感知方案规划（§14-3q）：定位从「任务沙箱生成器」演进为「本地工程助手」，双模式并存（沙箱保留 + 工程模式新增）。能力三层：L0 只读感知（工程根+文件树+入口/依赖索引）→ L1 可写修改（edit_file 最小改动+备份回滚）→ L2 跨任务记忆（SQLite 持久化工程上下文）。架构：CLI use/--project 工程根 → ProjectScanner 扫描（排除 obj/bin/.git）→ 工程数据块注入（复用约束注入路径）→ agent_pro 中介理解规划 → small 执行 → 验证/回滚。安全：默认只读，--write 显式授权，越界检查以工程根为准。分阶段：P0 只读感知 → P1 可写最小改动 → P2 跨任务记忆。**
> **10-01 复测失败类型基线（§14-3p）：上下文治理+A/B/方案1 后串行复测 4 类失败任务——①交互计算器（此前挂死 249s/301s）：PyCodeGuard 拦截 Console.ReadLine 生效，4 分钟快速失败（模型改写交互程序能力不足→CS5001），挂死形态根治；②银行账户（此前规划失败）：成功（3 分钟，自定义异常+多文件验证通过，升级链生效）；③通用爬虫（此前假成功：输出全 0 判 OK）：真成功——example.com 真实抓取数据，假阳性防护拦截 2 次失败输出；④学生系统（回归）：成功（2 分钟，无回归）。metrics 基线更新：83 任务 61 成功/22 失败（73.5%，较 74 任务 73.0% 微升）；large 首次入 by service（2/2 成功）；剩余失败形态健康（快速失败而非挂死/空响应），多为模型能力边界。**
> **10-01 升级链缺陷修复（§14-3n）：空响应升级时重置 _lastStepFailed——升级是主动决策，不被 recovery 规则（只该响应工具失败）重路由回原层（16:56 天气任务实证升级白升）；17:13 天气复测一次成功（北京 22°C/晴）**
>
> **18:23 完成 3 案例多文件回归（订单 / 待办 / 学生全部一次编译通过）**
>
> **19:05–20:20 负向回归 6 任务 + 方案 1+3 / 2 / A / B（§11.5）；20:30–21:30 三项新防线（§11.5.6）；21:30–22:10 双路径统一 + 免 Key 闭环（§11.5.7）：JSON/凭据/stderr 同步 Orchestrator、通用失败词替代精确词表、wttr.in 免 Key 实现实证成功**



***

## 0. 一句话摘要

本地优先（Local-first）的 AI 编码 Agent 工作台：.NET 10 控制台应用，用户在 CLI 输入自然语言需求，系统在**本地 Ollama 四模型（classifier/small/agent_pro/large，全部本地推理）**之间做智能路由，自动完成「规划 → 生成代码 → 写文件 → 运行验证 → 出错自动修复」的闭环，并用 SQLite 记录会话、任务指标与跨任务记忆。



| 项目   | 说明                                                                                                                     |
| ---- | ---------------------------------------------------------------------------------------------------------------------- |
| 解决方案 | `LocalAiWorkbench.slnx`（新版 XML solution 格式）                                                                            |
| 目标框架 | net10.0（**已在产物中强制 net10.0，勿降级**）                                                                                       |
| 核心依赖 | Microsoft.SemanticKernel 1.80.1、SK OpenAI Connector（走 Ollama `/v1`，兼容端点；当前全部本地）、Microsoft.Data.Sqlite 11.0.0-rc.1 |
| 版本控制 | **本机 git 仓库**（main，40+ commits，无远端）；测试工程 `tests/Agent.Infrastructure.Tests`（136 项断言，入 slnx）                          |
| 入口   | `dotnet run --project src/Agent.Cli`（Ollama 未启动时 CLI 自动拉起，见 §14-3f）；工程模式 `use <路径>` / `--project` / `--write` / `rollback` |



***

## 1. 三层项目结构



| 项目                     | 职责                              | 依赖                    |
| ---------------------- | ------------------------------- | --------------------- |
| `Agent.Core`           | 抽象接口 + 纯模型（设计目标零依赖）             | 无（见 §12-10 的残留包说明）    |
| `Agent.Infrastructure` | 全部实现：主循环、编排、路由、工具、Lint、指标、会话    | Core + SK + Sqlite    |
| `Agent.Cli`            | 控制台入口：交互循环、事件渲染、模型健康检查、run 目录清理 | Core + Infrastructure |

### 1.1 Agent.Core（约 450 行）



* `Abstractions/`


  * `IAgentLoop.cs`：`RunAsync` 返回 `IAsyncEnumerable<AgentEvent>`—— 事件流式产出，为前端 / UI 预留

  * `IAgentTool.cs`：工具契约（Name / Description / InputSchema / InvokeAsync）

  * `IToolRegistry.cs`、`ISessionStore.cs`

* `Models/`


  * `AgentOptions.cs`：全部可调参数 + `ModelEndpoint`（Provider/Model/Endpoint/ApiKey）

  * `AgentEvent.cs`：record 事件层级（Thinking / ToolCall / ToolResultEvent / FinalAnswer / Error / Routing / PlanGenerated / FileWritten）

  * `AgentRequest.cs`、`ChatTurn.cs`（Core 零依赖，不引 SK 类型）

  * `Plan.cs`：`Plan` + `FileSpec`（Planner 输出结构，JSON snake\_case 映射）

  * `SystemPrompt.cs`：ReAct 模式系统提示词（内置大量踩坑规则）

  * `TaskMetric.cs`：19 字段指标模型

### 1.2 Agent.Infrastructure（约 4100 行，核心实现）



| 目录 / 文件                            | 职责                                                                          | 行数    |
| ---------------------------------- | --------------------------------------------------------------------------- | ----- |
| `AgentLoop.cs`                     | ReAct 主循环（§11 编译修复；§11.5.4/11.5.6/11.5.7 凭据+空答案+stderr 白名单+通用失败词+免 Key 注入；§14-3e 检查链改调 OutputVerifier；§14-3g 用户消息附加环境约束；§14-3n 空响应升级重置 _lastStepFailed；§14-3o 上下文治理：recovery 重建精简上下文 + run_command 输出注入截断、read_file 保留完整） | 827   |
| `Orchestration/Orchestrator.cs`    | 三阶段编排 Planner→Executor→Validator（§11.5.3/11.5.7 凭据+通用失败词+JSON 双路径统一；§14-3e 检查链改调 OutputVerifier；§14-3g 环境约束注入 Planner+Plan）         | 689   |
| `Quality/OutputVerifier.cs`        | **run_command 输出判定公共验证器**（凭据/stderr 剥离/结构/通用失败+双容错/占位；agent+orchestrator 双路径共用**单一来源**，根治双路径不一致，§14-3e） | 236   |
| `Orchestration/PlannerService.cs`  | 生成结构化 Plan + `SimplifyRunCommand` 后处理硬化（§12-1 多文件规则；§11.5.2 截断防护；§14-3l 解析失败升级 Large）           | 678   |
| `Orchestration/ExecutorService.cs` | 逐文件生成 / 修复代码（§14-3g 生成/修复消息附加 plan.EnvironmentConstraint；§14-3l 修复输出相同升级链）                                | 571   |
| `Orchestration/TaskClassifier.cs`  | 纯规则判断走编排还是 ReAct                                                            | 103   |
| `Routing/ExternalTaskDetector.cs`  | **外部数据任务识别**（确定性词表：天气/IP 定位/汇率等 → 环境约束数据块，§14-3g）                    | 46    |
| `Routing/`                         | 三层漏斗路由（ModelRouter / RuleEngine / IntentClassifier / RoutingRules.json）     | \~480 |
| `Tools/`                           | 6 个工具（list\_dir/read\_file/write\_file/edit\_file/create\_dir/run\_command） | \~630 |
| `Quality/PythonLintService.cs`     | ruff 优先，py\_compile 降级；F821/F811 阻断                                         | 189   |
| `Metrics/`                         | SqliteMetricsCollector + AgentLoop/Orchestrator 两个装饰器                       | \~500 |
| `Session/SqliteSessionStore.cs`    | turns 表存对话历史                                                                | 108   |
| `KernelFactory.cs`                 | 注册 4 个命名 ChatCompletion 服务                                                  | 72    |
| `DependencyInjection.cs`           | `AddAgent(options, workspaceRoot, dbPath)` 一站式装配                            | 126   |
| `RetryHelper.cs`                   | 指数退避重试（1s,2s,4s…）                                                           | 68    |

### 1.3 Agent.Cli



* `Program.cs`（735 行）：配置区 → 健康检查（§14-3f Ollama 连接失败自动启动+轮询+OLLAMA_MODELS 注入）→ 交互循环 → 自动分发；含 `plan` / `orch` / `stats` 调试命令（带「临时测试」注释，未清理）。



***

## 2. 架构总览



```mermaid
flowchart TD
    CLI["Agent.Cli Program.cs<br/>交互循环 + 事件渲染"] --> TC{TaskClassifier<br/>ShouldOrchestrate?}
    TC -- 否 --> LOOP["AgentLoop ReAct 循环<br/>路由→调LLM→工具/伪调用→业务检查"]
    TC -- 是（多文件/项目词/>40字）--> ORCH["Orchestrator 三阶段<br/>Planner → Executor → Validator"]
    LOOP --> ROUTER["三层路由漏斗<br/>L0查询词 → L1规则 → L2意图分类(1.5B) → L3映射"]
    ORCH --> ROUTER
    ROUTER --> KERNEL["SK Kernel<br/>4个命名服务<br/>classifier / small / large / agent_pro"]
    KERNEL --> TOOLS["6个工具<br/>write_file / run_command 等"]
    KERNEL --> LINT["Lint<br/>ruff / py_compile"]
    TOOLS --> FS["workspace/runs/task_xxx<br/>（每个任务独立目录）"]
    FS --> DB[("sessions.db 会话<br/>metrics.db 指标")]
```

核心要点：



* **两条执行链路**共用一个 Kernel 与工具集；CLI 启动时为整个会话建一个「根 DI 容器」，**每个任务再建一个独立 DI 容器**（因工具构造时绑定 `workspaceRoot`，必须指向该任务的子目录）。

* 所有事件以 `AgentEvent` 流式吐出，CLI 只负责渲染 —— 这是为将来接 UI 预留的接口。

* 全部本地调用统一传 `"think": false`（ExtensionData）关思考加速（从 DashScope 经验迁移）。



***

## 3. 模型端点配置（AgentOptions）



| 服务 ID        | Provider             | 当前模型                                            | 用途                                | 超时               |
| ------------ | -------------------- | ----------------------------------------------- | --------------------------------- | ---------------- |
| `classifier` | Ollama :11434        | qwen2.5:1.5b                                    | 意图分类（温度 0，极短 prompt）              | HttpClient 10 分钟 |
| `small`      | Ollama :11434        | qwen3-coder-flash-custom（注释备选 qwen2.5-coder:7b） | Executor 代码生成 / 简单任务              | 10 分钟            |
| `large`      | Ollama :11434        | qwen3.8-27b                                     | 写任务直连 / Search / Unknown 兜底     | 10 分钟            |
| `agent_pro`  | Ollama :11434        | qwen3.5:4b                                      | Planner 规划 / ComplexReason / 失败升级 | 10 分钟            |

行为参数：`MaxSteps=20`、`MaxHistoryTurns=10`、`Temperature=0.2`、`ExecutorMaxRetries=3`、`ComplexToPro=true`（ComplexReason 走本地 4B）。**当前全本地部署，无云端依赖**（DASHSCOPE_API_KEY 已不参与默认链路；代码保留 openai 兼容端点以支持未来切换）。

> **模型库位置（接手必知）**：Ollama 模型库根在 **`X:\ollama\models`**（实测 10 个模型：qwen3-coder-flash-custom/opt/agent、anarko/qwen3-coder-flash:30b、qwen2.5:1.5b、qwen3.5:4b、qwen2.5-coder:7b、nomic-embed-text 等），**用户环境未设 `OLLAMA_MODELS` 变量**——手动 `ollama serve` 会读默认空目录（`%USERPROFILE%\.ollama\models`，blobs 为 0）导致健康检查模型全 FAIL。CLI 自动启动（§14-3f）已做探测注入：进程环境有变量则继承，否则默认库有模型则不注入，否则回退 `X:\ollama\models`。



***

## 4. 执行链路 A：AgentLoop（ReAct 单模型循环）

触发：`TaskClassifier.ShouldOrchestrate(input) == false`（单文件词 / 查询词 / 设计词 / 默认）。

流程（`AgentLoop.RunAsync`，MaxSteps=20）：



1. **模糊输入检测**（"写一个函数" 等 ≤10 字无细节）→ 直接提示补充需求。

2. **载入会话历史**（SQLite 最近 10 轮）→ `IsNewTask` 命中（"写一个 / 创建…" 且无后续词）则清空历史。

3. 每步先做**路由决策**：首轮决策缓存复用；上一步失败则重新路由（触发升级）。

4. 调 LLM（`FunctionChoiceBehavior.Auto(autoInvoke:false)`），按响应分三种情况：

* **情况 A：SK 结构化 FunctionCall** → 逐个执行工具，结果以 `FunctionResultContent` 回填 chatHistory。

* **情况 B：纯文本 JSON 伪工具调用**（`{"name":..., "arguments":...}`）→ 手写花括号匹配解析成伪调用再执行。**这是小模型不走原生 function calling 的稳定性兜底，不要删。**

* **情况 C：最终答案** → 过「业务合理性检查」：


  * 代码请求但没写文件 → 注入 user 消息强制写（连续 3 次拒绝 → 报错终止）

  * 写了文件但没 run → 强制运行

  * 只 build 没 run（用户要比较 / 输出 / 演示）→ 强制 `dotnet run`

  * 运行失败 → 强制修复

  * 输出疑似 Hello World 模板 → 强制重做

  * 纯设计类请求（设计 / 规划 / 架构 / 方案 且无写代码词）→ 直接交付

1. **工具结果 4 道判定**（`ExecuteToolAsync`）：退出码 ≠ 0 /stderr 错误信号 /stdout 结构性错误（Traceback、error CS…）/ 占位符与模板输出检测 —— 任一命中判失败并触发路由升级。



***

## 5. 执行链路 B：Orchestrator（Planner → Executor → Validator 三阶段）

触发：`ShouldOrchestrate == true`（强多文件词 / 项目词 / 输入 > 40 字）。



1. **Planner**（agent\_pro 4B，温度 0.1）：输出结构化 Plan JSON（summary/language/files \[{path,purpose,requirements,skeleton}]/run\_command/expected\_output）。空输出重试 3 次、解析失败带错误反馈重试 1 次。**后处理&#x20;**`SimplifyRunCommand`**&#x20;做了大量硬化**（务必保留，全部是踩坑对策）：

* 提取用户指定文件名（如 parse\_csv.py）强制入口改名并重建 run\_command

* 简化 run\_command（python xxx.py/node xxx.js/dotnet run）

* 禁词替换：matplotlib→ASCII 图、numpy→list/dict、pandas→csv 模块、requests→urllib.request、flask/django→标准库

* 过滤独立示例数据文件（create\_sample/init\_db/seed…）→ 职责并入入口脚本

* 强制注入「数据文件不存在则创建」、argparse 必须有 default、csproj 用 net10.0

1. **Executor**（small coder 模型，温度 0.1）：按 Plan 文件顺序逐文件生成；**已生成文件内容截断 1500 字符作为上下文传给后续文件**（保证接口一致）；后处理强制 argparse default、csproj TargetFramework。

2. **Validator**（循环，最多 ExecutorMaxRetries=3）：

* 先 Lint（ruff，F821/F811 阻断）→ 不过则按文件分组调 `FixFileAsync` 修复 → 回顶部重 Lint

* Lint 过 → 跑 run\_command（C# 自动补 `--project`）→ 失败则用正则从错误输出定位文件（支持 `File.cs(行,列): error CSxxxx`、`File "xxx.py"`、argparse usage 兜底、入口文件名兜底）→ 修复 → 重跑

* **修复内容与原文 diff 率 < 2% 视为「模型没理解」→ 终止**；最终成功 / 失败写入指标

1. C# 运行命令的 `--project` 注入：找到 Plan 里的 .csproj，把 `dotnet run` 改写成 `dotnet run --project xxx.csproj`（避免多 csproj 冲突）。



***

## 6. 路由体系（三层漏斗）



```
L0 查询词硬编码拦截（ModelRouter 内："看看/查看/看一下/列出/显示/有哪些文件/什么文件"）→ small

L1 规则引擎（RoutingRules.json，priority 降序）

L2 意图分类（qwen2.5:1.5b，/no\_think 极短 prompt，温度 0，输出 6 类之一）

L3 意图 → 服务映射：

&#x20;  SimpleQA / CodeGenerate / FileProcess → small

&#x20;  Search / Unknown → large

&#x20;  ComplexReason → ComplexToPro ? agent\_pro : large
```

规则类型：`regex` / `keyword_any` / `prefix_any` / `length_min` / `length_max` / `after_failure`（上一步失败 → 升级 agent\_pro，priority 200，最高）。

RoutingRules.json 当前 9 条规则（含 2 条重名 `complex_keywords`，见 §12-3）。



***

## 7. 工具层（6 个，全部绑定 workspaceRoot，路径越界检查）



| 工具            | 要点                                                                                                                                                                      |
| ------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `list_dir`    | 列出 \[DIR]/\[FILE]（含大小）；空目录给明确提示                                                                                                                                         |
| `read_file`   | 上限 200KB                                                                                                                                                                |
| `write_file`  | **大量后处理**：csproj 强制 net10.0 + obj/bin 隔离路径（防多项目共享 obj 冲突）；py 强制 argparse default；.cs 文件同目录无 csproj 时**自动补建**；csproj 文件名异常自动改名；拒绝空内容（白名单 `__init__.py` 等除外）              |
| `edit_file`   | old\_string 必须唯一，否则报错；拒绝空 old\_string                                                                                                                                   |
| `create_dir`  | 幂等                                                                                                                                                                      |
| `run_command` | **白名单**（python/dotnet/node/git/dir/ls/…）；禁 `-c`/`-e` 内联执行；30 秒超时并杀进程；`chcp 65001` + UTF-8 输出；`-p` 自动规范化为 `--project`；**超时返回&#x20;**`退出码：124`（2026-09-22 15:10 修复，见 §11） |



***

## 8. CLI 命令



| 命令                                          | 作用                                                     |
| ------------------------------------------- | ------------------------------------------------------ |
| 普通输入                                        | 自动分发（TaskClassifier），每任务建 `task_xxxxxx` 子目录 + 独立 DI 容器 |
| `plan <需求>`                                 | 只测 Planner，打印 Plan（调试用，带「临时测试」注释）                      |
| `orch <需求>`                                 | 只测 Orchestrator 全流程（调试用）                               |
| `stats` / `stats recent` / `stats failures` | 指标汇总 / 最近 10 条 / 最近失败                                  |
| `new`                                       | 开新会话（换 sessionId）                                      |
| `exit`                                      | 退出                                                     |

启动时：本地模型健康检查（Ollama `/api/tags` 探活 + 逐个模型是否已 pull，缺失给 `ollama pull` 命令）+ 清理 7 天前或超 50 个的旧 run 目录。



***

## 9. 数据落盘（workspace/）



* `sessions.db`：`turns` 表（session\_id /role/content /created\_at），241 行 / 74 会话（9-22 15:00 核对，此后新增未重计）

* `metrics.db`：`task_metrics` 表 19 列（模式、路由、成败、is_suspicious、重试数、各阶段耗时、lint 错误数、错误摘要），**58 条：40 成功 / 18 失败（69.0%），21 agent / 37 orchestrator，平均修复 0.47 次，平均耗时 123.7s**（9-22 22:10 核对）。注意：空答案 / 异常终止路径**不落指标**（见 §11.5.4）；成功率低主因是天气系列复测大量如实判失败（凭据 / 网络 / 模型能力），非回归退化

* `runs/<时间戳-随机4位>/task_<8位id>/`：每个任务独立工作区，共 51+ 个 run 目录

* 生成产物：C# **多文件项目已打通**（订单 / 待办 / 学生 3 案例验证，均 csproj + 模型 + 服务 + 入口 四文件一次编译通过）；此前均为单文件 C# 或单文件 Python



***

## 10. 当前实测能力（截至 2026-09-22 18:25）



* Python 单文件脚本（fib.py/prime.py/get\_ip.py/calculate\_stats.py/ 购物车类）基本稳定

* C# 单文件项目（订单管理 / TodoApp）成功，耗时 143–210 秒（本地模型慢）

* **C# 多文件项目已打通并完成 3 案例回归**（18:10–18:23，全部一次编译运行通过，无验证重试）：


  * 订单系统：OrderSystem.csproj + Order.cs + OrderService.cs + Program.cs（81.5s）

  * 待办事项：TodoProject.csproj + Todo.cs + TodoService.cs + Program.cs（99.3s）

  * 学生管理：Student.csproj + Student.cs + GradeService.cs + Program.cs（88.7s）

* 超时修复已生效：17:53 的交互式计算器任务（Console.ReadLine 循环）被判 **FAIL**（249 秒），不再假阳性成功

* **负向回归 6 任务**（19:05–19:16）暴露 3 类缺口：Planner 输出截断无兜底、语义失败假阳性成功、修复器对无行列号错误无能为力 —— 均已加固或明确定界（§11.5）

* **凭据型外部 API 缺口已堵**（20:05 天气任务失败 → 方案 A+B）：401/403/API key 类失败会判失败并引导改用免 Key 实现；空最终答案不再静默"[完成]"而是重试 1 次后明确报错（§11.5.4）



***

## 11. ⚠️ 历史编译故障与修复记录（已于 2026-09-22 15:47 修复并验证）

> **现状：已修复。**
>
>  
>
> `dotnet build`
>
>  通过（0 错误，2 个既有 CS8602 警告），冒烟运行正常。以下为修复过程留档。

**2026-09-22 15:10 对&#x20;**`AgentLoop.cs`**&#x20;/&#x20;**`Orchestrator.cs`**&#x20;/&#x20;**`RunCommandTool.cs`**&#x20;的修改曾引入编译错误，导致&#x20;**`dotnet build`**&#x20;失败：**



```
src\Agent.Infrastructure\AgentLoop.cs(726,10): error CS1513 应输入 }
```

### 根因（已逐行定位 + 临时副本验证）

`AgentLoop.ExecuteToolAsync` 的 `try` 块缺少闭合 `}`，且最终 `return (output, true);` 被移到了 `if (name == "run_command")` 块内部。修复后还会暴露第二个错误：



```
AgentLoop.cs(554,50): error CS0161 并非所有的代码路径都返回值
```

### 修复方法（已应用并验证：真实构建 0 错误编译，仅剩 2 个既有 CS8602 空引用警告）

找到 `AgentLoop.cs` 中 `ExecuteToolAsync` 的这段（当前约 723–727 行）：



```
&#x20;           return (output, true);

&#x20;       }

&#x20;       catch (Exception ex)
```

改为：



```
&#x20;           return (output, true);

&#x20;           }

&#x20;           return (output, true);

&#x20;       }

&#x20;       catch (Exception ex)
```

即：**补一行&#x20;**`return (output, true);`**（非 run\_command 路径的正常返回值）和一行&#x20;**`}`**（闭合 try 块）**，放在 `catch` 之前。

### 这次修改同时带来一个好消息

15:10 的改动实际上**修掉了「超时误判成功」的历史漏洞**：



* `RunCommandTool` 超时输出现在带 `退出码：124`（此前只有 "错误：命令执行超时"，不含退出码，被下游误判成功）

* `Orchestrator.RunCommandAsync` 与 `AgentLoop.ExecuteToolAsync` 都新增了 `result.StartsWith("错误") → 判失败` 的检查

* 修复后交互式死循环程序会被正确判失败（17:53 计算器任务已实证判 FAIL）



***

## 11.5 2026-09-22 晚：负向回归与四项加固（全部已应用并验证）

### 11.5.1 负向场景回归 6 任务（19:05–19:16，run 20260922-190538-9aee）

用户按 §14 建议构造 6 个负向任务实测「验证-修复闭环」：

| 任务 | 结果 | 结论 |
| --- | --- | --- |
| 银行账户#1（自定义异常类） | **Planner JSON 输出在 requirements 的 "using" 处截断 → 解析失败、重试 1 次仍失败 → "[错误] 规划失败，任务终止"** | 新缺口：截断无兜底 → 方案 1+3 |
| 仓储（泛型接口+内存实现） | 6 文件退出码 0，但 stdout "未找到ID为1的产品" → **假阳性成功** | 根因：Product 用 `Guid.NewGuid().GetHashCode()` 做 Id（随机），查 GetById(1) 必失败；退出码 0 骗过检查 → 方案 2 |
| 订单明细（Order+OrderItem+总价） | 5 文件运行成功，总价 45.49 vs 预期 300.00 | 软指标偏差，设计行为（§12 多文件观察） |
| 日志（ILogger+两实现+注入） | 6 文件运行成功，log.txt 实际落盘 | 正常（构造函数注入 OK） |
| 图形面积（抽象基类+多态） | 5 文件运行成功，面积 12.566/12 正确 | 正常（AreaCalculator.csproj 语义命名生效） |
| 银行账户#2（重试） | Planner 空输出 2 次、第 3 次成功；首 run 退出码 1 → CS0272（Account.Balance set 不可访问）→ 定位 Program.cs → 修复 → 重跑 0 | **「验证-修复闭环」首次实战成功** |

### 11.5.2 方案 1+3：Planner 截断防护（PlannerService.cs，已应用，构建 0 错误）

* 规划规则新增：每条 `requirements` **≤40 字**（实测过长输出会被截断）、文件数上限 **≤6**
* 规则 G 新增：csproj **项目名必须与任务相关**（不再复用 OrderSystem.csproj）、**requirements 里不要写任何 using 指令**（这是截断触发点）
* 解析失败重试 **1→2 次**，重试提示明确"可能是被截断" + 给出完整 JSON 结构模板
* 验证：`plan` 命令实测完整 JSON 无截断；银行账户修改后日志（task_e5195ae0）三项预期全部命中，且新增 CS7036 跨文件构造签名错误被修复闭环正确处理（定位 Account.cs → 补参 → 重跑 0）——**修复闭环第 2 次实战成功**

### 11.5.3 方案 2：语义失败信号检查（Orchestrator.cs，已应用，构建 0 错误）

* `RunCommandAsync` 新增 check 3.5：退出码 0 后、占位符检查前 —— stdout 含语义失败信号（未找到 / not found / not exist / 添加失败 / 创建失败 / 保存失败 / 删除失败 / 查询失败 / 更新失败 / 读取失败 / 写入失败 / 加载失败 / 操作失败 / 处理失败 / failed to / add failed）且 expected_output 未含该信号 → 判失败进修复闭环；**容错**：预期本身含信号 = 任务要演示失败路径，放行
* 命中时错误信息附加 `计划预期输出：{plan.ExpectedOutput}`，请模型"使实际输出与计划预期一致"（**数据补丁，不动 System prompt**，见 §11.5.5）
* 验证：回归 1 仓储一次通过无误伤；回归 2 产品查询真实拦截缺陷程序（输出"未找到产品"+ 漏 ID 2），但进入修复后 **FixFileAsync 返回与原文完全相同 → "模型没理解"守卫终止**（修复器提示词只有编译错误类诊断，无"语义失败"类，见 §12-14）

### 11.5.4 方案 A+B：凭据失败检查 + 空答案兜底（AgentLoop.cs，已应用，构建 0 错误）

* **A（检查 0.5）**：run_command 判定层新增外部凭据失败信号 `\b401\b`/`\b403\b`/`unauthorized`/`invalid api key`/`api key`/`api_key`/`appid`/`无效的密钥`/`认证失败` 等 → 判失败并注入指令"改用无需凭据的实现（如 wttr.in 免 Key 天气服务，或本地模拟/示例数据）"。背景：20:05 天气任务模型写 OpenWeatherMap + `YOUR_API_KEY` 占位 → 401 失败 → recovery 升级 agent_pro → **返回空答案 → 静默"[完成]"**（会话库留空 assistant 轮、指标不落）
* **B（空答案兜底）**：情况 C 入口 —— 模型返回空内容 → 重试 1 次，仍空 → `[错误] 模型连续返回空答案，任务终止`，不再静默交付
* 验证（重跑天气任务，task_34f710fb）：**B 端到端实测**——空内容 → "模型返回空内容，强制重试" → 仍空 → 明确报错终止；A 本轮未触发（失败提前发生在 `import requests` ModuleNotFoundError），但代码分析确认 20:05 的 401 输出必命中信号表

### 11.5.5 打地鼠规避原则（用户确认的开发约束）

* 对"语义失败修复器无从下手"，**否决**了"给 FixFileAsync 提示词加语义失败诊断类"（提示词工程打地鼠），改为**数据结构性补丁**：check 3.5 命中时把 `计划预期输出` 附进错误信息，System prompt 不动
* 信号表保持当前**最小集不随案例膨胀**（语义信号 15 个、凭据信号 11 个）；若补数据后仍失败，接受"明确失败 + 展示预期 vs 实际"为终态，不靠堆规则掩盖

### 11.5.6 20:30–21:30 三项新防线（全部已应用并验证）

**① stderr 无害警告白名单（检查 2 重构）**
* RequestsDependencyWarning（requests 库版本兼容提示，写入 stderr 但无害）此前会触发"stderr 非空即失败"，误伤成功程序。现在先按 `^.*RequestsDependencyWarning.*` 块（含其后缩进的 warnings.warn( 延续行）剥离，剥离后为空则放行
* 验证：21:01 复测任务 stderr 有该 Warning，**不再误判失败**（首次实战）

**② stdout JSON 错误检测（检查 3.5 扩展）**
* 原检查 3.5 只匹配以 `错误:/失败:/Error:/Failed:` 开头的行；21:01 复测发现程序把错误封装成 JSON（`{"error": "Connection aborted..."}`，退出码 0）可绕过 → 新增"任意行含 `"error"`（JSON 字段）且无成功迹象 → 判失败"
* 验证：构建 0 错误；21:08 复测中该分支由方案 A（401）先命中未直接触发，构造性场景 `{"error": ...}` 必然命中（代码级确认）

**③ Python 解释器归一化（RunCommandTool.cs）**
* 根因（排查实证）：`where python` 解析到**豆包沙箱 Python 3.14**（PATH 靠前，无第三方包），而裸 `pip` 是**系统 Python311**（有 requests）——造成"pip 已装但 python 找不到"的持续矛盾（20:24/20:38/20:53 三次复现）
* 修复：RunCommandTool 在**白名单校验之后**（避免首个 token 变路径被拒）对命令归一化——`python/python3` → 系统 Python 绝对路径（`py -c` 解析 + 常见路径兜底，结果缓存），独立 `pip/pip3` → `系统python -m pip`，`python -m pip` 受保护不重复替换
* 配套：沙箱 python 已补装 requests（2.34.2，一次性环境操作）
* 验证：21:01 复测 stderr 路径显示 Python311——**ModuleNotFoundError 消除**（实证）

### 11.5.7 21:30–22:10 双路径统一 + 免 Key 闭环（全部已应用并实证）

**背景**：发现多条防线只加在 AgentLoop（agent 路径），Orchestrator（orchestrator 路径）缺失，复杂任务走编排路径时漏检。逐项对齐：

**① JSON 错误字段检测同步到 Orchestrator（检查 3.5b）**
* AgentLoop 已有 `"error"` / `"error_` 检测；Orchestrator 的 15 语义信号不含 → 21:37 复测程序输出 `{"error_type": "URLError"}` 假阳性成功。已同步：Orchestrator stdout 含 `"error"` / `"error_` 且预期不含、无成功信号 → 判失败（expected 演示容错保留）

**② 凭据检查同步到 Orchestrator（检查 0.5）**
* AgentLoop 已有 401/403/api_key 等 11 信号；Orchestrator 无 → 21:49 复测程序输出 `HTTP错误: 403 - Forbidden` 假阳性。已同步（同构，含免 Key 参考实现注入，硬失败无容错）

**③ 通用失败词替代精确词表（双路径）**
* 根因：15 个精确词（添加失败/创建失败/…）遇组合词（获取IP失败/加载数据失败/urlopen error）必漏，逐个加词即打地鼠
* 工程化收敛：失败 / failed / `\berror\b` / 错误:（行首）+ JSON 字段 → 命中即进入失败判定；**双容错**：expected 含失败词（演示）或 stdout 含成功信号（成功/success/completed，正常报告）→ 放行；查找类信号（未找到/not found/not exist）单独保留
* 实证：22:01 复测 `网络错误: ...ip-api.com timed out` 被 `错误:` 拦截 → 触发修复

**④ stderr 无害警告整块剥离同步到 Orchestrator（检查 2）**
* AgentLoop 用正则整块剥离（含 warnings.warn( 延续行）；Orchestrator 只 Replace 词 → 残留 `...\requests\__init__.py:113: ` 前缀，**成功程序被误判失败**进入无意义修复循环（22:01 复测实证）
* 已同步为正则整块剥离（RequestsDependencyWarning 整行+延续行）

**⑤ 免 Key 强化（工程化，非提示词）**
* 凭据检查命中时注入 **wttr.in 免 Key 参考实现代码**（ip-api.com 定位 + wttr.in 天气，可运行），给模型"可直接采用的实现"而非让它自己想
* 检查 4.1 增加占位 API Key 检测（YOUR_API_KEY / YOUR_OPENWEATHERMAP_API_KEY / 你的密钥 → 判未完成）
* **闭环实证（22:07 成功任务）**：模型写带 Key 程序 → 凭据拦截（api_key）→ 注入参考实现 → 模型第 2 轮**实际采用 wttr.in** 输出 `IP: 8.8.8.8 / Temperature: 21°C / Humidity: 65%`（中间 ip-api.com 超时被通用失败词拦截、第 3 轮成功，stderr Warning 剥离放行）——免 Key 链路完整跑通

***

## 12. 已知问题 / 坑（接手必读）



1. **【已修复并回归】Planner 提示词「规则 F」冲突 → 已改为「规则 G」**：原有两个自相矛盾的「规则 F」（软引导多文件 / 强制单文件），后写的强制规则生效导致多文件请求总被压成单文件。18:10 已删除硬性单文件规则，改为一致的「规则 G」：用户明确要求多文件时**必须**多文件，并给出多文件编译安全约定（global namespace、每文件自带 using、类名 = 文件名、Program.cs 只放 Main、不写 `using <项目名>;`）。**已用订单 / 待办 / 学生 3 案例回归通过**（均 4 文件、一次编译运行成功）。

2. **【历史漏洞，已修已验】交互式程序被误判成功**：旧代码超时输出不带退出码 → 判成功（TodoApp/OrderSystem 曾假阳性）；15:10 修复（退出码 124 + "错误" 前缀判定）+ §11 编译修复后，17:53 交互式计算器已正确判 FAIL。仍建议：给 Executor 的 prompt 补「禁止 Console.ReadLine/while (true)」禁令（`SystemPrompt` 已有，Executor 没有）。

3. **RoutingRules.json 两条同名规则&#x20;**`complex_keywords`：priority 150（→agent\_pro 本地）与 85（→large 云端）语义冲突，150 先命中，85 是死代码。

4. `AgentOptions.EnableOrchestration`**&#x20;未接线**：实际分发走静态 `TaskClassifier.ShouldOrchestrate`，配置项无效（误导）。接线或删除。

5. **每任务新建整套 DI 容器**（AddAgent per task）：重复构建 Kernel / 工具，当前规模可接受，但扩到 UI 后要考虑复用。

6. **Orchestrator 重试计数语义混用**：Lint 修复分支 `retries++` 后 `continue` 回循环顶；run 失败分支在循环底 `retries++`。读代码时注意区分。

7. **Prompt 里大量「实测调优」补丁规则**（禁第三方库、net10.0 模板、示例数据自包含、argparse default、占位符 / 模板检测、业务合理性检查……）—— 全部是踩坑后的对策，**改前先理解，别轻易删**。

8. **metrics.db/sessions.db 中文在部分工具链下显示乱码**：写入正常，是读取端编码问题（CLI 内正常）。

9. **无 git、无测试**：建议尽快 `git init`，并把关键路径（Planner 后处理、路由、伪调用解析）补单测。

10. `Agent.Core.csproj`**&#x20;残留&#x20;**`Microsoft.SemanticKernel.Connectors.Ollama`**&#x20;1.80.1-alpha 引用**：注释写着「删掉所有 PackageReference，Core 零依赖」，但包引用还在（Core 代码未实际使用）。要么删引用，要么注明 Core 并非零依赖。

11. `AgentLoop.cs`**&#x20;474/477 行 CS8602 空引用警告**：既有警告，非本次引入，顺手可修。

12. `Program.cs` **里** `plan` / `orch` 是调试命令（标注「临时测试」），未从产品路径移除；工作区根目录还散落 `is_even_odd.py`、`list_files.py`、`weather_scraper.py`、`Program.cs` 等手工测试残留，可清理。

13. **Planner 输出截断：已缓解未根治**：requirements ≤40 字 + 文件 ≤6 显著降低截断率，但超长 JSON 仍有理论截断风险；解析失败重试 2 次是兜底不是根治。根治方向：分块输出或流式解析（成本高，暂缓）。

14. **修复器对"语义失败"（无行列号）能力有限**：FixFileAsync 只按编译错误类诊断工作，对"输出内容不对"（如查询漏 ID）会返回与原文几乎相同的代码 → diff<2% 守卫终止。缓解：错误信息附 `计划预期输出` 数据；已知边界：**此类失败会明确终止并展示预期 vs 实际，不会无限打转**。

15. **凭据型外部 API 高频复现**：模型反复写 OpenWeatherMap + 占位 Key（至少 3 次）；方案 A 只拦"运行后产生的失败输出"，**不查代码内容里的占位 Key**（WriteFileTool 仅拒空内容）。若程序因其他原因（如 import 失败）没跑到 API 调用，占位 Key 仍会漏过——最终靠空答案兜底 B 或明确失败收场。彻底解决需在写文件/SystemPrompt 层加"禁止需要注册 Key 的服务"（方案 C，用户未确认，勿擅动）。

16. **跨语言容错是精确匹配**：预期英文 "not found" vs 程序中文 "未找到" 不互认——方案 2 容错只在"预期包含信号"时放行。实测两次恰好拦对/放对，保持精确匹配，暂不别名化。

17. **【已修复】run_command 环境偶发异常**：2026-09-22 20:24/20:38/20:53 天气任务 `import requests` 报 ModuleNotFoundError，但 `pip install requests` 显示 "already satisfied"——根因是**双 Python 错位**：裸 `python` 命中豆包沙箱 3.14（无第三方包）、裸 `pip` 命中系统 3.11（有包）。21:00 已通过 RunCommandTool 解释器归一化 + 沙箱补装 requests 修复（§11.5.6③），复测实证 ModuleNotFoundError 消除。

18. **天气类任务的模型行为缺陷（已工程化缓解）**：模型反复写 OpenWeatherMap + 占位 Key（≥5 次）、外网服务失败后倾向文本搪塞/沉默——通过 §11.5.7⑤ 的**免 Key 参考实现注入**（凭据拦截时附 wttr.in 可运行代码）实证缓解：模型在修复轮**实际采用参考实现并成功取到真实天气**。残余：agent_pro 修复时仍可能沉默（B 兜底为明确失败）；ip-api.com 等外网服务偶发超时（网络层，非代码问题）。

19. **【已完成】双路径检查链重复实现 → 已抽公共 OutputVerifier（10-01）**：凭据 11 模式 / 通用失败词 / JSON 字段 / stderr 剥离此前在 AgentLoop 与 Orchestrator **各写一份（≈120 行重复）**，9/22 已因"只改一条路径"踩 3 次双路径不一致。已收敛为 `Quality/OutputVerifier.cs`（internal 静态类，无状态无新依赖）：`HasCredentialFailure` / `StripHarmlessWarnings` / `HasStructuralError` / `HasGenericFailure`（含 expected+success 双容错与查找类信号）/ `HasPlaceholderKey` / `IsPlaceholderOutput` / `IsTemplateOutput`。**判定逻辑单一来源**，失败后反馈文案保留两路径差异（免 Key 注入 / 计划预期输出）。信号集取两路径并集（AgentLoop 检查 3 新增 ZeroDivisionError 等）；stderr 阈值统一为"剥离后非空即失败"；顺手修正原 Orchestrator 的 NETSDK 替换顺序缺陷（先过滤 warning 行再做词替换）。AgentLoop 1002→891、Orchestrator 966→801、新增 271 行；构建 0 错误，28 项确定性回归（%TEMP%\lawcheck\ovtest）全部通过。

### 多文件回归中的观察（2026-09-22 18:25）



* `expected_output`**&#x20;是软指标**：3 个任务的输出格式与 Plan 预期略有差异（如学生管理预期 "平均值为：85.0" 实际 "88.10"），验证只看退出码 0 → 通过。这是设计行为；若要严格校验输出格式需另加机制（会降低成功率）。

* **Status 字段建模不稳定**：plan 测试里出现过 "枚举类型 Status"，实际执行多为 string 属性 —— 模型对类型建模有漂移，不影响编译运行；想统一可加一条提示词约束。

* **错误&#x20;**`using <项目名>;`**&#x20;未再出现**：规则 G 的约束在生效（此前出现过一次 `using OrderSystem.Order;`，被 Executor 正确忽略）。



***

## 13. 构建与运行



```
\# 前置：已安装 .NET 10 SDK；已启动 Ollama（ollama serve）并 pull 四个本地模型

cd "X:\\.NET AI\LocalAiWorkbench"

\# 构建（已恢复通过）

dotnet build LocalAiWorkbench.slnx

\# 运行（会做本地模型健康检查）

dotnet run --project src/Agent.Cli

\# 可选：大模型兜底需要

setx DASHSCOPE\_API\_KEY <你的key>   # 改后需重开终端
```

工作区根目录取 `AppContext.BaseDirectory` 上跳 5 级（从 bin/Debug/net10.0 回到项目根），产物落 `workspace/runs/`；`RoutingRules.json` 以 `CopyToOutputDirectory=PreserveNewest` 拷到输出目录。

**建议的快速验证路径**：`dotnet run --project src/Agent.Cli` → 输入 `用 C# 写一个待办事项项目，分 Todo 模型、TodoService、Program，多文件组织` → 观察 \[规划]/\[写入]/\[完成] 事件与产物文件数 → `stats` 看指标。



***

## 14. 建议的下一步开发方向（按优先级）



1. **【已完成】修复编译**（§11，15:47 验证）。

2. **【已完成】打通 C# 多文件生成**（18:10 规则 G 替换冲突规则；18:23 完成订单 / 待办 / 学生 3 案例回归，全部一次通过）。

3. **【已完成】负向场景回归**（19:05–19:16 六任务实测，详见 §11.5.1）：覆盖了跨文件引用（CS0272 跨文件属性访问、CS7036 构造签名、Guid 随机 Id 语义失败），闭环在编译类错误上两次实战成功；**语义失败类（无行列号）修复仍有限**（§12-14）——后续可做：让修复器在无 diff 时读 expected_output 重写（先补数据试，勿加提示词规则）。

3b. **【已完成】凭据与空答案兜底**（20:05 天气任务失败 → 方案 A+B，§11.5.4）。

3c. **【已完成】三项新防线**（20:30–21:30）：stderr 无害警告白名单 / stdout JSON 错误检测 / Python 解释器归一化（§11.5.6）。

3d. **【已完成】双路径统一 + 免 Key 闭环**（21:30–22:10）：JSON 错误检测、凭据检查、stderr 整块剥离同步到 Orchestrator；通用失败词替代精确词表；凭据拦截注入免 Key 参考实现并实证跑通（§11.5.7）。**可选后续**：ip-api.com 超时备选（换免费定位源或加重试）；Orchestrator 修复器"输出与原文相同即终止"的判定可考虑放宽（当前会误杀已成功程序的微调场景）。

3e. **【已完成】OutputVerifier 公共检查类**（10-01）：双路径检查链收敛为单一来源，根治"只改一条路径"的双路径不一致（§12-19）。构建 0 错误、28 项确定性回归通过。**后续**：ovtest 断言已收敛为正式工程 tests/Agent.Infrastructure.Tests（§14-3i，P0 完成）。

3f. **【已完成】Ollama 自动启动**（10-01）：健康检查探活失败（仅连接失败，HTTP 异常不触发）→ 自动后台 `ollama serve`（CreateNoWindow，不随 CLI 退出被杀）→ 每 1s 轮询 /api/tags 最多 15 次 → 就绪继续模型检查；启动失败/15s 未就绪打印原提示。**关键补充**：自动启动注入 `OLLAMA_MODELS` 模型库根（见 §3 模型库位置）——否则 serve 读默认空目录模型全 FAIL（实测首版即踩中，注入后 10 模型全恢复）。端到端实测：kill Ollama → CLI 启动 → 「正在尝试自动启动 → [OK] Ollama 在线（10 个模型已安装）→ 3 个本地模型全部 [OK]」。Program.cs 647→849 行。

3g. **【已完成】任务环境适配·阶段 1：外部数据任务环境约束注入**（10-01）：根因——天气类任务反复失败源于模型在生成层不知道环境事实（无 Key / 海外服务可达性未知 / 必须 timeout），此前免 Key 注入只在"运行快速失败"后触发，挂起形态漏过（10/01 14:22 实证 30s kill）。实施：① 新建 `Routing/ExternalTaskDetector.cs`（66 行，确定性词表：天气/气温/IP地址/根据IP/经纬度/定位/汇率/货币兑换/实时行情/股票行情，**识别任务类型有限集合，非失败形态无限集合**——与打地鼠的本质区别；命中返回环境约束数据块，非 SystemPrompt 非修复器规则）；② 三路径单一来源注入：AgentLoop 用户消息附加 / Orchestrator 传给 Planner 的 userInput 附加 + `Plan.EnvironmentConstraint`（Plan.cs 新增可变属性）/ ExecutorService GenerateFileAsync+FixFileAsync 上下文附加。**验证**：天气任务复测成功（14:48:17 agent/106s/退出码0，模型直接采用 http://ip-api.com/json/ + https://wttr.in/ + timeout=8 + 完整超时异常处理，输出广州 35°C 真实天气）；C# 多文件学生管理系统（orchestrator 路径）正常完成不受影响；fib 本地任务正常（无约束注入，不误伤）；OutputVerifier 28/28；构建 0 错误。metrics 63 条 44 成功/19 失败（69.8%，此前 67.8%）。**观察记录**：fib 输出（标题行+数据行）被既有模板检测判"疑似占位符（1/2 行）"但任务最终成功——既有边界，未动检查链。**后续阶段**：阶段 0 实测数据：ip-api.com 643-699ms / ipapi.co 751-1341ms / wttr.in 1300-4179ms 全部可达（2026-10-01），挂起为瞬时抖动，timeout 后仅秒级快速失败。

3h. **【已完成】写入时 timeout 静态检查**（10-01）：`WriteFileTool`（289→335 行）PostProcess .py 分支新增 `EnsureHttpTimeout`——确定性行级改写：`requests.*(` / `urlopen(` / `HTTP(S)Connection(` 外部 HTTP 调用无 timeout → 自动补 `, timeout=8`；**已带 timeout / 跨行调用（行内无闭合括号）/ 非 .py 文件一律不动**（保守防破坏）。与 csproj net10.0 / argparse default 同构的后处理风格。**定位**：阶段 1 注入约束让模型"大概率写对"，本层是**兜底**——即使模型漏写 timeout，挂起形态（30s kill）也变成秒级快速失败，被运行输出检查链接住。**验证**：ovtest 新增 5 项确定性断言（无 timeout 补 / 已带不动 / 跨行不动 / urllib 补 / 非 py 不动），33/33 通过（28 项原有 + 5 项新）；天气任务复测成功（15:09:35 agent/115s，产物两处 requests.get 均 timeout=8，WriteFileTool 检测已带不动）；构建 0 错误。metrics 64 条 45 成功/19 失败（70.3%）。天气任务时间线：14:22 失败（253s 挂死）→ 14:48 成功（106s，阶段 1 注入）→ 15:09 成功（115s，阶段 1+2 双保障）。**后续（已完成）**：C# HttpClient 结论为无需扩展（默认 100s 超时，30s kill 更早触发）；EditFileTool 同规则已完成（§14-3j，单一来源 PyCodeGuard）。

3i. **【已完成】P0 可持续化**（10-01）：① **git init 本机仓库**（本机 Git 2.54.0，用已有全局配置 BMDPLAinsong；.gitignore 忽略 .vs/bin/obj/workspace/；基线 commit 9affee6 58 文件，P0 commit 9dc15b5）；② **测试工程收敛**：ovtest 33 断言迁入正式工程 `tests/Agent.Infrastructure.Tests/`（零依赖断言 harness，控制台工程引用 Agent.Infrastructure，入 slnx；运行 `dotnet run --project tests/Agent.Infrastructure.Tests`，退出码 0=全过；历史 ovtest 保留在 %TEMP% 可弃）；③ **死配置清理**：RoutingRules.json 重名 complex_keywords 改名 complex_local(150)/complex_cloud(85)（语义行为不变，RuleEngine 仍加载 9 条）；删除未接线的 AgentOptions.EnableOrchestration（全项目无读取）；Core.csproj 移除残留 SemanticKernel.Connectors.Ollama 包引用（保持零依赖）；slnx 移除空 /workspace/ folder。**验证**：构建 0 错误；tests 33/33；冒烟"判断质数"任务路由（force_code_simple→small）与执行正常。**保留项**：Program.cs 的 plan/orch/stats 命令虽带「临时测试」注释但用户实际使用过，未删。

3j. **【已完成】交互程序拦截 + 工具守护收敛 + 约束通用化**（10-01）：① **新建 `Tools/PyCodeGuard.cs`**（public static，单一来源）：`EnsureHttpTimeout` 从 WriteFileTool 移入（实现不变，删除 WriteFileTool 本地重复版本——9/22 双路径不一致教训），新增 `DetectInteractive(content, relativePath)→string?`——.py 命中 `\binput\s*\(`、.cs 命中 `Console\.ReadLine\s*\(` 返回拒绝文案（"验证环境无法提供终端输入，请改为命令行参数/固定数据"），未命中 null；② **WriteFileTool**：PostProcess 改调 `PyCodeGuard.EnsureHttpTimeout`，写入前过 `DetectInteractive`（拒绝即 return，不落盘）；③ **EditFileTool**：写回前同守护（交互拒绝 + .py 补 timeout——原不经过 PostProcess，属覆盖缺口，现与 write_file 行为一致）；④ **约束块通用化**：ExternalTaskDetector.ConstraintBlock 去天气专用表述，改为"优先免 Key 且国内可达的服务（如 IP 定位 ip-api.com，天气 wttr.in）+ 必须 timeout=8 + 失败快速报错"——汇率/行情等非天气任务同样适用；⑤ **C# HttpClient 结论**：.NET HttpClient 默认 100s 超时 > CLI 30s kill 兜底，挂死必被 kill（与 Python requests 无默认超时不同），**无需扩展**（记录结论，不堆代码）。**验证**：tests 33→38（write .py input 拒 / write .cs ReadLine 拒 / 正常 .py 不拒 / edit .py 新内容 input 拒 / edit .py 无 timeout 补）38/38；汇率任务实测成功（force_code_simple→small，约束注入生效：fixer.io 免 Key + timeout + 快速报错，输出 1 USD=6.72 CNY）；构建 0 错误。**待确认**：ExecutorService SystemPrompt 中"Python 不用 requests/只用标准库 urllib"与外部任务实际用 requests 的隐性矛盾——拟条件化（本地任务禁、外部任务允许），属提示词层改动，需用户确认后做（暂缓）。

3k. **【已完成】P2 内核强化**（10-01）：① **模板误判校准**：`OutputVerifier.IsPlaceholderOutput` 原实现把 `EndsWith(:)`/`EndsWith(=)` 行计入占位计数——"标题行（冒号结尾）+ 数据行"恰好 50% 阈值误判占位（fib 形态）。校准：冒号/等号结尾行**不计入**数据特征计数（标题行是合法输出结构），新增**纯标题壳**规则（全部行均 :/= 结尾才判占位，如"结果:"、"订单:\n客户:"）。② **AgentLoop 空响应升级链**（P2-3）：模型返回空内容第 1 次时不再原模型重试——`PromoteService`（small→agent_pro→large，每层至多一次）强制升级服务重试，替换 `firstDecision` 供下一轮复用（空响应不是工具失败，不触发 recovery 路由语义）；已到最高层才原层重试、二次空响应才终止。③ **metrics 归因面板**：`%TEMP%\lawcheck\qmetrics2.py`（SQLite 直查），失败任务按 error_summary/输入文本归因七类（模型空响应/凭据/超时挂起/结构错误/网络不可达/工具错误/其他）+ 每类驱动建议（对应已实施强化，防打地鼠——先归因后动手）。**验证**：tests 38→42（标题+数据不判占位 / 单行纯标题壳判 / 全空标题壳判 / 键值输出不判）42/42；构建 0 错误。**归因发现**：67 条 metrics 48 成功/19 失败（71.6%）；失败 19 条中**模型空响应 10 条**（P2-3 升级链已覆盖）；新类型——**"修复输出与原文相同"×2** 与 **"规划失败"×2**——已由 §14-3l 两项升级链覆盖（待复测观察）。

3l. **【已完成】A+B 升级链收官**（10-01，归因面板驱动的第二批强化）：① **修复输出相同升级链（A）**：`ExecutorService.FixFileAsync` 修复模型固定 small——模型能力不足时无效输出原文件（FixFileAsync 判"修复失败"，metrics ×2）。实施：修复输出经后处理后 `IsSameOutput`（Trim 后精确比较）检测与原文相同 → **升级 small→agent_pro→large 重试**（每层至多一次；已到最高层返回原样，由调用方判修复失败）——模型能力层兜底，非提示词。② **规划失败升级链（B）**：`PlannerService` 原第 2 次解析失败仍用 AgentPro 同模型重试（4B 生成的 JSON 截断（requirements 过长）再试往往仍截断）。实施：**第 2 次解析失败升级 Large 重试**（替代原同模型重试）。**验证**：构建 0 错误；tests 42/42；冒烟实证 B——学生管理系统任务（16:21）Planner 原始输出被截断（expected_output 残缺）→ 第 1 次重试（AgentPro）仍截断 → "升级到 Large 重试" → Large 一次成功产出完整 4 文件 Plan → dotnet run 退出码 0、输出"学生平均成绩: 85.33"。A 的触发场景（修复输出相同）依赖模型行为无法主动构造，逻辑与 P2-3 同构，代码路径由构建+冒烟验证。**升级链全景（10-01 内核）**：P2-3 AgentLoop 空响应（small→pro→large）、A 修复输出相同（small→pro→large）、B 规划解析失败（pro→large）——三层分别覆盖 agent / orchestrator 修复 / orchestrator 规划。

3m. **【已完成】P1-4 Prompt 矛盾清理（requests 条件化）**（10-01）：矛盾——ExecutorService SystemPrompt 硬禁令"Python 不用 requests" 与 ① 外部数据任务环境约束块（授权免 Key 服务 + 参考实现）② AgentLoop/Orchestrator 内置天气/汇率参考实现（本身用 requests）③ 汇率/天气实测（模型一直用 requests）三方冲突；且本地任务实际用不到 requests，禁令纯冗余。实施：① ExecutorService SystemPrompt 删除 requests 禁令，改为条件化表述"若任务附带[环境约束]（外部数据任务），可按约束使用 requests 访问外部 HTTP 服务（必须带 timeout）"（numpy/pandas/flask/django/matplotlib 禁令保留）；② PlannerService `SimplifyRunCommand` 新增 `isExternal = ExternalTaskDetector.Detect(userInput) is not null`——**外部任务跳过 requests→urllib.request 替换（保留 requirements 中的 requests，与约束块/生成链路一致）**，本地任务仍强制替换（防乱用第三方库）；③ bannedReplacements 删除 requests 条目（消除替换表与外部任务矛盾）。**验证**：构建 0 错误；tests 42/42；汇率任务复测成功（16:42，constraint 注入 → 模型 requests+timeout=8 → 输出 1 USD=7.2 CNY，requirements/代码/运行三链路一致）。**原则**：非新增规则（无 SystemPrompt 堆砌），删冗余 + 条件化已有表述 + 与数据块（约束/参考实现）对齐。

3n. **【已完成】升级链缺陷修复：空响应升级被 recovery 覆盖**（10-01）：复测暴露——P2-3 升级链设 `firstDecision=large` 后，若 `_lastStepFailed=true`（此前工具失败），AgentLoop 140-152 行 `else if (_lastStepFailed) → RouteAsync`，recovery 规则（200）把决策重路由回 agent_pro，**升级白升**（16:56 天气任务实证：升级 large → recovery → agent_pro → 空响应 → 终止）。修复：空响应升级分支同时 `_lastStepFailed = false`——升级是主动决策，recovery 只该响应工具失败；下一轮走 else 复用升级后的 firstDecision。**验证**：构建 0 错误；tests 42/42；17:13 天气任务一次成功（模型直接写对 wttr.in 键名，输出北京 22°C/晴 + 当前位置 35°C/多云，timeout/错误处理齐备）——本次未触发修复链/升级链（模型写对），升级链修复的真实触发场景（修复失败→空响应→升级→不再被拽回）依赖模型行为不可控，由代码逻辑（_lastStepFailed 作用域/复用路径）保障。**待确认（方案 2 未实施）**：天气类任务 wttr.in 键名领域知识注入（ExternalTaskDetector 附加键名事实段）——用户仅确认方案 1，方案 2 暂缓。

4. **补 Executor prompt 禁令**：「禁止 Console.ReadLine/while (true)」，让交互式任务在规划层就规避，而不是靠超时判失败。

5. **清理 RoutingRules.json 重名规则**，合并语义；**EnableOrchestration 接线或删除**。

6. `git init`**&#x20;纳入版本控制**；为 TaskClassifier / RuleEngine / TryParseToolCallsFromText 补单元测试。

7. **远期**：CLI 之外的前端（AgentEvent 事件流已为 UI 预留；Routing 事件、指标统计都已齐备）。



***

## 附：本次核对的事实清单（2026-09-22）



* 构建：已恢复通过（0 错误，2 个既有 CS8602 警告 @ AgentLoop.cs 474/477）；修复前为 CS1513 @ 726 + CS0161 @ 554

* 冒烟：`echo exit | dotnet run` 正常启动退出，Ollama 健康检查通过

* 多文件回归（18:10–18:23）：订单 81.5s ✓ / 待办 99.3s ✓ / 学生 88.7s ✓，全部 4 文件一次编译运行通过

* 超时修复实证：17:53 交互式计算器判 FAIL（249s）

* metrics.db：27 条（21 成功 / 6 失败，77.8%；8 agent / 19 orchestrator；平均修复 0.41 次；平均 106.5s）

* sessions.db：241 行 / 74 会话（15:00 核对值）

* 文件时间线：HANDOVER.md 14:55 首撰 → 15:10 源码修改引入编译错误 → 15:47 编译修复 → 18:10 多文件规则修复 → 18:23 3 案例回归通过 → 19:05–19:16 负向回归 6 任务 → 19:40–20:20 方案 1+3 / 2 / A+B 实施与回归 → 20:30–21:30 stderr 白名单 / JSON 检测 / Python 归一化 + 沙箱 requests → 21:30–22:10 双路径统一（JSON/凭据/stderr 同步 Orchestrator）+ 通用失败词 + 免 Key 闭环实证

* 最新防线实证（20:30–21:30）：方案 A 第 2 次实战（401 拦截）；stderr Warning 不再误伤；Python311 归一化消除 ModuleNotFoundError；空答案兜底稳定（agent_pro 连续空 → 重试 1 次 → 明确终止）

* metrics.db：58 条（40 成功 / 18 失败，69.0%；21 agent / 37 orchestrator；平均修复 0.47 次；平均 123.7s）——天气系列复测如实判失败拉低成功率，非回归退化

* 负向回归（19:05–19:16，run 20260922-190538-9aee）：6 任务中 2 正常、4 暴露问题（截断 / 假阳性 / 软偏差 / 修复闭环首战成功），详见 §11.5.1

* 加固验证（19:40–20:20）：方案 1+3（plan 命令 + 银行账户 CS7036 修复实证）、方案 2（仓储无误伤 + 产品查询拦截实证 + 容错放行实证）、方案 A+B（天气任务重跑：B 空答案兜底端到端实证；A 信号表就位未触发）

* 修复闭环实战：第 1 次（19:16 CS0272）、第 2 次（19:41 CS7036）均成功；语义失败类（无行列号）仍终止于"模型没理解"守卫（能力边界，见 §12-14）