# LocalCodeForge 项目交接文档

> 撰写日期：2026-09-22（基于源码通读 + 真实构建验证 + workspace 运行数据核对）


















> > > > **10-13 批次8（10-13 B）：文本兜底路径 read_file 无上下文压缩（双路径漂移修复）**
> 根因：30B 走情况 B（TryParseToolCallsFromText 文本兜底）——该路径仅 run_command
> 截断，read_file 无短路/截断/折叠：X1 目标文件全文注入 38KB + read_file 结果 39KB
> 原样进上下文 = 77KB，CPU 30B prefill >20min 超时实证（"读盘根 index.html 并修复"
> 任务挂起假象：日志显示"卡在读文件后无下文"，实为 read_file 结果未压缩导致上下文膨胀）。
> 修复：压缩逻辑抽公共方法 CondenseToolResult（短路/截断/折叠 + _ctxToolBytes 累计），
> 情况 A 结构化调用与情况 B 文本兜底共用——消双路径漂移（ReadPathMatches 短路断言
> Suites_Tools 986-990 已有，正斜杠归一已覆盖）。
> 复测（读盘根 index.html 并修复语法错误）：[上下文预算] 目标文件全文注入（38344 字符）
> → read_file 短路命中（已全文注入，消双份冗余）——文本路径压缩生效，上下文从 77KB
> 降至约 38KB。任务 20min HttpClient 超时系 30B CPU 推理速度边界（3060 6G 部分
> offload，GPU 利用率 15%），非架构缺陷。
> tests 810 全绿 · commit 23465df

> > > > **10-13 批次7 架构缺陷盘点（⑨ 单模型串行 + ⑩ 事件 schema 版本化）**
> ⑨ 单模型串行（用户确认解读 A=意图分类去模型化）：热路径此前"多模型串行"——L2 意图
> 分类先调独立 classifier（qwen2.5:1.5b）再调主模型；而 L0/L0.5/L1 三层已全确定性规则。
> 新增 DeterministicIntentClassifier（纯词表六类判定：Search→ComplexReason→FileProcess
> (IsReadTask)→CodeGenerate(IsWriteTask)→SimpleQA→Unknown；Search 词表不含文件读词防
> 读任务误判走云端）；DI 注册切换（模型版 IntentClassifier 类保留可一行回退）。任务内
> 模型调用收敛为"主模型单模型串行"：省一次 1.5b 调用 / 少一个常驻模型依赖 / 分类确定。
> ⑩ 事件 schema 无版本：task_events 表此前无 schema 版本列、payload 无版本标签——schema
> 演进无锚点。TaskEventStore 加 CurrentSchemaVersion=1 + 表 schema_version 列（DEFAULT 1）
> + 老库 PRAGMA 检测 ALTER 补列迁移 + StoredEvent 加 SchemaVersion 字段（读写往返携带）。
> **验证**：build 0 错误 / tests **810 通过 0 失败**（799+11 新增：⑨ 7 断言 + ⑩ 4 断言，
> 无标题不污染 91 顺序核验）/ 91 标题顺序一致 / 无头冒烟健康检查 4 OK。
> 架构缺陷盘点 10 项至此全部处理（①~⑩）。

**10-13 批次6 架构缺陷盘点（⑧ 能力分层：工具按目标服务分层注入）**
> 根因级项：AgentLoop.BuildPlugin 无条件全量注入 10 个工具——small/agent_pro/large 三层模型
> 看到完全相同工具集（含 run_command 危险工具、analyze_code/dependency_analyzer/find_symbol
> 分析类、remember 记忆类），低层模型拿满权限，能力不分层。
> **落地**（数据驱动，非提示词）：新 ToolLayering 表（工具名→层级 basic/analysis/memory；
> 服务→允许层级：small=basic，agent_pro/large=全量；新增工具未登记默认 basic 防静默全可见）
> + AgentOptions.EnableToolLayering 开关（默认 true，可回退）。AgentLoop 改造：循环前预路由
> 定目标服务 → ToolLayering.Filter 过滤工具集 → BuildPlugin 按子集注入 + kernel 插件按任务
> 替换（防跨任务残留）；仅缩减时注入"可用工具"清单（让模型知道边界）；analyze_code 工程
> 引导按服务裁剪（small 无此工具时不注入，防引导模型调用不存在的工具）。预路由与循环内
> step0 路由同输入同调用，总调用次数不变。
> **验证**：build 0 错误 / tests **799 通过 0 失败**（788+11 新增：层级表/过滤/服务层级 11
> 断言，无标题追加不污染 91 顺序核验）/ 91 标题输出顺序一致 / 无头冒烟 SMOKE_EXIT=0。
> 后续轮：⑨ 单模型串行、⑩ 事件 schema 无版本。

**10-13 批次5 架构缺陷盘点（⑥ tests 单文件治理）**
> 根因级项：单文件回归已达 3949 行 / 91 个 Console.WriteLine("== … ==") 区段 / 788 Check 断言，
> 改一处牵动全文、区段间符号相互可见，任何"移区段"都易错位（本批次脚本核验即发现 8 处 Run 语义偏差）。
> **拆分**：12 个新文件——TestHarness.cs（公共静态 Harness：Pass/Fail/Check、TestTmpRoot/TestTmp、
> OutputVerifier 反射句柄 + Cred/Strip/Generic/Struct/PhKey 包装、Invoke/M/TryCompileRegex、
> MPhOutPublic/MTmplPublic）+ 11 个 Suites_*.cs 主题文件（OutputVerifier/Tools/Memory/Routing/Project/
> Backup/Tuning/Config/PathGuard/DiffSpectre/Dashboard，91 个 Run 方法全量覆盖）；Program.cs 瘦身为
> 入口 + test-tmp 清理 + Harness 接线 + 按原行号顺序调用 91 个 Run + 摘要 return。
> **权威核对**：sections_baseline.txt（91 标题→原行号）为顺序唯一权威；Python 脚本逐条对比
> "输出标题 == baseline 行号序"。
> **验证**：build 0 错误（9 warning 非阻断）/ tests **788 通过 0 失败**（与批次4 同基线，断言数不变）/
> 91 标题输出顺序与 baseline 完全一致 / 无头冒烟 SMOKE_EXIT=0。后续轮：复测与否由用户拍板。

**10-13 批次4 架构缺陷盘点（① 工具契约结构化：ToolResult 落地）**
> 根因级项：工具返回从"错误：xxx"文本前缀升级为结构化契约。新 `ToolResult(Success, Code, Message)` + `ToolErrorCodes` 16 码词表（单一来源，与 tool_rejections 落库码 out_of_bounds/pd_conflict/read_only/project_file/sensitive 对齐）。双轨消费：模型/渲染/E2E 仍吃 Message 文本（`ToString()==Message` + Contains/StartsWith/EndsWith/Length 兼容转发 + implicit operator string），机器判定读 Success/Code 字段——54 处 tests InvokeAsync 零改动编译通过。
> 改造面：IAgentTool/AgentToolBase 签名 `Task<ToolResult>`；10 工具（Remember/FindSymbol/ListDir/DependencyAnalyzer/CreateDir/ReadFile/WriteFile/EditFile/CodeAnalyzer/RunCommand）错误点按语义给码（OutOfBounds/SensitivePath/NotFound/TooLarge/InvalidArg/PdConflict/ProjectFile/ReadOnly/InteractiveInput/CmdNotAllowed/InlineExec/Timeout/PathEscape/SensitiveCmd），成功点 Ok。RunCommandTool 的 CheckPathBoundary 元组化 (Code, Message)，InvokeAsync 直接包 Fail；CheckProjectName 保持 string?（public、tests 用），调用处包 NotFound。
> 接入点：AgentLoop.ExecuteToolAsync `(output, toolResult.Success)`（1227 与 1360/1363 从文本前缀改为读 Success 字段；SK BuildPlugin 函数结果保持字符串契约）；Orchestrator 三处（677 WriteFileAsync / 706 RunCommandAsync 检查 0 / 874 ReadFileAsync）读 Success 字段；run_command stdout/stderr 提取继续在 Message 上做（显式 .Message）。IsToolSemanticFailure（渲染后文本的语义函数）与 `错误：工具` 特例豁免保留。
> 验证：build 0 错误 / tests 788 通过（770+18 新增：Ok/Fail 字段、Code 词表对齐、兼容转发、文本判定与字段判定等价性）/ 无头冒烟 SMOKE_EXIT=0。后续轮：FailureCategorizer 读 Code（需与 RecordMetric 存储格式对齐）、TestRunnerService 判测试输出（进程输出语义，保持文本）。
> **10-13 批次3 架构缺陷盘点（② 模型后端超时收窄 + ⑦ 记忆索引）**
> **背景**：架构盘点批次 3。② 盘点口径修正：KernelFactory 经排查**已支持双 Provider**（ollama → /v1 兼容端点、openai → 任意 OpenAI 兼容服务，含 strata）——"只能 Ollama"不成立；真缺口是 **openai 分支硬编码 5 分钟超时**（strata 本地 30B 走 openai 分支被 5 分钟腰斩，与 ollama 分支 20 分钟不一致）。⑦ 记忆搜索实证**全表扫描**：`SELECT ... FROM agent_memory` 拉全部行到 C# 逐个 Contains（N 行 × M 关键词）。
> **② 修复**：KernelFactory openai 分支超时 `5 分钟硬编码` → `options.OllamaHttpTimeoutMinutes`（语义=本地模型 HTTP 超时，云端短任务可配置调小）——strata 切换从此超时口径一致。
> **⑦ 修复**：AgentMemoryStore.Search 关键词过滤**下沉 SQL**（逐关键词 `topic LIKE $k ESCAPE '' OR content LIKE $k`，参数化）；`EscapeLike` 转义 `% _ \`（关键词含 Method1500 等下划线 token，不转义会把 _ 当单字符通配符误命中 Method1x500）；C# 只对命中行打分（topic+2/content+1 计分逻辑不变），行数级过滤替代全表传输。
> **验证**：构建 0 错误；tests **770 全绿**（766→+4：记忆索引 4 断言——下划线精确命中/无命中不拉全表/content 命中）；无头冒烟正常。
> **边界**：LIKE '%..%' 前缀通配天然不走 B-tree 索引，但记忆量级（数百条）行数过滤已足够；若未来上万条再评估倒排表（成本高，暂不做）。

> **10-13 批次2 架构缺陷盘点（③ 注入双实现归一）**
> **背景**：架构盘点批次 2——消"工具结果注入双实现漂移"。排查实证：截断逻辑此前三处各写各的——AgentLoop.SummarizeOutput（头尾+省略提示，进化版）、ExecutorService ×2 裸 `Substring` 1500/2000（原始版，无提示、无折叠感知）。折叠预算只加了 AgentLoop 主分支，Executor 分支无感知（漂移已具现）。
> **归一**：新 `src/Agent.Infrastructure/Context/ToolResultNormalizer.cs`——全注入点唯一截断实现 `Truncate(text, head, tail)`（含总字符数省略提示）。① AgentLoop 4 处调用替换 + 删除 SummarizeOutput 私有方法（537 run_command 摘要 160/80、547/727 run_command 400/300、559 read_file 6000/2000——行为等价）；② ExecutorService 2 处裸截断替换（267 预览 1500/0、490 修复参考 2000/0——获得省略提示，30B 不再误以为"内容完整"）。
> **设计要点**：归一的是"实现"不是"阈值"——各调用点预算参数保持场景语义（预览/修复/工具结果本就该不同），消的是同一策略三份实现的漂移风险；后续加折叠/预算升级只需改 ToolResultNormalizer 一处。
> **验证**：构建 0 错误；tests **766 全绿**（761→+5：ToolResultNormalizer 截断/边界/空串/tail=0 5 断言）；无头冒烟正常。

> **10-13 批次1 架构缺陷盘点（⑤ 配置校验 + ④ 持久层生命周期）**
> **背景**：用户要求按架构缺陷盘点先做批次 1——消两块债：⑤ 配置膨胀无校验（矛盾配置静默生效）＋ ④ 持久层明文只增不清（最大明文表 turns 无生命周期）。排查中修正了盘点口径：TaskEventStore/RoutingDecisionStore 的保留期清理**此前已接线**（硬编码 30/90），真缺口是 **turns 表（会话历史）只增不清**——每轮对话全量明文，无 Prune、无保留期。
> **⑤ 配置校验（新）**：`src/Agent.Core/Config/AgentConfigValidator.cs`——确定性纯函数 `Validate(AgentOptions) → List<string>`，零依赖可单测。校验：14 个正/非负整数类（步数/重试/记忆/超时/上下文预算×3/保留期×3）、4 个路由调权率∈[0,1]、Temperature∈[0,2]、语义关系（ContextFoldThreshold ≥ ContextToolResultBytes，否则截断永远优先、折叠形同虚设）、4 个端点（Provider∈{ollama,openai}、Model 非空、Endpoint 以 http(s):// 开头）。CLI 在 DI 构建后调用——有错红字逐条列出 + `Environment.Exit(1)`，绝不静默跑在矛盾配置上。
> **④ 持久层生命周期（收窄后）**：① AgentOptions +3 保留期配置（ChatTurnRetentionDays=90 / EventRetentionDays=30 / RoutingDecisionRetentionDays=90，后两者替换 Program.cs 硬编码魔法数）；② `SqliteSessionStore.PruneOlderThan(int days)` 新方法（turns 按 created_at 全局清理，返回删除条数，失败 0 fail-safe）；③ `ISessionStore` 接口 + PruneOlderThan（DI 注册的是接口，CLI 经接口解析——顺带修掉"具体类未注册会崩溃"的隐患）；④ CLI 启动区 3 处接线（RoutingDecision/TaskEvent 换配置 + turns 清理）。
> **验证**：构建 0 错误；tests **761 全绿**（753→761：+配置校验 5 + 会话生命周期 2 + 路径短路 1）；无头管道冒烟正常（启动→exit，无配置误杀、无崩溃）。
> **边界**：task_metrics 不清（stats/归因依赖全历史，量级小）；turns 按全局 created_at 清（不区分会话——过期会话历史一并回收，活跃会话仅保留最近 MaxHistoryTurns 轮有运行时价值）。

> **10-13 记忆分层 1+2+3（上下文不仅压缩，还要存储防失忆：折叠摘要 + 任务事实 + 文件状态记忆）**
> **背景**：分层上下文预算（f838289）解决"hot 上下文压缩"；用户进一步要求"存储防失忆"（MemGPT/Zep 式 memory hierarchy 的工程化落地）。存量基础设施 60% 可复用：sessions.db / agent_memory（去重合并+Search 注入）/ TaskEventStore（事件全量持久化= cold 层）/ TaskFeatureClassifier。本段按"1+2 3 顺手、4 向量检索等做完再评估"实施三阶段。
> **阶段 1 折叠摘要（ContextFolder，新）**：read_file 工具结果注入时累计字节（`_ctxToolBytes`，每任务重置）；累计超 `ContextFoldThreshold`(90KB，AgentOptions 可配) → 后续 read_file 结果走确定性折叠（零模型调用）：代码文件（.cs/.py/.ts/.html…）提取结构定义行（class/interface/record/def 等正则）+ 首 5 行 + 尾 5 行 + 行数；文本取头尾。语义保留优于纯截断（截断丢中间结构，折叠留定义）；完整内容声明"见会话事件存储"——TaskEventStore 旁路已全量持久化工具事件，cold 层可回放，折叠只影响 hot 上下文占用。优先级：短路（X1 已注入）> 单次超 25KB 截断 > 累计超 90KB 折叠。
> **阶段 2 任务事实（写文件数）**：TaskMemoryExtractor.Extract 新参数 `writtenFileCount`（追加在 finalAnswer 之后，既有调用零破坏）→ 归档 content 附 `｜写N文件`（成功/失败都记）。CLI ConsumeTaskAsync 事件循环统计 `ToolResultEvent(Name==write_file && Success)`，返回值扩为 4 元组，两处调用点（agent/orchestrator）透传 ArchiveTask。后续任务注入"上次写了 N 个文件"——30B 能判断"这是增量任务"而非"从零新建"。
> **阶段 3 文件状态记忆**：WriteFileTool 构造加可空 `AgentMemoryStore? memoryStore`（null 跳过，tests 默认构造零冲击）；写【已存在】文件成功后（有备份语义）→ `Add("file", 文件名, "工程内文件被修改（原 N 字符 → 新 M 字符），任务 X")`。kind=file 非 task → feature 不匹配只降权不剔除；后续输入含文件名 token（"index.html"）→ Search 自动命中注入"该文件最近改动"。DI 注入 `sp.GetRequiredService<AgentMemoryStore>()`。
> **验证**：构建 0 错误；tests **753 全绿**（742→753：+折叠 5 + 写文件数 3 + 文件记忆 3）。端到端复测预期：大文件任务第二轮起 `[上下文预算] 累计注入超` 折叠日志出现、归档记忆含写文件数、改过文件的下一任务注入文件状态记忆。
> **边界**：折叠仅作用于"新注入"（不回溯历史 chatHistory——防打地鼠，历史靠截断已受控）；阶段 4 向量检索（embedding+top-k）未做，等 1-3 复测后评估。

> **10-13 分层上下文预算（架构级修复：X1 全文 + read_file 全文 = 90KB 双份 → CPU 30B prefill >20min 超时）**
> **现象**（23:48 第三轮复测）：B2 轻量通道 + X1 内容注入全部生效——read_file 第一轮直接命中真实 `x:/index.html` ✓、写目标干净 ✓；但 read_file 返回 40KB 后下一轮 LLM 调用 1200s 超时。实测：40KB 单轮 = 93.5s（ollama law-unified CPU 30B），90KB 双份 = 25K tokens → 超 20 分钟。
> **根因（用户点破：上下文缺乏科学裁切压缩机制）**：同一文件内容注入两遍——X1 注入 40KB + read_file 结果 40KB；且 read_file 结果"保留完整"（edit_file 匹配依赖），大文件一次撑爆。
> **方案（数据结构性，阈值进 AgentOptions 可配）**：
> 1. **注入层阈值化**：目标文件 ≤ `ContextTargetInjectBytes`(40KB) → X1 全文注入 + 记录 `_injectedTargetFullPath`（read_file 短路用）；> 阈值 → 骨架注入（路径+总行数+前 50 行），不短路，靠 read_file 分页定向。
> 2. **工具结果层 read_file 压缩**：① 短路——读取目标 == X1 已全文注入文件 → 结果替换短提示"内容已注入，无需重复读取"（消双份，40KB→单份 93s/轮）；② 超 `ContextToolResultBytes`(25KB) → 头 6000 + 尾 2000 截断（完整靠 offset 分页续读）。短路仅发生在"注入=File.ReadAllText(同文件)"时，内容同源，edit_file old_string 匹配不受影响。
> 3. **输出引导**：write_file 覆盖已存在 >50KB 文件 → 工具结果附警告"建议 edit_file 局部修改"（30B 生成 50KB 新文件 = 数小时级，edit_file 只输出改几行）。
> 4. **预算日志**：首轮任务注入量 `[上下文预算] 任务注入 N 字符（≈N KB）`；短路/截断命中点 LogWarning（可观测，复测直接可见）。
> 5. **tests**：`ReadPathMatches` 短路纯函数 +5（绝对命中/相对不命中/无 path/大小写/不同文件），**742 全绿**。
> **验证**：构建 0 错误；tests 742 全绿。端到端复测预期：任务第二轮上下文 = 单份 40KB（X1 注入），read_file 被短路，模型直接基于注入内容 edit_file，全程 3-5 轮分钟级（远低于 1200s）。
> **边界**：>40KB 文件走骨架注入 + read_file 截断，模型需 offset 分页（慢但受控）；完成门兜底自创/写错位置。

> **10-12 X1 内容注入脱离工程守护（B2+：轻量通道下模型不再失去目标文件引导）**

> **现象**（23:41 复测）：`use --write 读取 x:\ 盘下 index.html 文件…` → B2 轻量通道（不挂工程，projectBlock=null）→ 模型第一轮 `list_dir X:\`（越界）→ `dir` → 自创页面写 run 目录 → **完成门正确拦截**（`任务要求写入 x:\index.html，但本轮未写到此目标` ✓ 完成门真实生效）。但模型**没读真实 x:\index.html**——而 23:17 复测第一步 read_file 直接命中。同为 B2 通道，行为随机。
> **根因**：X1 内容注入整块被 `projectBlock is not null` 守护——B2 轻量通道 projectBlock=null → **注入整体跳过** → 模型拿不到"目标文件已定位 + 真实内容"，只能靠自觉（碰巧 read_file / 或 list_dir 探路）。同一通道两次行为不同 = 无引导的随机行为。
> **修复（数据结构性）**：X1 分支（`TryResolveAbsolutePath` 存在性命中 + `TryLoadFileContentByPath` 读内容）**脱离 projectBlock 守护**——绝对路径命中即注入"目标文件绝对路径 + 全文"，无工程也注入；工程内文件检索（FindExistingProjectFileRef）与位置提示分支仍限工程模式。安全不变：`TryLoadFileContentByPath` 内敏感区 `SensitivePathGuard.IsDenied` 源头拒 + 200KB 上限降级位置提示（内容不会泄露敏感文件）。
> **验证**：构建 0 错误；tests **737 全绿**（逻辑改动不破坏现有断言）。端到端效果需真实任务复测（用户决定）——预期模型第一轮即 read_file 真实文件（内容已在上下文）。
> **边界**：模型拿到全文后仍可能不照做（随机性无法完全消除），但注入把"定位+内容"变成事实而非探索，失败面大幅收窄；完成门兜底拦截自创/写错位置。

> **10-12 use 一步到位精细化 B2（文件目标轻量通道：不挂整盘工程根）**

> **现象**（23:01 复测）：`use --write 读取 x:\ 盘下 index.html 文件，并修复它的语法错误` → 自动提取盘根 `x:\` 挂工程（**2000 文件 / 11871 目录**）→ ProjectScanner CompactBlock 工程上下文巨大 → large 每轮 prefill **~290 秒**（1 步 list_dir 花 290s、2 步 read_file 后模型吃下一轮又 ~290s）→ 观感"卡在读取文件"。实际 A/B 定位全生效（写目标干净 + read_file 读到真实 RAG 页面），瓶颈是工程根选错：修单文件不需要把整个盘挂成工程。
> **修复（B2 精细化）**：use 参数非路径形态（TrySplitPathTask 断链）时按目标类型分流——① TryResolveAbsolutePath 命中【文件】→ 不 use（保持沙箱/当前模式，显式写目标白名单覆盖写、沙箱显式读例外覆盖读，上下文干净每轮秒级）；② TryResolveAbsoluteDirectory 命中【目录】→ use 该目录（精确工程根，非盘根）；③ 仅盘根 → use 盘根兜底。ProjectPathDetector 重构：候选提取 + 最长存在前缀剥段逻辑提为私有 Resolve()，TryResolveAbsolutePath/TryResolveAbsoluteDirectory 对称共用（消双通道漂移）。
> **验证**：构建 0 错误；tests **737 全绿**（735→737：+目录解析尾部剥段命中 1 + 无盘符 false 1）。端到端效果需真实任务复测（用户决定）——预期 `use --write 读取 x:\index.html…` 不再挂工程、直接轻量读修。
> **边界**：仅盘根兜底仍可能挂大工程（用户任务里只给盘根时不可避免）；文件目标保持当前模式，若当前沙箱则写靠显式白名单、读靠显式例外。

> **10-12 读改混合定位修复 A+B 第二轮（TryResolveAbsolutePath 空格化 + use 一步到位盘根提取）**

> **现象**（22:54 复测）：① 写目标已干净（`x:\index.html` ✓ 第一轮 A 生效），但 `read_file x:/index.html` 仍"路径越界"——B 未注入：TryResolveAbsolutePath 候选正则 `[^\s...]` 排除空格 → 空格分隔形态截断在 `x:\`（盘根是目录，File.Exists=false）→ 不命中；模型随后自创页面写 run 目录。② `use --write 读取 x:\ 盘下 index.html 文件…` 报"盘符不一致"——use 后第一位是任务动词"读取"，TrySplitPathTask 在 `x:\` 断链 → 整段被当路径 → ApplyUse 盘符防御误报。
> **根因**：写目标通道（TryGetWriteTarget 整段+清洗）与读注入通道（TryResolveAbsolutePath 正则）未共享同一空格形态归一——修了写、漏了读；use 语法假设路径在参数首位，无法表达"--write + 任务文本一步到位"。
> **修复**：A=TryResolveAbsolutePath 候选正则去 `\s` 排除（截断点=中文标点/行尾）→ 整段提取后 CleanPathSuffix 清洗；File.Exists=false 时逐段剥尾部空格段（最长存在前缀循环）——含空格路径（`D:\My Projects\f.txt`）与尾部自然语言（`f.txt 的内容`）顺带支持，中间空格保留。B=UseCommandParser.TryExtractDiskRoot（首盘根 + 存在性校验）+ Program.cs use 分支：TrySplitPathTask 断链且参数非有效目录 → 提取盘根 ApplyUse(writeFlag) + 整段作 pendingTask；无盘根 → 命令层提示"路径无效"。原"路径开头"use 形态行为不变。
> **验证**：构建 0 错误；tests **735 全绿**（730→735：+绝对路径空格方位词/含空格路径/尾部剥段 3 + use 盘根提取命中/无盘根 false 2）。端到端效果需真实任务复测（用户决定）。
> **边界**：use 一步到位提取盘根时任务文本原样执行（工程根=盘根，范围大于精确文件）；路径打错回退盘根的既有边界不变。

> **10-12 读改混合定位修复 A+B（空格形态清洗 + 沙箱显式指向读例外）**

> **现象**（22:45 复测，沙箱模式未 use）：输入"读取 x:\ 盘下 index.html 文件，并修复它的语法错误"（**空格分隔**）→ ① 写目标变脏 `[显式授权] x:\  index.html`（"盘下"Replace 后留双空格，脏路径进白名单）；② read_file X:/index.html **路径越界**——沙箱读严格，模型读不到目标 → 自创页面写 run 目录 → 完成门正确判失败。
> **根因**：① 空格分隔形态下 CleanPathSuffix 只剥"盘下"词本身，残留双空格，`x:\  index.html` 以"含空格形似文件"判为脏目标；② 沙箱模式（projectRoot null）读仅限 run 目录——"沙箱严格"与"给路径就干活"（输入显式给出 x:\index.html = 用户明确指向）矛盾。
> **修复（数据结构性）**：A=CleanPathSuffix 三步升级——① 盘根方位词正则归一（`^([A-Za-z]:\)\s*(根目录下|目录下|盘下)\s*` → `$1`）；② 剥尾部类别词；③ 方位词 Replace 改正则 `\s*(根目录下|目录下|盘下)\s*`（连相邻空白一起规整，子目录形态"\盘下 target.html"→"\target.html"）+ TrimEnd。B=ReadFileTool 加任务级 `SetExplicitReadTarget`（AgentLoop 任务开头 TryResolveAbsolutePath(input) 命中即注入；串行安全），沙箱越界检查放行"输入显式指向的绝对路径文件"——黑名单仍源头拒、写仍严格白名单。语义：读=黑名单排除，但额外要求路径出现在用户输入里（比全放开保守）；用户输入里写出来的路径即授权（显式优于隐式）。
> **验证**：构建 0 错误；tests **730 全绿**（726→730：+CleanPathSuffix 空格形态 2 + TryGetWriteTarget 空格形态 1 + ReadFileTool 沙箱未指向越界/指向放行 2；真实临时文件 File.Exists 命中）。端到端效果需真实任务复测（用户决定）。
> **边界**：沙箱下未显式给出的 run 目录外文件仍越界（隔离语义保持）；"盘下"作真实目录名（极少见）会被剥除，接受；输入无盘符路径走原相对文件名逻辑。

> **10-12 读改混合定位强化（X1 绝对路径优先注入 + X2 方位词清洗）**

> **现象**：复测"读取x:\盘下index.html文件，并修复它的语法错误"（工程=盘根 2000 文件/1380MB）——注入已给相对路径 index.html 全文，模型仍第 1 步 list_dir 盘根摸底（1分44秒 + 巨大上下文）。且日志 `[显式授权] 检测到写目标 x:\盘下index.html文件`——"盘下"被当路径成分（脏路径）。
> **根因**：① 注入只给相对文件名，模型无绝对路径锚点 → 习惯性探索；② TryGetWriteTarget 通道1 `x:\盘下index.html文件` 无空格 → LooksLikeFile（含 .）→ 脏路径直接命中。
> **修复（数据结构性，非提示词）**：X1=AgentLoop 注入块加绝对路径优先分支——`TryResolveAbsolutePath(input)`（盘符正则提取 → CleanPathSuffix 清洗 → File.Exists 校验）命中 → 注入**绝对路径** + 全文（模型直接锚定，无需 list_dir）；未命中回退 FindExistingProjectFileRef 相对名逻辑（行为不变）。X2=ProjectPathDetector 新增 `CleanPathSuffix`（① 剥尾部类别词"文件/目录/文件夹"，② 剥任意位置方位词"盘下/根目录下/目录下"——不剥独立"下"，防误伤"X:\下载"），TryGetWriteTarget 通道1 候选先清洗再判存在/形态（"盘下"误判修复）。
> **验证**：构建 0 错误；tests **726 全绿**（719→726：+CleanPathSuffix 4 + TryResolveAbsolutePath 2 + TryGetWriteTarget X2 1；真实临时文件 File.Exists 命中）。端到端效果需真实任务复测（用户决定）。
> **边界**：输入无盘符路径 → 走原相对文件名逻辑；目录名以"文件/目录/文件夹"结尾的真实路径会被剥尾（少见，接受）；子目录后紧跟方位词无分隔符形态（`...MyApp盘下x`）少见且剥后需补 `\`，本版不处理。

> **10-12 读改混合再修复（双正则漂移 + 交互态 IME 丢字）**

> **现象 2 连击**：复测"index.html 读取并修复"→ 模型仍大海捞针翻无关目录（list_dir 安装包 → read_file Strata README）；再测"use --write X:\index.html 读取这个文件修复里面的语法错误"→ 路由 SimpleQA → small，模型 0 步回复"未明确任务"。
> **根因 1（双正则漂移）**：方案 B 内容注入以 `ExtractTargetFile` 为入口，其 `TargetFileRe` 扩展名表缺 **html** → `index.html` 提取返回 null → 注入块整体跳过；而存在性校验 `FindExistingProjectFileRef`（`TaskClassifier.FileReferencePattern`，宽正则）能命中——两正则不一致。修复：① TargetFileRe 补 `html|htm`；② **注入块改以 FindExistingProjectFileRef 为权威驱动**（更宽正则 + 存在性校验），ExtractTargetFile 仅作新建任务位置提示兜底——消双路径漂移（tests +2：ExtractTargetFile html/htm）。
> **根因 2（交互态 IME 丢字）**：交互态自实现 `ReadLineWithTabCompletion` 用 `Console.ReadKey(true)` 逐键读取，与中文 IME 组合/选字竞争 → 任务输入随机丢字（"读取这个文件修复里面的语法错误"→"读取这个修复里面的语法错"，turns id=669/673 实证；"文件"丢失 → CodeContext 不命中 → IsWriteTask/IsReadTask 双双失效 → SimpleQA → small）。管道输入（Console.ReadLine）3 次完整、交互态中文输入 2 次损坏——输入管线差异即根因。修复：交互态读行退化为 `Console.ReadLine()`（原生行编辑，IME 正确）；Tab 补全/↑↓历史随之降级（IME 正确性优先，防打地鼠）；web 端输入不受影响（CommandHub 无 ReadKey）。
> **验证**：构建 0 错误；tests **719 全绿**（717→719：+2 html/htm 提取）；管道复测与用户 22:12 相同输入，turns 完整 + 路由读改混合 large + 内容注入生效（task_events 实证）；交互态中文输入需用户在真实终端手动验证（自动化难覆盖 IME）。

> **10-12 读改混合任务跑偏修复（A+B：L0.5 排除混合 + 目标文件内容注入）**

> **现象**：`use --write X:\` 巨型盘根工程（2000 文件/11001 目录）下，任务"index.html 读取这个文件，并对这个文件进行修复"跑偏——模型未读 index.html、自创无关 StrataDemo 项目（README.md/Program.cs 写成功，两次写 .csproj 被项目文件守卫拒 → 熔断失败回滚）。
> **两层根因（已实证）**：① 路由误判——`IntentOverride` 写动词表含"修复"→ 整个任务判纯写任务 → L0.5 数据闭环降级 small（小模型不读目标文件）；② 上下文缺失——X:\ CompactBlock 目录树被 MaxTreeLines 截断，index.html 未注入上下文，模型"不知道文件存在"（与 B 符号定义注入教训同源：位置指引对大模型无效）。
> **修复（数据驱动，非提示词）**：
> **A（ModelRouter L0.5）**：读改混合任务（`IsWriteTask && IsReadTask` 同时命中）→ 保持 large 不降级，路由决策落库"读改混合任务保持 large（需先读已有文件再修改）"；仅纯新建写任务参与数据闭环降级。
> **B（AgentLoop 目标文件注入升级）**：写任务引用工程内已存在文件（`ReadOnlyGuard.FindExistingProjectFileRef` 命中）→ 新增 `TryLoadFileContent`（public static，可测）预读全文注入"任务引用文件 {name} 已存在于工程根，当前内容如下——请基于真实内容读取/修复，无需再探索定位"；上限 200KB（超大 → 位置提示兜底）、SensitivePathGuard.IsDenied（敏感区 → 位置提示）、异常一律 false 不阻断；文件不存在（新建任务）保持原位置提示。
> **词性修复**：ReadVerbs 移除"内容"/"结构"两个名词——"文件内容为 hello"中"内容"是写目标描述非读意图，误命中会把纯写任务判为"读改混合"（tests 716/1 失败实证）。
> **验证**：构建 0 错误；tests **717 全绿**（708→717：+6 读改混合判定 +3 TryLoadFileContent）；真实复测（X:\ + index.html 任务）task_events 实证——Routing #0 **serviceId=large / "读改混合任务保持 large"**（A 生效），工具序列 #1 list_dir → #3 **read_file index.html** → #4 拿到真实内容（B 生效，模型自主读目标文件，不再自创任务）。任务最终因 30B 推理慢 LLM 调用超时（HttpClient 1200s 任务级超时配置生效）未完成——已知性能边界，非 A+B 缺陷；模型未写任何文件，X:\index.html 未被改动。

> **10-11 复盘④ 可疑成功归因验证（装饰器转换层补测 + 首个真实归因样本）**
> **审计**：8 条真实可疑成功（is_suspicious=1 ∧ success=1 ∧ is_e2e=0）error_summary **全部为空**——10-10 归因落库（AgentLoopMetricsDecorator 从事件文本提取 [检测]/[边缘成功]/未真正实现 → suspiciousReason 写 ErrorSummary）自实现后无任何真实样本产生过，旧样本未回填，链路处于"写完未验证"状态。
> **修复/验证（两层）**：
> ① 确定性测试——tests 新增装饰器转换层 8 断言（699→707）：stub 固定事件流（ToolResultEvent 含 [检测]模板 → ErrorSummary="输出疑似模板"；[边缘成功] → "边缘成功（失败信号+成功证据）"；Error 未真正实现 → 含真实原因；干净流 → is_suspicious=0 且 summary 空）。此前 tests 只覆盖 metrics 读写层，事件→归因转换层无覆盖。
> ② 真实样本——LAW_E2E=0 显式覆盖（计入真实统计）跑"写一个Python 计算器方法"，任务成功且触发边缘成功信号，SQL 实证最新样本 **error_summary='边缘成功（失败信号+成功证据）'**，归因端到端打通。
> **验证**：构建 0 错误；tests 707 全绿；真实样本归因落库。

> **10-11 复盘③ dotnet 多 csproj 目录歧义（run_command 层确定性预检）**
> **现象**：多文件写任务模型在同一目录写 ≥2 个 .csproj → `dotnet run` 报 MSB1011（包含多个项目文件，无法确定启动项目）→ 任务失败（10-11 学生系统 FAIL/other 实证）。
> **修复（数据驱动，非提示词）**：RunCommandTool 新增纯函数 `CheckDotnetRunAmbiguity(command, cwd)`——仅拦截 `dotnet run` 且未显式指定 `--project/-p/.csproj` 且 cwd 顶层 .csproj ≥2 的形态，进程执行前返回错误消息附目录内实际项目清单 + 正确用法（`dotnet run --project <文件名>`），退出码 125。与 CheckProjectName（幻觉项目名）互补：一个管"引用了不存在的"，一个管"没引用但存在多个"。
> **验证**：构建 0 错误；tests 693→**699**（+6 断言：多项目拦截/清单含项目名/--project 放行/显式 .csproj 放行/非 dotnet 放行/单项目放行）；E2E 冒烟（奇数偶数 + 分析项目）exit 0。**顺带修正验证命令缺陷**：旧 `cmd /c "... & echo EXIT=%ERRORLEVEL%"` 的 %ERRORLEVEL% 在整行解析时提前展开 → EXIT 恒 0 不可信（10-11 曾掩盖一次真实构建失败）；改 PowerShell `$LASTEXITCODE` 校验。

> **10-11 复盘② agent_pro P2 调权错配修复（routing_tier 数据错配）**
> **审计发现**：P2（ComplexReason → agent_pro 降级判定）查询 `GetTuningStatsAsync(AgentPro, "SmallModel", ...)`——但 agent_pro 任务的 routing_tier 实际是 **Rule**（L1 规则 complex_local 命中）/ OrchestratorPlanner（编排子调用），不存在 SmallModel → 永远 0 样本 → "样本不足保持 agent_pro"，**P2 从未真正评估过 agent_pro 能力**。L0.5/L0.5b 调权查 Rule 正确，仅 P2 一处错。
> **修复（数据结构性）**：ModelRouter P2 + stats 面板（Program.cs）查询 tier 统一改 Rule。30B 时代 agent_pro+Rule 实为 19 条 / 68%（≥ 50% 阈值）→ 保持 agent_pro，但样本从 0 → 19，调权数据真正生效（若未来低于阈值将自动降级 large）。
> **验证**：构建 0 错误；tests 693 全绿；SQL 实证 agent_pro+Rule 30B 时代 19/13=68%。

> **10-11 复盘① write_unverified 归因增强（数据闭环：让失败可归因）**
> **审计结论**：write_unverified（写文件但 run_command 未通过 → 熔断）是近 30 条最大失败面（10/30），但 13 条历史样本的 error_summary **全是同一模板**——熔断消息吞掉了 run_command 真实失败原因（超时/编译错/退出码/没跑无法区分）；task_events 在 Dashboard 未激活期不落库，历史事件流不可查。
> **修复**：AgentLoop 记录最后一次 run_command 的工具结果摘要（_lastRunCommandSummary，SummarizeOutput 160/80），熔断失败消息经纯函数 BuildWriteUnverifiedReason(summary) 拼接真实原因（summary 空 → 兼容旧消息）。此后 write_unverified 的 error_summary 可直接按"超时/编译错/退出码"归因。
> **验证**：构建 0 错误；tests 693 全绿（+4 断言：空摘要兼容 / 带摘要含原因）。

> **10-11 glm-4.7-flash:q4_K_M 对比测试 → 回退 law-unified（模型选型结论）**
> 用户要求对比 glm-4.7-flash:q4_K_M（Ollama 本地 19GB q4_K_M）vs law-unified。三类代表任务 E2E 实测**全线失败**：
> 1. 单文件写（C# 奇数偶数）：`HTTP 500: incomplete GLM tool call: XML syntax error: <arg_value> closed by </tool_call>`——glm 本地量化的工具调用 XML 格式不稳（arg_value 未闭合等），Ollama 严格解析直接 500；升级 large 重试同样失败。
> 2. 多文件编排（学生管理系统）：Planner 输入截断（4512→500）后 glm 生成计划 JSON 不完整 → 解析失败 ×2 → 规划失败。
> 3. 工程分析：模型连续空响应，升级链兜底也空。
> **结论**：glm-4.7 本地 q4 量化版工具调用层不兼容（模型端产出非法 XML，非内核缺陷），不推荐。19GB 与 30B 同量级无体积/速度优势。
> **动作**：配置回退 law-unified（Small/AgentPro/Large），构建 0 错误，冒烟 E2E 恢复（奇数偶数写任务 OK + 分析任务收敛 OK）。三角色温度分级保持（0.3/0.5/0.7）。切换历史：law-unified → qwen3.8-27b（10-10）→ glm-4.7-flash:q4_K_M（10-11 失败回退）→ law-unified。

> **10-10 可疑成功治理（① 开展）**
> **目标**：让"可疑成功"计数反映真实（此前 stats 显示 ~20，真实口径仅 8-10）。
> **数据审计结论**：
> 1. **E2E 漏标是主因**——`is_e2e` 仅由环境变量 `LAW_E2E=1` 标记，E2E 复测（`type e2e_input.txt | dotnet run`）一旦忘记设该变量，E2E 用例即混入真实统计。已实证：`_law_e2e_test.txt`、`pyst.py hello world` 等 10 条 E2E 历史用例 is_e2e=0。回填修正 10 条后，真实可疑成功 = **8 条**。
> 2. **suspicious 无归因字段**——可疑成功任务 error_summary 恒空（装饰器只标 is_suspicious=1，不记录来源），无法区分 [边缘成功] / 输出疑似模板 / 未真正实现。
> **修复（结构性，非提示词）**：
> 1. **源头标记**：Program.cs Main 启动时，输入/输出任一重定向（无头管道=E2E 语义，与 RendererFactory 交互判定一致）且未显式设置过 LAW_E2E → 自动补标 `LAW_E2E=1`。不再依赖"记得设环境变量"；显式 `LAW_E2E=0` 可覆盖（真实自动化管道）。
> 2. **归因落库**：AgentLoopMetricsDecorator + OrchestratorMetricsDecorator 记录首个可疑信号类别（边缘成功/疑似模板/未真正实现/完成门目标不符）→ 成功任务也写 ErrorSummary。此后可疑成功可直接 SQL 按类别归因。
> 3. **历史回填**：`UPDATE task_metrics SET is_e2e=1 WHERE user_input LIKE '%_law_e2e%' OR '%hello world%' OR '%pyst.py%'`（10 条）。
> **验证**：构建 0 错误；tests 689 全绿；E2E 复测确认无头管道自动标 is_e2e=1 生效（本次 E2E 两条任务 e2e=1）。
> **E2E 环境观察（非代码缺陷，记录）**：本次 E2E 两条任务执行失败——`HTTP 500 (api_error: llama)` 与 30 分钟任务超时取消。根因：`qwen3.8-27b:latest`（12GB）在 Ollama 中常驻占用（`ollama ps` 可见，67%/33% CPU/GPU），与 E2E 使用的 law-unified（17GB）竞争 6GB 显存，law-unified 加载期服务报 500。属环境层资源竞争，非本次改动引入。

> **10-10 两个新问题修复（熔断假完成 + run_command 目录基准）**
> 1. **问题1 熔断失败包装**：读任务大文件 read 被拒（`错误：文件过大...`）返回 ok=true → 进入熔断签名检测 → "工具已验证（read_file）。最后验证：错误原文"假完成（完成文本即错误原文）。修复：新增纯函数 `AgentLoop.IsToolSemanticFailure(output)`（输出以"错误："前缀即业务级失败）；熔断收尾前先判——语义失败时读任务已收敛（IsReadQueryConverged）→ 放行模型总结，否则判 Error（诚实失败，不再包装成功）。tests 覆盖 4 条。
> 2. **问题2 run_command 目录基准**：只读工程模式下 write_file 基准=run 目录，而 run_command cwd=`_projectRoot ?? _workspaceRoot`=工程根 → 任务产物（run 目录）无法被 `dotnet run --project` 找到（E2E parity 实证"文件路径不存在"）。修复：新增静态纯函数 `RunCommandTool.ResolveWorkingDirectory(ws, projectRoot, projectWriteEnabled)`——可写工程 → 工程根（dotnet 构建工程），只读工程/沙箱 → run 目录（任务产物验证），与写工具基准一致；DependencyInjection L117 传入 projectWriteEnabled。tests 覆盖 4 条。
> 3. **顺带修正（E2E 暴露的确定性误伤）**：
>    a) 项目名幻觉校验原实现固定扫"工程根顶层"（TopDirectoryOnly）——本项目 csproj 全在 src/tests 子目录 → 空列表误拦一切 .csproj 引用。改为 `CheckProjectName(command, cwd)`：相对 cwd 存在放行 + 递归同名放行 + 均不存在才拦截（提示 cwd 实际顶层项目）。tests 4 条（相对存在/递归同名/幻觉拦截/无引用）。
>    b) 401/403 外部凭据信号裸 `401` 误伤 dotnet SDK 版本号（`10.0.401`）——每次构建输出含版本路径都会误判"需要 API Key"。收紧为 `(?<![\d.])401(?![\d.])`（排除前后紧邻数字/点的版本号/小数/长数字，保留 HTTP 状态码语义）。tests 6 条。
>    c) tests 环境 flaky 修复：记忆治理测试 FindGovDb 用"排序最大"枚举 TestTmp（pid 复用残留旧 db → 断言错位），改为直接用 gov 实例准确路径。
> E2E 复测：问题2 校验放行生效（run 目录真实项目不再误拦，首次 dotnet run 退出码 0）；任务 2 最终仍失败——失败面已转移到"模型在同目录写 2 个 csproj 致 dotnet run 目录歧义"（30B 生成质量边界，记录待后续）+ 工程感知路径（模型按工程路径写入 run 目录同名结构）——非本轮两个修复的失败。
> tests 671→689（+18：IsToolSemanticFailure 4 + ResolveWorkingDirectory 4 + CheckProjectName 4 + 401/403 收紧 6）。提交后 HEAD 见 git log。

> **10-10 方案4 读任务收敛判定 + empty_response 归因 + 注释清理**
> 1. **方案 4 收敛判定**：ReadQuery 达到步数上限时，若最后一步是纯文本总结（无工具调用）且长度 ≥30 → 判成功而非"最大步数终止"（读任务交付物即总结文本本身）。实现：AgentLoop 记录 `lastModelText` / `lastStepHadToolCall`（native 调用与伪调用分支均标记）；纯函数 `AgentLoop.IsReadQueryConverged(bool, string?, int minLen=30)` 可测。tests 665→671（+6）。防御性兜底：三件套后读任务普遍提前收敛，该分支实际触发少；E2E 验证无回归（读任务 13 步、写任务既有行为不变）。
> 2. **empty_response 14 条归因（数据驱动结论：无需新改动）**：11 条 = 9-22~10-01 4B 时代天气/IP 系列（历史边界，4B 已废弃）；1 条 = 10-03 BOM 乱码输入（`Console.InputEncoding=UTF8` 编码加固已闭合，10-03 后无复发）；2 条 = 10-08 large 偶发空响应（10-09 重跑均成功，B2 空响应升级链已兜底）。当前 30B 时代 empty_response 非活跃失败面。
> 3. **配置注释清理**：Program.cs L51-55 注释由 iq1_m/8080 残留改为与 law-unified@11434 一致（strata 切换历史注明指向 HANDOVER 10-09 段）；健康检查双协议探活代码保留（支持未来复启用 strata）。
> E2E 新观察（记录，不在本轮处理）：① 读任务熔断"以工具验证收尾"在最后一步 read_file 大文件失败时，完成文本即错误原文（可疑成功形态之一）；② 写任务 csproj 命名错误（Program.csproj vs parity.csproj）致 `dotnet run --project` 路径失败（30B 生成质量边界）。

> **10-10 项目复盘（全面）**
> 数据口径：metrics.db task_metrics，is_e2e=0 真实统计（截至 10-09 17:44 + 当日 E2E 验证）。
> - **总体**：443 任务 / 383 成功 / 60 失败（86.5%）；平均 84.9s / 修复 0.18 次；可疑成功 28。
> - **失败类别**：empty_response 14 / max_steps 11 / write_unverified 10 / write_not_attempted 5 / other 5 / timeout 4 / target_mismatch 4 / not_found 3 / compile_error 2 / runtime_error 1 / llm_error 1。
> - **服务**：small 240·92.9%·53.3s（主力）｜agent_pro 66·71.2%·105.6s｜large 57·82.5%·107.2s｜orchestrator 80·82.5%·146.9s｜orchestrator_sub 153·100%。
> - **配置现状**：Small/AgentPro/Large = law-unified@11434（Ollama 30B Q4）——strata q2_0→iq1_m 实验已回退（随 970b67c 一并提交，工作区 clean）；iq1_m 正确性持平、速度未提升，停用合理。
> - **数据地基**：routing_decisions 141（small 106 / large 19 / agent_pro 16）；tool_rejections 25（pd_conflict 9 / out_of_bounds 7 / empty_content 2 / interactive 2 / project_file 2 / read_only 2 / drift 1）。
> - **待办优先级**：① 方案 4 读任务收敛判定（40 步已产出总结→判成功，三件套最后闭环）；② empty_response 14 条分布归因（SQL 数据驱动）；③ 配置注释残留清理（Program.cs L55 8080/11434 混合字样）；④ 可疑成功 28 治理（验证器容错克制）。

> **10-09 读任务三件套（"分析项目"必败修复）**
> 背景：`use <工程> + 分析一下这个项目` 实测 20 步/359s 截断失败——模型发散探索（4 list_dir + 16 read_file）、3 次猜错路径（AgentLoop.cs / ProjectAnalyzer.cs / TaskEvent.cs 不存在且无候选提示）、30B CPU 每步 ~18s、max_steps=20 一刀切。
> 三个工程化修复（数据驱动，非提示词；方案 1→2→3 确认后实施）：
> 1. **read_file 失败附候选路径**：文件不存在 + 工程模式 → `ProjectScanner.FindFileCandidates`（精确同名→全列；扩展名相同 + 名字公共前缀 ≥4 模糊匹配，TaskEvent.cs→TaskEventHub.cs）→ 拦截消息附"工程中相近文件（候选路径）"，模型不再继续猜；沙箱模式无工程索引保持原样。数据源：`ProjectScanner.ListFilesWithSize`（复用 Walk 排除规则）。
> 2. **分析类任务注入核心文件清单**：命中 `TaskFeatureClassifier.ReadQuery`（确定性词表，含"分析/解释/总结"前缀）→ 注入 `ProjectScanner.CoreFileLines`（入口 + sln/slnx + 依赖清单 + 按体积 Top8 源码文件，均为真实相对路径）——模型 5-8 步读完核心文件即可总结，不再逐文件发散。
> 3. **读任务步数分级**：`AgentOptions.ReadQueryMaxSteps=40`（默认），AgentLoop 循环前按 ReadQuery 特征类取 `max(MaxSteps, ReadQueryMaxSteps)`（11 处引用统一替换 effectiveMaxSteps）；写任务保持 MaxSteps=20 防空转。
> 验证：构建 0 错误；tests 648→665（+17：分类词表回归 5 / 配置 2 / 候选匹配 4 / 核心清单 4 / read_file 模式分支 2）；E2E 复测同款任务 **12 步自主收敛、0 猜错路径**（对比失败日志 20 步截断/3 次猜错），总结质量完整（结构/特性/技术栈正确）。改动文件：AgentOptions.cs、ProjectScanner.cs、ReadFileTool.cs、AgentLoop.cs。



> **10-09 追加：模型升级为 strata qwen3.8-flash-next-coder-iq1_m（用户提供 strata "Model and engine" 面板截图：Engine v0.1.41 / Context 32768 / KV cache 4-bit Hadamard-rotated all in VRAM / Experts in VRAM 126 (0.2GB) / Speculation MTP <=3 tokens + prompt lookup on / Images off）。strata 服务端已切到 iq1_m（/v1/models 实测 loaded, ctx=32768，q2_0 已不在列表），workbench 仅改 Program.cs 配置区三处 Model = "qwen3.8-flash-next-coder-iq1_m"（Endpoint 8080 / Provider ollama / Temperature 角色语义 0.3/0.5/0.7 均不动；KV/专家/推测为服务端配置，workbench 不透视）。**踩坑记录**：PowerShell `-replace` 对含数字的模型名会踩正则转义坑（替换串 `iq1_m` 被处理成 `iql_m`、第三处还拼出 `qwqwen` 前缀）——**必须用 Edit 工具 replace_all 精确替换，禁用命令行 -replace**（与 10-09 早轮 q2_0 切换结论一致）。修复后 Grep 确认 4 处（含注释）均为正确名。验证：构建 0 错误 + tests 648/648 + E2E 奇数偶数 188.4s 一次成功（q2_0 同款 169.2s——**速度未显著提升，正确性保持**，单样本不足以定论，1-bit 量化的 dequant 开销 + MoE 专家路由可能抵消 KV/推测收益；且写任务因新模型样本清零仍直连 large）。**当前运维注意**：本次启动 Ollama 11434 未在线（Classifier qwen2.5:1.5b FAIL 降级，strata 无此模型）——若 Ollama 长期停用需决定 Classifier 替代方案（strata 换小模型 / 停用意图分类，待用户决策）。
> **10-09 三角色切换 strata（qwen3.8-flash-next-q2_0，OpenAI 兼容端点）：** 用户提供 strata 部署配置（Base URL `http://127.0.0.1:8080/v1`、ApiKey/Model 服务端不校验），要求将承接 Small/AgentPro/Large 三角色的 30B `law-unified` 切换为 strata 上的 `qwen3.8-flash-next-q2_0`，跑真实任务对比"有无提升"。**改动**：① Program.cs 配置区 Small/AgentPro/Large 三角色 → Provider="ollama"（KernelFactory 拼 `{Endpoint}/v1` 正好匹配 strata 的 `/v1` 端点）、Model="qwen3.8-flash-next-q2_0"、Endpoint="http://127.0.0.1:8080"、Temperature 角色语义保留（0.3/0.5/0.7）；Classifier 不动（qwen2.5:1.5b@11434）。② CheckLocalModelsAsync **按端点分组探活**（关键：Classifier 在 11434/Ollama、三角色在 8080/strata，单端点探活会让跨端配置误报"未安装"）：每个唯一端点独立探测，模型集按端点映射；**双协议探活**（先 `GET {endpoint}/v1/models` 解析 data[].id，404/失败回退 `GET {endpoint}/api/tags`）；**自动启动仅限默认 Ollama 端口 11434**（strata 无 serve 子命令，连不上只提示不拉起）；在线/缺失提示按服务类型自适应（strata 提示"确认部署已包含"、Ollama 提示 `ollama pull`）。**对比实测（同款真实任务）**：奇数偶数 85.8s→169.2s 成功（速度 ~2 倍下降，正确性保持）；字母大小写 169.5s/343.5s 双失败 → 192.3s 一次成功（**正确性显著提升**）；Python 计算器（30B 曾 empty_response 失败）→ 269.7s 成功（含递归下降解析器，不用 eval）；图书管理系统多文件 orchestrator 父任务 536s（子调用 64-118s）成功。**结论**：q2_0 代码质量够用（曾失败项全部转成功、负数/边界/自检覆盖更全），但**速度未获提升反而下降**（q2_0 在 strata 无 ollama 的 prefix cache 优势；2-bit dequant 开销 + 写任务因新模型样本清零回 large）。**架构价值**：双端点健康检查 = 本地可混合部署（Ollama + strata 并存），KernelFactory 的 ollama provider 天然兼容任意 OpenAI 兼容本地服务。构建 0 错误 + tests 648/648；E2E 4 任务 4/4 通过。（回滚：git checkout a3d5605 恢复 law-unified 配置；strata 服务需用户侧保持运行）
> **10-08 use 一步到位双根因修复（盘符归一化 + 会话写目标指代继承）：** 用户实测两缺陷：① `use --write X:\ 在X盘根目录新建一个空的 test.txt 文件` 工程根落错——`Path.GetFullPath("X:")` 按 .NET 盘符相对语义解析为"X 盘上的当前目录"（cwd=bin\Debug），`Directory.Exists("X:")`=true 掩盖静默错位；② 第二轮"向这个文件写入…"指代断裂——模型写任务目录同名文件+记忆旧内容，type 验证成功即收尾，实际 `X:\test.txt` 为空（双重假完成）。修复（工程化、显式优于隐式）：**根因①** TrySplitPathTask/ApplyUse 双防线：解析到 `盘符:`（length==2）时补 `\` 归一到盘根；ApplyUse 盘符一致性校验（解析结果盘符与输入不一致 → 拒绝）；**根因②** 会话写目标延续 `sessionWriteTargets`（CLI 主循环，最近 3 条文件级目标、去重）——**仅指代词命中时继承**（`HasReferentialPronoun` 词表：这个文件/该文件/刚建的文件/上述文件/刚才那个文件/这个脚本/该脚本/这个程序/刚才写的…），白名单延续 + 上下文注入（`（本次任务显式写目标：…）`）+ 达成门三处同一开关；new/use 清空、任务失败保留；继承前过 SensitivePathGuard.IsDenied。**完成门 B 语义修正**：从 `File.Exists(target)`（历史存在即放行，假完成漏洞）改为"**target ∈ 本轮 writtenFiles**"（AgentLoop 记录归一化绝对路径，baseRoot 与 WriteFileTool 对称）；两轮修掉自身 bug（`!exists || writtenFiles is not null` 写成"有记录必失败"）。**扩展内容提取** `ExtractRequirementTokensExtended`：`内容：xxx` 冒号 / `写入一句 xxx` 动词形态，仅 `_writeTarget != null` 时启用（普通生成任务不误伤）。**词表补全**：OperationVerbs 加"新建"（缺失致"新建文件"误判 SimpleQA → 纯文本幻觉完成零工具调用）。**健壮性**：ProjectScanner.Walk 对无权限目录（`X:\$RECYCLE.BIN`）try/catch 跳过 + ExcludedDirs 补 `$recycle.bin`/`system volume information`（否则 E2E 首次跑盘根整体崩溃）。**验证**：构建 0 错误 + tests **648/648**（+10：盘符归一化 / 指代正3负1 / 扩展提取三形态 / 完成门 B 三态 / 新建词补全）；E2E 两轮（`use --write X:\ 新建 _law_e2e_test.txt` + `向这个文件写入 内容：hello-e2e-20261008`）全通：工程根=X:\、指代继承命中、目标文件存在且内容=hello-e2e-20261008、任务目录无残留、无拒绝。E2E 中模型曾写相对路径被 B 门诚实拦截+回滚（防假完成实证），也写对过 `X:/_law_e2e_test.txt` 正斜杠绝对（归一化正确放行）。
> **10-08 use 裸命令拦截 + dotnet 验证超时档位（A+B）：** 实证日志暴露两缺陷：① 用户输入裸 `use`（无路径）漏进任务流——CLI 判定 `StartsWith("use ")` 只认带空格，裸 use 被 agent_pro 误解为"继续上一任务"跑 8 步；② 沙箱模式 run_command 30s 超时，倒计时任务 `dotnet run` 冷编译（restore+build+run）在 30B 推理占 CPU 下 >30s 卡线、两次必败（非模型写不对，是验证通道超时）。修复（工程化，非提示词）：A. 新增 UseCommandParser（Infrastructure 公共解析器，CLI 与 tests 共用）：裸 use / use --write 缺路径命令层拦截打印用法提示，不进任务流；`^use(\s|$)` 锚定不误伤 "useful"/中文"使用"。B. RunCommandTool.ResolveTimeoutSeconds：工程模式 90s 不变；沙箱模式仅 dotnet 验证命令放宽 90s（对齐工程模式），其余命令保持 30s 快速失败（不拖慢交互/死循环反馈）。构建 0 错误 + tests 641/641（新增：use 解析 3 连 + 超时档位 3 连）。E2E 复测：裸 use → 提示 + 无任务；倒计时任务 → [完成] 且无超时。
> **10-08 Monaco 编辑器引入（本地 vendor，选择性引入）：** 事件流代码块点击升级为 Monaco 只读编辑器（语法高亮/行号/折叠，```lang 进 data-lang，无语言标记保持纯文本 pre）；diff 查看器替换为 Monaco StandaloneDiffEditor（左右对比 + 文本视图回退按钮）。关键决策：不引入任何 NuGet 包、不换前端框架——Monaco 全量 min/vs（25MB/107 文件）作为本地静态资源 vendor 在 src\Agent.Dashboard\vendor\monaco，csproj Content 构建复制到 bin\vendor（ProjectReference 传递复制，CLI/tests 输出均含），DashboardHost UseStaticFiles 挂 /vendor（置于鉴权中间件前：Monaco loader 内部 fetch 不带 token，vendor 是公开编辑器代码无鉴权必要）；index.html 按需 loader.js + require.config 懒加载（不影响首屏）。/api/diff 增加 original/modified 全文（读备份+当前文件，非 tooLarge 时；修正：backup.Value.Full 是路径非内容，须 ReadAllText）。前端 Monaco 失败自动回退红绿行文本视图。构建 0 错误 + tests 638/638（新增：页面标记 / vendor 源树存在 / GET /vendor 200 / /api/diff 三字段）。
> **10-08 任务完成后无提示符修复（prompt 统一主循环）：** web 提交任务完成后终端不显示新 `> `，看似无法开展下一次任务。根因：交互态 prompt 由后台线程在 ReadLine 前打印，任务执行期间后台线程阻塞在 ReadLine（web 场景用户不碰终端）→ 任务完成后无人补 prompt（终端输入场景 ReadLine 返回后下一轮会打，故此前无感）。修复：prompt 统一由主循环打印（空闲等待 NextAsync 前）——任务完成后必然出现新 `> `；后台线程不再自打 prompt（终端输入回显由控制台层负责，ReadLine 会回显键入字符；ReadLineWithTabCompletion 内部本就不打 prompt，Enter 仅 WriteLine 结束行、Tab 分支重绘——无双 prompt 风险）。无头路径不走 hub 保持同步读-执行，E2E 逐字节兼容。构建 0 错误 + tests 632/632。
> **10-08 Web 提交任务 CLI 无回显修复：** web POST /api/commands 提交的任务在 CLI 端不显示任务内容（终端输入由 ReadLine 天然回显；web 注入走 CommandHub 无回显，直接跳 [工作目录] 开跑）。修复：QueuedCommand 增加 Source 字段（terminal/web，TrySubmit 可选参数向后兼容）；DashboardHost 提交标记 "web"；CLI 主循环消费时 if Source==web 补 Console.WriteLine(input)（提示符 `> ` 已由后台线程打印，连成 `> {任务内容}`，与交互态视觉一致）。terminal 来源不补打（ReadLine 已回显，防双重显示）。无头路径不走 hub，E2E 逐字节兼容不变。构建 0 错误 + tests 632/632。
> **10-10 Web 页「加载中…」卡死修复（前端 JS SyntaxError）：** 上一轮改造 showTaskEvents 时替换边界未对齐，残留多余顶层 }，整个 <script> 块 SyntaxError → 全部 JS 失效（SSE/状态更新/卡片加载全挂），顶部状态栏永远停留在静态文本「加载中…」，CLI 本体不受影响。修复：删除多余 }；用 node --check 提取 script 块做语法门（一次通过）；新增防回归断言（顶层三连闭合模式「});
}
}
// ── stats ──」不存在）。教训：手改大 HTML 时替换边界必须带上下文锚点，改完必须过 JS 语法检查（字符串断言抓不住语法错误）。构建 0 错误 + tests 630/630。
> **10-10 ②③④ Markdown 渲染 / 工具配对 / 折叠挂载：** ② finalAnswer 完成文本改轻量 Markdown 渲染（mdToHtml：标题/代码块/有序无序列表/粗体/行内码，转义先行防 XSS，不引入第三方库；截断 500 字符 + 展开全文 expandMd，dataset.full 存原文）。③ 工具调用配对（buildUnits）：同一节内 toolCall 与其后第一个 toolResultEvent 合成 toolpair 块（call 摘要 + [ok]/[fail] 结果区 + 展开），无配对边界兼容独立单行；过滤/关键词作用于块内行（call/res 独立判定，双隐藏才整块跳过）。④ 折叠挂载：卡片级（① 已有）+ 工具块级（tp-call 点击折叠/展开结果区，collapsedPairs 按 boxKey 隔离）；toggleCard/togglePair 统一走 rerenderBox（events 重渲 / detail 本地直画 lastDetailList）。expandEv 兼容无 .tag 容器。构建 0 错误 + tests 629/629。
> **10-10 ① 事件分组（平铺 → 卡片内分节，UI 地基）：** 事件流从平铺改为按任务节分组卡片。分组边界确定性切分（不靠模型）：routing 开新卡片；finalAnswer/error 结束卡片并入档（done/fail 标记）；其余事件归属当前卡片。节号 = 节首事件源数组 index（稳定）→ 折叠状态跨全量重建保留；折叠状态按 boxKey 隔离（实时流 events / 回放 detail 各自独立，防节号交叉污染——回放折叠本地直画 lastDetailList，不重复 fetch）。统一渲染 renderGrouped(box, events, opts) 实时流与回放共用（消双路径漂移）；renderEventLine 核心逻辑不动，仅外层包分组容器；完成/错误区前插 tc-sep 分隔线（行被过滤/关键词隐藏时不插线防残留）；过滤/关键词/自动滚动/展开/失败定位语义全部保留。CSS：taskcard 嵌套卡片 + 头行（路由摘要+折叠钮）+ 左缘状态色条。构建 0 错误 + tests 628/628。
> **10-10 命令条吸顶修复（P1 补课）：** 上轮 P1 声明了吸顶但未生效——cmdBar 埋在「任务实时流」卡片内部，sticky 的吸附范围是父容器（卡片）边界，滚过卡片即滚出视口，与「唯一操作入口随时可及」目标相悖。修复——cmdBar 移出卡片、置为 .colL 直属子级（滚动容器 body 的直接子级，sticky 相对视口吸附直到页面底部）；header 显式高度 --headH:42px，cmdBar top:var(--headH) 吸顶时紧贴 header 下方不重叠；样式改卡片式（背景/边框/圆角/阴影）。防回归断言：嵌入页结构 colL>cmdBar 直属 + sticky top:var(--headH)。构建 0 错误 + tests 627/627。
> **10-10 CLI 日志错乱治理（内嵌服务显式日志级别）：** 根因——DashboardHost 用 WebApplication.CreateBuilder() 无参重载吃脚手架默认 Logging（ConsoleProvider + Information 最低级），Kestrel/Hosting 启动 4~5 行 info 日志（Now listening/Application started/Hosting environment/Content root）与 CLI 交互渲染挤进同一个 stdout，且自动启动发生在主循环前污染首屏。修复——StartAsync 显式声明：ClearProviders() + AddConsole(simple) + SetMinimumLevel(LogLevel.Warning)（正常静默，Warn/Error 可排障）；CLI 自身日志（关注点 A，DependencyInjection 显式 Information）不动。验证——管道模式 dashboard 命令有交互守卫无法真启 Kestrel，改为永久断言：tests 冒烟块捕获 Console.Out（StartAsync + 请求窗口）断言无 Kestrel 日志。构建 0 错误 + tests 626/626。
> **10-10 P0 事件流渲染修复 + UI 收尾（5 项）：** P0——AgentEvent 是 C# record，序列化无 type 判别字段导致 SSE 实时事件缺 type、回放 type 为 .NET 类名，前端 renderEventLine 全失配（功能性阻断）。按源头隔离修复：后端双出口（SSE + /api/tasks/{id}/events）统一补 wire type 短名（thinking/toolCall/toolResultEvent/finalAnswer/error/routing/planGenerated/fileWritten，未知类名兜底原样），前端回放不再 JSON.parse(payload) 直接消费平铺事件；存储层 Type 保持类名不变（历史数据兼容）。P1——命令条 #cmdBar position:sticky 吸顶，任务执行中命令入口随时可及；折叠卡片标题带预览数字徽标（badge：统计 N·X% / 调权 稳态或触发项 / 记忆 N 条 / 历史 N 条 / 备份 N 条），页面打开预加载 5 接口并标记 loaded，展开不再重复请求。P2——cmdHint 移至命令条（紧跟取消按钮，溢出省略）；fetchBackups 刷新时 diff 区一行清理回引导态。tests 回放断言语义更新（Thinking 类名 → thinking 短名，保留测试意图）。构建 0 错误 + tests 625/625。
> **10-10 右栏信息卡片收纳（折叠式）：** 统计/路由调权/记忆/历史任务/任务详情回放/工程备份与 Diff 六个卡片改为可折叠（点击标题展开/收起，箭头 ▸/▾ 旋转指示）；首次展开才懒加载对应 API（stats/tuning/memory/tasks/backups），页面打开不再一次打满 6 个接口；刷新按钮仍全量刷新；diff 清空按钮 stopPropagation 避免误触折叠。构建 0 错误 + tests 625/625。
> **10-10 Dashboard 前端交互与美观优化（index.html）：** 布局重排——全宽卡片改双栏（左：实时流/产物/图谱，右：统计/调权/记忆/历史/回放/备份），信息密度提升、首屏不再被长卡片占满；实时流新增关键词过滤框 + 清空按钮，工具结果 [ok]/[fail] 分色 + "展开"看全文；命令输入支持 ↑/↓ 本地历史（上限 10）；全局 toast 反馈（提交/取消/回滚，4.2s 自动消失）；历史任务行选中态视觉（.sel 类）；文件预览加关闭按钮；diff 区加清空按钮；header 加在线状态点（SSE 心跳驱动）；视觉 polish——暗色 slim 滚动条、表格斑马纹、主次按钮（提交=主色）、卡片圆角阴影、按钮 hover 过渡。约束：所有 tests 页面断言标记（fbtn/autoScroll/layoutOneDegree/renderDepSvg/selectDepFile/depArrowG/DEP_NODE_MAX 等）保留。构建 0 错误 + tests 625/625。
> **10-10 CLI 自动启动 Web Dashboard（不用再手动敲 dashboard）：** 交互终端启动 CLI 即自动起 Kestrel + 自动开浏览器 + 打印 token URL——"运行即看"。关键设计：启动逻辑抽为局部函数 StartDashboardAsync，与 dashboard 命令共用同一份（消双路径漂移）；无头态（管道/重定向）由守卫天然跳过（E2E 输出逐字节兼容，管道复测验证不触发）；`LAW_NO_AUTODASH=1` 可关（默认开）；commandHub 声明上移主循环前（局部函数明确赋值约束）。dashboard 命令保留（幂等：已运行只提示）。
> **10-10 数据治理三层原则文档化：** 将本窗口踩过的两个数据污染坑（routing_decisions 无 is_e2e 列、task_metrics 三处查询漏过滤）提炼为常驻原则，落 DEVELOPER §7.1——**一句话铁律：新表/新查询上线前先问"测试/E2E 数据会不会进来、口径在哪一层定死"——能源头隔离就源头隔离，能下沉视图就下沉视图，绝不把"每个查询自己记得过滤"当约定。**三层手段按优先级：① 源头隔离（落库入口守卫，E2E 物理上不写）→ ② 视图下沉（口径进 SQL 视图，查询 FROM 视图）→ ③ 单点过滤（代码过滤收拢唯一入口）。附"新表 3 问"检查清单（标记列 / 无条件落库 / 重复 WHERE）。
> **10-10 Web 端回滚（补齐"看-改-回"闭环）：** 唯一硬不对称缺口——Web 能看 diff/备份/任务回放，但 rollback 只能 CLI 敲。新增两个端点（复用 CLI 同一套 BackupService，无并行实现）：`POST /api/rollback/dry-run`（DryRunRollbackTask 预览恢复/删除清单，不执行）+ `POST /api/rollback`（RollbackTaskFull 执行，返回 restored/removedNew）。前端备份区每个任务分组加"回滚"按钮，两步确认：dry-run 预览（改动用红删除色展示，明确"未执行任何改动"）→ 确认执行 → 刷新列表。有风险操作：预览后仍需点击"确认回滚"才生效；鉴权走全局 AuthToken 中间件；无工程根/空 taskId → 400。**tests 618→625**（+3 端点守卫：无工程根 dry-run/空 taskId/无工程根 rollback → 400；+4 真实闭环：临时工程造备份 → dry-run 清单含 Order.cs/TaxService.cs → 未执行改动 → rollback 恢复 1/删除 1 → 文件复原）。
> **10-10 路由决策表 E2E 源头隔离（审计+加固）：** 审计 routing_decisions 表（3B 调权数据地基）发现其与 task_metrics 同构的隐患——无 is_e2e 列、RecordAsync 无条件落库。实测污染面：决策表 96 条中 E2E 仅 2 条（2.1%），结构性原因：E2E 用例 215 条中 211 条走 orchestrator 编排（orchestrator 任务不经 ModelRouter，设计上不产生决策记录），仅 4 条 agent 模式。但缺"防未来"机制：一旦新增 agent 模式 E2E 用例，E2E 决策将不可区分地灌入表。**加固（源头隔离）**：ModelRouter 新增 public static IsE2ERun（读 LAW_E2E，与两个 MetricsDecorator 同款），RecordDecisionAsync 在 E2E 下直接跳过——E2E 物理上不落库，未来 3B 调权按特征类/服务分布不踩污染坑。tests 616→618（+2 判定开关行为断言）；构建 0 错误。
> **10-10 真实口径视图下沉（task_metrics_real）：** 系统性审计全部 metrics 查询发现失败类别之外还有 3 处漏 is_e2e 过滤：QueryStatsAsync（数据闭环调权地基——small/OrchestratorExecutor 含 E2E 252 条 92.8% vs 真实 119 条 100%）、GetEpochBreakdownAsync（时代隔离面板——small 混入 135 条 E2E）、BackfillFailedSubMetricsAsync（父失败回填会把 E2E 子记录改写为 validator_failed）。**下沉方案**：建 SQL 视图 `task_metrics_real`（`WHERE is_e2e = 0`），6 个真实口径查询统一 FROM 视图（Summary/Recent/RecentFailures/FailureBreakdown/QueryStats/EpochBreakdown），消灭"每个查询记得加 WHERE"的脆弱约定；Backfill UPDATE 加 `AND is_e2e = 0`（视图不支持 UPDATE，唯一手动点，属写操作非统计口径）。E2E 用例仍查原表（is_e2e=1），单表全量审计能力保留。**tests 613→616**（+3 行为护栏：数据闭环 / 时代隔离 / 父失败回填排除 E2E，同库并存断言）；stats 复测：总任务 405 = 全部 621 - E2E 216，失败类别真实口径（compile_error:2 / validator_failed 消失）。
> **10-10 真实口径视图下沉（task_metrics_real）：** 系统性审计全部 metrics 查询发现失败类别之外还有 3 处漏 is_e2e 过滤：QueryStatsAsync（数据闭环调权地基——small/OrchestratorExecutor 含 E2E 252 条 92.8% vs 真实 119 条 100%）、GetEpochBreakdownAsync（时代隔离面板——small 混入 135 条 E2E）、BackfillFailedSubMetricsAsync（父失败回填会把 E2E 子记录改写为 validator_failed）。**下沉方案**：建 SQL 视图 `task_metrics_real`（`WHERE is_e2e = 0`），6 个真实口径查询统一 FROM 视图（Summary/Recent/RecentFailures/FailureBreakdown/QueryStats/EpochBreakdown），消灭"每个查询记得加 WHERE"的脆弱约定；Backfill UPDATE 加 `AND is_e2e = 0`（视图不支持 UPDATE，唯一手动点，属写操作非统计口径）。E2E 用例仍查原表（is_e2e=1），单表全量审计能力保留。**tests 613→616**（+3 行为护栏：数据闭环 / 时代隔离 / 父失败回填排除 E2E，同库并存断言）；stats 复测：总任务 405 = 全部 621 - E2E 216，失败类别真实口径（compile_error:2 / validator_failed 消失）。
> **10-10 失败类别统计补洞（is_e2e 过滤）：** GetFailureBreakdownAsync 此前漏 `AND is_e2e = 0`（唯一漏过滤的统计查询，成功率/最近失败/最近 N 条均正确），导致失败类别被 E2E 用例严重污染：compile_error 21→真实 2（19 条 E2E）、validator_failed 19→真实 0（全部假象）、validator_mismatch 2→真实 0。已补过滤并与 GetSummaryAsync 口径一致；tests 补洞（E2E 失败与真实失败同库并存断言排除）+1 → 613/613；stats 面板复测确认真实口径（compile_error:2 / validator_failed 消失）。
> **10-08 证据链落库（B 阶段）：** 边缘成功（验证器命中软信号但存在成功证据）从日志升级为可统计、可审计的数据。① OutputVerifier 4 参版 reason 在 hadEvidence=true 时保留信号描述（修复首版实现中 reason 被尾部清空覆盖的 bug——tests 反射断言实证）；② AgentLoop/Orchestrator 两个调用点在边缘成功时注入 [边缘成功] 标记（与 [检测] 同构进 ToolResultEvent），不再只打 LogWarning；③ AgentLoopMetricsDecorator / OrchestratorMetricsDecorator 检测词加“边缘成功” → is_suspicious=1 落库。**数据语义**：命中软失败信号 + 成功证据 → 任务成功但标可疑（stats 面板“可疑成功”计数回流，可审计容错误放）。**tests 610→612**（+2 reason 保留断言）；E2E 复测演示计算器任务：输出含 [边缘成功] 标记，metrics is_suspicious=1。
> **10-08 证据链先行（OutputVerifier 收敛，A 方案）：** 用户方向性警告验证器复杂度持续上升（通用失败词 + 成功信号容错 + expected 容错 + 演示计算行 + 演示错误行 + 演示查找行……每层容错都是事故补丁且规则间相互作用，尽头是“验证器变成不可维护的规则堆”）。核实后实施结构性收敛：原 4 层容错（expected 含失败词 / 成功信号 / 演示计算行 / 演示查找行）合并为统一“成功证据”检测器 HasSuccessEvidence（4 个证据形态：预期含失败信号 / 成功计数报告 / 演示计算程序 / 演示查找流程），语义从“豁免魔法”变为“证据存在性”——命中软失败信号（失败词/查找信号/JSON 字段）时，有成功证据 → 边缘成功（不判失败，调用方可标记可疑）；无成功证据 → 判失败。**双路径独立判定保持保守语义**：查找类豁免只豁免查找信号本身，通用失败词仍独立判定（防叠加信号被吞——未找到+ID列表+处理失败 组合仍判失败）。**API 兼容**：HasGenericFailure 3 参签名保留（tests 反射绑定不受影响），新增 4 参重载输出 hadEvidence；AgentLoop/Orchestrator 两个调用点改用 4 参版，hadEvidence=true 打 LogWarning（边缘成功可观测，任务级可疑落库留 B 阶段）。**行为等价验证**：tests A/L 两区块 20 条历史断言逐条核对保持（含 2024 “成功 5 条”容错 2 收紧为“成功+数字”——实测证据 4 吸收“操作成功”词，行为不变）；新增 12 条断言（证据检测器 8 + 4 参重载语义 4，含“查找豁免不吞通用词”组合）。**tests 598→610 全绿**；E2E 冒烟：“成功计算 + 除零错误演示”计算器任务——stdout 命中“错误:”软信号，证据 3 识别为边缘成功放行，任务完成无回归。
> **10-08 符号地图治理（analyze_code 大文件截断 + range 参数）：** BigService 复测实证 analyze_code 全量符号地图缺陷——5003 个方法的文件 → 5003 行地图全量注入上下文 → 模型被淹没，analyze_code 后无后续动作（write_not_attempted 卡分析阶段）。修复（确定性、数据结构性）：① 全量模式符号上限 `MaxSymbolEntries=300`，超出只显示前 120 条 + 省略行（`... 省略 N 个符号 ...`）+ 定向引导（find_symbol / range）；符号总数统计仍真实；② 新增可选 `range` 参数（1-based 行段含边界，如 1500-1550）只扫描该行段；非法/超界 → 用法提示。**复测 BigService**：模型走通 analyze_code range → find_symbol Method1500（[BigService.cs:1503]）→ 尝试 edit_file，53s（原 150s 卡死）；**完成门 D 真实触发**（首见日志：'任务要求文件中包含内容「已审查」，但本轮写过的文件均未包含'）——模型 edit_file old_string 不唯一失败未写入 → D 门拦截诚实失败 + 回滚正常。**剩余边界（记录）**：模型拿 find_symbol 指引（offset=1493）后未 follow（未读段即 edit_file）——30B 已知行为边界（E2E 6 轮实证'不会跟着符号位置去 read_file'）；彻底解法 = 完整定义内容注入（B 方案，另行确认）。
> **10-08 内容符合性门（完成门 D · 验证层补洞）：** BigService 加注释复测实证假成功——模型定位/编辑链路全对（read_file offset 分页 → edit_file 精确匹配 → dotnet run 编译通过），但 new_string 生成错（x + 1500 写成 x + 15000 且漏加 // 已审查），验证层只看编译+运行成功 → ok=1 假成功。**根因 = 完成门缺"修改内容符合需求"这道门**（只查写到了目标 B/C，不查改的内容对不对）。修复（数据驱动、非提示词）：① 新增 `ContentConformance`（`src\Agent.Infrastructure\Project\ContentConformance.cs`）——从任务输入提取需求 token：引号/反引号包裹内容 + 注释形态（`// 已审查`/`# TODO`），排除 URL（`(?<!:)//`）与 C# 语言名（`(?<![A-Za-z])#`，否则所有 C# 任务全量误伤）；② `CompletionGate.Evaluate` 增加 D 门：有需求 token + 本轮写过文件时，任一写过的文件（归一化后）必须包含任一 token，否则判失败（verification 类，消息附需求内容，引导 edit_file 精确修改）。**自查规避的两个灾难**：a) `\bID` 系 .NET 正则 Unicode \w 边界坑（已实证，去边界）；b) verbatim string 中双引号须 `""` 非 `\"`（CS1525/CS1056 定位）；c) `#` 注释正则若不排除 C# 语言名 → 全量误伤（tests ①c 回归断言）。**验证**：构建 0 错误；tests 571→**583/583**（+12：注释形态提取 / URL 不误提取 / C# 不误提取 / 中文引号多 token / 无引号跳过 / 文件名形态过滤 / 文件含 token / 缺 token（改错漏注释）/ 完成门 D 拦截+可疑 / 放行 / 无 token 跳过 / 归一化）。**复测 BigService**：ok=0 write_not_attempted（150s）——假成功消除，熔断防假完成生效；Method1500 保持原样。**遗留新发现**：analyze_code 对 5000+ 方法文件输出全量符号地图（约 5003 行）淹没上下文，模型卡分析未编辑——下轮候选修复（大文件符号地图截断/聚合）。
> **10-08 Lookup 演示容错（用户确认"看下其他还有那些需要修复的"后实施；not_found 类 3 条失败里 2 条实证）：** metrics 真实失败回查发现 **LookupFailSignals（未找到/not found/not exist）命中即失败、无演示容错**——同一验证器里 GenericFailHits 已有演示容错（A/A'），查找类信号路径没有，教学演示程序被误杀：① 10-03 图书管理系统：完整演示（添加 3 本→列表→查找 ID=2 找到→查找 ID=99 "未找到该图书"→移除→菜单）被"未找到"误判 not_found → 修复循环白跑 10 分钟；② 09-22 产品查询项目：任务**本身要求**"查询 ID=99 时打印未找到该产品"，expected 未传（agent 路径）→ 被误杀。**根因 = 查找类信号路径缺演示分支豁免（与 A' 同族：一条信号路径有容错一条没有）。**修复（`OutputVerifier.HasGenericFailure` 容错 3b）：LookupFailSignals 命中且预期文本不含该信号时，若 `HasDemoLookupSuccess(stdout)`（正向形态：`(?<!未)找到` / `(?<!not\s)found` / `ID\s*[:：]` 列表条目 / 已添加/已成功/成功/added/removed）→ 视为演示分支 continue 继续检查其余信号；否则仍判失败。**关键实现坑**：① .NET 正则默认 \w 含 Unicode 字母，"品"与"I"之间无单词边界 → `\bID\s*:` 对"产品ID:"不匹配（实测 False），改去边界 `ID\s*[:：]`；② 否定前缀陷阱——"未找到"本身含"找到"、"not found"本身含"found"，正向形态必须用 lookbehind 排除（`(?<!未)找到`、`(?<!not\s)found`），否则豁免形同虚设（L8 回归断言）。**验证**：构建 0 错误；tests 563→**571/571**（+8：图书系统演示放行 / 产品查询演示放行 / 孤立未找到仍失败 / 未找到+失败词无正向仍失败 / expected 含未找到放行回归 / not found+列表放行 / 仅 not found 仍失败 / 否定前缀陷阱回归）。影响面：not_found 类任务从"必败+白跑修复循环"转多数成功；结构性错误层（error CS / Traceback / 找不到文件）由前置检查硬拦不受影响。
> **10-08 A' 演示容错放宽（用户确认"执行A"后实施；复测实证 A 修复真实失效 → 形态假设过窄修正）：** 复测 2 真实任务（P0+P1 收尾）暴露：**A 修复在真实任务上失效**——Python 计算器模型实际输出 `10 + 5 = 15 ... 错误: 除数不能为零`（错误行为**独立业务消息行**，无算式前缀 `10 / 0 ->` 形态），原 `HasDemoErrorLine` 正则要求错误行必须带 `数字 op 数字 (=|->) 错误` 算式前缀 → 容错 3 双条件缺一 → 仍判失败（metrics 13:46 empty_response，与 stats 外部凭据类"失败主因=模型空响应"交叉印证）。**根因 = tests 用例假设了错误的输出形态（`10 / 0 -> 错误:`），真实模型形态不同 → tests 绿 ≠ 真实生效（警示第 2 次实证，同 10-08 早间复测教训）。**A' 实施（`OutputVerifier.cs`）：容错 3 判据② 由 `HasDemoErrorLine`（算式前缀）改为 `HasDemoFailMessage`（业务消息形态）——正则 `(?:错误|失败)[:：](?!\s*0\b)\s*\S|(?:\berror\b|\bfail\w*)[:：](?!\s*0\b)\s*\S`：错误词 + 冒号 + 非空白内容（排除 `错误: 0` 等否定/计数形态；首版 `\s*+` 占有量词 .NET RegexParseException "Nested" 不支持 → 改前瞻前移 `(?!\s*0\b)` + `\S` 兼容写法）；判据① `HasDemoCalcLine` 不变（成功计算行）。**验证**：构建 0 错误；tests 559→**563/563**（+4：独立错误消息行放行 / 英文 error 放行 / 否定计数形态 `错误: 0` 仍判失败 / 无成功计算行仍判失败）；**复测 Python 计算器真实任务通过（metrics 10-08 14:02 ok=1）**——模型改写 sys.argv 版（PyCodeGuard 拒 input() 后）输出四则结果，A' 容错路径由 tests 断言覆盖。任务 1（C# 字母大小写）本次未触发 B（无自重复），模型写语法错误 CS1039 → 30B 生成质量边界，不修（已定性）。提交链：ac139a2 后新提交（A'）。
> **10-08 A' 演示容错放宽（用户确认"执行A"后实施；复测实证 A 修复真实失效 → 形态假设过窄修正）：** 复测 2 真实任务（P0+P1 收尾）暴露：**A 修复在真实任务上失效**——Python 计算器模型实际输出 `10 + 5 = 15 ... 错误: 除数不能为零`（错误行为**独立业务消息行**，无算式前缀 `10 / 0 ->` 形态），原 `HasDemoErrorLine` 正则要求错误行必须带 `数字 op 数字 (=|->) 错误` 算式前缀 → 容错 3 双条件缺一 → 仍判失败（metrics 13:46 empty_response，与 stats 外部凭据类"失败主因=模型空响应"交叉印证）。**根因 = tests 用例假设了错误的输出形态（`10 / 0 -> 错误:`），真实模型形态不同 → tests 绿 ≠ 真实生效（警示第 2 次实证，同 10-08 早间复测教训）。**A' 实施（`OutputVerifier.cs`）：容错 3 判据② 由 `HasDemoErrorLine`（算式前缀）改为 `HasDemoFailMessage`（业务消息形态）——正则 `(?:错误|失败)[:：](?!\s*0\b)\s*\S|(?:\berror\b|\bfail\w*)[:：](?!\s*0\b)\s*\S`：错误词 + 冒号 + 非空白内容（排除 `错误: 0` 等否定/计数形态；首版 `\s*+` 占有量词 .NET RegexParseException "Nested" 不支持 → 改前瞻前移 `(?!\s*0\b)` + `\S` 兼容写法）；判据① `HasDemoCalcLine` 不变（成功计算行）。**验证**：构建 0 错误；tests 559→**563/563**（+4：独立错误消息行放行 / 英文 error 放行 / 否定计数形态 `错误: 0` 仍判失败 / 无成功计算行仍判失败）；**复测 Python 计算器真实任务通过（metrics 10-08 14:02 ok=1）**——模型改写 sys.argv 版（PyCodeGuard 拒 input() 后）输出四则结果，A' 容错路径由 tests 断言覆盖。任务 1（C# 字母大小写）本次未触发 B（无自重复），模型写语法错误 CS1039 → 30B 生成质量边界，不修（已定性）。提交链：ac139a2 后新提交（A'）。
> **10-08 C+A+B（用户确认"①→② 开展"后：C 项完成 + 复测暴露 2 缺陷 + 按 A→B 修复）：** **C 项 LLM HttpClient 超时 600s→20 分钟（用户确认实施）**：AgentOptions 加 OllamaHttpTimeoutMinutes=20；KernelFactory RegisterEndpoint 增 options 参数——Ollama HttpClient 用 options.OllamaHttpTimeoutMinutes（云端 5 分钟不动）；tests +1 断言（545/545）。实证依据：10-04 BigService 890s 任务被 600s HttpClient 超时腰斩。**复测 2 真实任务（验证 B 兜底，B 未触发=正常——llm_error 异常为偶发事件）**：两任务均失败，暴露 2 个确定性缺陷。**缺陷 1 · OutputVerifier 误杀演示输出（Python 计算器）**：模型写对——程序是教学演示，输出正常计算行 10 + 5 = 15 + 异常分支演示行 10 / 0 -> 错误: 除数不能为零、8 % 3 -> 错误: 不支持的运算符: %。HasGenericFailure 命中 GenericFailHits "错误:" → hasFail=true → 无 "成功/success" 词 → 判失败 → 模型 read_file 确认代码正确但每轮 run_command 都触发检测 → 修复循环熔断误杀。本质：演示程序的"业务演示失败行"被当真实失败。**缺陷 2 · 同文件内重复类型定义无拦截（C# 字母大小写）**：模型 write LetterCaseChecker.cs 900 字符内写了 2×class LetterCaseChecker → CS0101 编译失败 → 修复循环熔断。P-D（TypeConflictDetector）只拦跨文件重复（工程已有类型），不检测新内容自身重复；10-03 的 CS0101 5 条失败同源。**A · 演示分支容错（OutputVerifier 容错 3，确定性规则非提示词）**：stdout 同时含 ① ≥1 行 数字 op 数字 = 数字 成功计算形态（HasDemoCalcLine）且 ② ≥1 行 数字 op 数字 (=|->) 错误/失败/error 异常演示行（HasDemoErrorLine）→ 视为演示程序正常输出放行；结构性错误层（Traceback / error CS / 未捕获异常）由前置 HasStructuralError 硬拦不受影响——只放行"演示性失败行"，真失败仍拦。**B · 同文件自重复类型定义检测（P-D 扩展）**：TypeConflictDetector 新增 DetectSelfDuplicateTypes（计数版——现 ExtractTypeNames 去重不能复用；剥离注释/字符串后同内容中 class X ≥2 次 → 返回"名（定义 N 次）"按次数降序）；WriteFileTool/EditFileTool 对 .cs 新内容在敏感区检查后、工程 P-D 前独立拦截（**独立于工程模式：沙箱/工程均生效**——实证 LetterCaseChecker.cs 是沙箱任务）→ 拒绝消息"新内容自身重复定义类型：X（定义 2 次）。请只保留一份…"。**顺带确认（不改）**：cd CSharpProject && dotnet run 被 run_command 白名单拒 = 设计内行为（防路径绕过，模型应改用 --project）。**验证**：构建 0 错误（7 既有警告）；tests 545→**559/559**（+14：A 6——演示放行/真失败仍拦/仅错误演示行无成功行仍判失败/结构性错误不豁免/既有成功信号容错保持/expected 容错优先；B 单元 4——2 次重复/单类空/注释字符串不计/3 次；工具拦截 4——write 沙箱拒绝/拒绝未创建/单类成功/edit 拒绝）。提交链： 367c96（C 项）→ 95d24fd（清理误提交 5 个诊断临时文件 build_c/cli_review_*/tests_c + .gitignore 补录——教训：构建/tests/CLI 复测输出文件须先补 .gitignore 再 git add -A）→ ef2454f（A+B）。# LocalCodeForge 项目交接文档

> 撰写日期：2026-09-22（基于源码通读 + 真实构建验证 + workspace 运行数据核对）
> **10-08 内核复盘三连收尾（用户确认"按 1→2→3 顺序"；② 记忆排查 + ③ 真实失败收尾 A+B，含回填误标修正教训）：** **② 记忆 feature 排查（复核结论：无缺陷）**：复盘初判"feature 缺失"为误读——feature 是**标签列**（存 TaskFeatureClassifier 特征类）而非 kind 值；实际 feature 非空 46 条（SingleFile 18/Other 16/ReadQuery 6/MultiFile 3/ProjectIncremental 3），ArchiveTask 传 `TaskFeatureClassifier.Classify(input)` 写入正常，Search 跨特征类过滤正常（task 剔除 / model 降权），治理无删异常——**全链路健康，零改动**。**③ 真实失败收尾**：审计 metrics 失败明细暴露两个缺口——字母大小写 cancel 68s 任务（同类前 2 次成功，Ollama 端偶发中断流）与 890s 超时（600s HttpClient 已知边界）实证 **LLM 调用异常此前直接判失败、无任何兜底**。**A 补 E2E 回填特征（含误标→修正全过程）**：Order 增量 E2E 簇（GetSummary 12/OrderTotalService 4/TotalAmount 1 = 17 行）漏标污染 orchestrator 成功率与 validator_failed 类别。首版回填用宽特征 `(OrderService AND (增加/新增))` → **误标 146 行真实任务**（10-03 项目名幻觉任务链 DiscountService/TaxService/CalculateTotal 的真实输入"在工程中新增 DiscountService 类…并在 OrderService…"含 OrderService+新增）；复位 SQL `LIKE '%DiscountService%'` 又过宽——**误伤 112 行真 E2E**（10-03 21:44 "在工程中新增一个 ShippingService 类，复用 TaxService 和 **DiscountService** 计算…"E2E 用例输入含 DiscountService 复用描述）。**最终修正（数据驱动、时间线判定）**：恢复 E2E（`LIKE '%新增一个 ShippingService%'` 41 行 + `ShippingService+TaxService` 复用特征 71 行 = 112 行）→ 复位真实任务（TaxService/CalculateTotal/DiscountService 且 **NOT ShippingService**）→ **终态：is_e2e=1 共 215 行（ShippingService 141 / e2e路径 45 / OrderSummary 12 / GetSummary 12 / OrderTotal 4 / TotalAmount 1），0 无法归组、0 残留未标记；真实统计 总 390 成功 345（88.5%）**。**回填 SQL 教训固化**：特征必须精确到 E2E 构造措辞（`%给 OrderService 增加%` / `%OrderTotalService%` / `%给 Order 增加 TotalAmount%`），**禁用"类名+动词"宽组合**——真实增量任务与 E2E 构造共享工程类名与措辞，靠类名不可区分，只能靠固定构造开头。**B LLM 调用异常升级兜底**：AgentLoop catch 处 `errorMessage` 非空时先 `PromoteService(decision.ServiceId)` 升级一次（small/agent_pro → large；large 已在最高层 → 判失败；每任务天然至多一次）——升级后 `decision=新 RoutingDecision(LargeModel)` + `firstDecision` 更新 + `_lastStepFailed=false`（防 recovery 重路由回原层白升）+ chatHistory 注入"上一步模型调用失败…请重新开始"；与空响应升级链同构。**FailureCategorizer 加 `llm_error` 类别**（Error 统一前缀"LLM 调用失败："，与 empty_response 区分，此前落 other 无法聚合）。**验证**：构建 0 错误；tests 540→**544/544**（+4：llm_error 归类 2 + 升级语义 large 无目标/small-agent_pro 可升 2）；CLI 无头启动触发精确回填幂等实证（0 新增/0 误标，构成保持 215，真实 390/88.5%）。**C（可选）**：HttpClient 超时 10→20 分钟用户未明确要求，未做。
> **10-08 metrics 数据治理：E2E 用例标记（用户确认"按 1→2→3 顺序"的第一项；让 stats 数字反映真实）：** 内核数据复盘（只读查询三个库）实证——**metrics 被 E2E 用例污染**：compile_error 21 条中 19 条是故意失败的 E2E 验证用例（case_incremental_*/ShippingService/OrderSummary/use --write 冒烟），other 9 条中 7 条同源——裸成功率 84.8% 被拖低，失败类别分布失真，后续调权决策被污染。**实施**：① `TaskMetric` 加 `IsE2E`；② `task_metrics` 加 `is_e2e` 列（建表 + ALTER 迁移，沿用 is_suspicious→failure_category 兼容先例）；③ **历史回填**（Initialize 内幂等 UPDATE，仅未标记行）：特征 = `workspace\e2e` 路径（error_summary/user_input）+ E2E 专属任务输入（ShippingService/OrderSummary/use --write 开头——E2E 用例构造，真实任务从未以此开头，**0 误标实证**：标记 198 行全部符合特征，分布 ShippingService 141/OrderSummary 12/e2e 路径 45）；④ **未来标记**：装饰器（AgentLoop/Orchestrator）组装 IsE2E = env `LAW_E2E=1`（E2E 冒烟脚本设置）；⑤ **stats 真实化**：GetSummaryAsync/GetRecentAsync/GetRecentFailuresAsync 全部 `WHERE is_e2e=0`，新增 `GetE2ECountAsync`——CLI stats 交互面板与无头文本均加"E2E 用例（已单列）"行。**回填后真实统计**：总 605 → 真实 407 条（剔除 198 条 E2E），真实成功率 **85.5%**（裸 84.8%），compile_error 21 → **真实 2 条**（10-01 控制台计算器 CS5001 + 10-03 DiscountService CS0246），validator_failed 19 → 11，other 9 → 4——真实失败集中在 empty_response 13（天气类历史）/max_steps 8/write_unverified 7。**验证**：构建 0 错误；tests 536→**540/540**（+4：E2E 落库/真实统计剔除/GetRecent 过滤/E2E 计数单列 + GetRecentFailures 过滤）；CLI 无头启动实测回填生效。**边界**：回填特征是下界+专属输入并集（E2E 用例错误摘要含路径才可识别；新 E2E 靠 env 标记）；真实统计不含 E2E（单列展示）；routing_decisions 调权数据独立于 task_metrics，不受此治理影响。
> **10-07 webUI 交互（用户确认后实施；"不是去掉 CLI，而是 web 作为主交互入口"）：** 核心洞察——主循环被 `ReadLine` 阻塞、Dashboard 只能看不能发，唯一差距是**输入源硬编码 Console**。方案：命令通道化。**① `CommandHub`（Agent.Core，有界 100 DropOldest）**：终端输入与 web 提交统一汇入同一通道——双入口、一次一任务串行语义不变。**② CLI 主循环输入通道化**：交互态起控制台后台线程（行编辑 Tab/↑↓/历史原样保留）写入通道，主循环 `commandHub.NextAsync()` 消费（阻塞时 web 提交即时响应，用户无需碰终端）；**无头（管道/重定向）保持原同步读-执行循环——E2E 输出逐字节兼容**；EOF（null）→ 退出。**③ Dashboard `POST /api/commands`**：空输入/超 2000 字符 → 400；通道满 → 429；成功 → 202 {queued:true}；`DashboardOptions` 加 `CommandHub` 字段（CLI 组装 + tests 两处构造同步）。**④ 前端命令输入框**（实时流上方）：提交 → POST → 202 清空输入框 + "已排队"提示；Enter/点击均可。**验证**：构建 0 错误；tests 511→**517/517**（+6：CommandHub FIFO/EOF/有界丢最旧/取消 → null + POST 400/202）；无头冒烟（help/exit 管道）逐字节兼容正常。**达成形态**：web 发任务、看过程、看结果、管理命令（use/stats/memory 等透传）都走浏览器；终端保留为第二入口；CLI 本体分发逻辑零改动。
> **10-08 webUI 交互 P0（用户确认后实施；补齐评估短板前两位——队列可见性 + 任务取消）：** 方案——① 队列状态：`CommandHub` 由 Channel 重写为**自实现有界队列**（单一数据结构，无双路径漂移；`Queue<QueuedCommand>` + SemaphoreSlim，seq 递增、SubmittedAt、Snapshot、DropOldest 100 丢最旧）；新增 **`GET /api/queue`** → `{pending, items:[{seq,input,submittedAt}]}`；`POST /api/commands` 202 响应补 **seq+pending**（web 用户提交即知"第几位/前面几个"）；前端提交提示升级 + **每 3 秒轮询 queueStatus**（队列可见性实时化）。② 任务取消：`CommandHub` 增 `AttachTaskCancellation/DetachTaskCancellation/CancelCurrentTask`——主循环任务开始把现有 `taskCts` 挂载进 hub（**与终端 q 键同一取消令牌**，Orchestrator 阶段间 ThrowIfCancellationRequested 响应，取消后走 AutoRollbackOnFailure），任务 finally 摘除；新增 **`POST /api/cancel`**（有任务 → 200 {cancelled:true}；无 → 409 {error}）；前端"取消当前任务"按钮。**验证**：构建 0 错误；tests 517→**524/524**（+7：CommandHub seq/SubmittedAt/Snapshot 序与 EOF 过滤/DropOldest 快照/取消 Attach→Cancel 令牌/Detach 后 false + API queue 结构/cancel 409）；无头冒烟（help/exit 管道）逐字节兼容正常。**边界**：取消为协作式（任务在阶段间/工具等待点响应，非强杀）；队列仍一次一任务串行；P1（任务归属 seq 联动 SSE、管理命令插队）与 P2（CLI 守护进程化）明确未做。
> **10-08 webUI 交互 P1-3 任务归属（用户确认 P1 后实施；P0 复测真实任务通过后开展）：** 方案——评估短板 #3"SSE 混排归属模糊"：一次一任务串行下同一时刻只有一个任务在跑，归属问题 = "前端不知道当前事件流对应哪条提交"。**实施**：`CommandHub` 增 `AttachTaskInfo(seq,input)/DetachTaskInfo()/CurrentTask`——主循环消费命令时保留 `currentSeq`，任务块挂载归属（与取消同 finally 摘除）；Dashboard 新增 **`GET /api/current-task`** → `{running:true, seq, input}` / `{running:false}`；前端轮询升级——**同时拉 /api/queue + /api/current-task，显示"▶ 当前任务：{input 截断 26 字符}（提交号 N）+ ⏳ 队列中 M 个"**。**零事件模型改动**（TaskEvent/SSE/持久化共用 JsonOpts 未动）——归属信息不进事件流，由轮询端点独立提供（数据单一来源 CommandHub）。**验证**：构建 0 错误；tests 524→**527/527**（+3：CommandHub Attach/Detach/CurrentTask + /api/current-task 无任务 {running:false}）；无头冒烟逐字节兼容正常。**边界**：无头路径 currentSeq 恒 0（无 web 交互，归属无意义）；仅任务执行中显示归属，排队中命令只显示队列数。
> **10-08 webUI 交互 ①②（用户确认一起推进；评估缺口 #1 产物浏览 + 内核兜底 #2 任务超时）：** **① web 任务产物浏览**：`GET /api/files`——run 工作目录文件浏览（**PathBoundary 逐段判定限定 WorkspaceRoot 内**，越界 400/不存在 404；path 为空 → 根 children；目录 → children{name,isDir,size,modifiedAt}；文本文件 → {text:true, content 前 50KB 截断}；二进制 → {text:false}；**raw=true → 下载**（octet-stream + 文件名））；`IsTextFile` 扩展名白名单（无扩展名默认文本）+ `PreviewText`（UTF-8 BOM 探测 + 50KB 上限）；**数据源 = 磁盘真实文件（任务产物），非内存镜像——单一来源**。前端"任务产物"卡片（实时流下方）：面包屑路径 + 列表（📁 目录点击进入 / 📄 文本点击预览 / 二进制显示大小）、"上级/刷新"按钮、`<pre>` 预览区 + 下载链接。**② 任务级整体超时**：`AgentOptions.TaskTimeoutMinutes` 默认 **30**（0=禁用）——主循环任务块 `Timer` 到期触发 `taskCts.Cancel()`（**与终端 q / web POST /api/cancel 同一取消令牌**，Orchestrator 阶段间响应 → cancelled → 照常走失败回滚承接）；`timeoutFired` 标志区分来源——**只有超时触发才打印"[任务] 超过 N 分钟未完成，已自动取消"**，q/web 取消不误报；Timer finally Dispose。**验证**：构建 0 错误；tests 527→**532/532**（+5：/api/files 根列表含 children+sample.cs / 文本预览含 content / raw 下载 200 / 越界 ../../ 路径 400 + TaskTimeoutMinutes 默认 30）；无头冒烟逐字节兼容正常。**边界**：文件浏览仅限 run 工作区（工程模式产物在工程根，需 use 后另行浏览——工程文件浏览未做，可后续扩 path 白名单）；超时为协作式（与取消同机制，阶段间响应）；预览截断 50KB。
> **10-08 webUI 交互 ③④⑤（用户确认三个轻量项一起开展）：** **③ 安全 token 加固**：`DashboardOptions` 末位加 `string? AuthToken = null`（可选参数，tests 现有构造零破坏）；`MapEndpoints` 加中间件——AuthToken 非空时所有端点校验 **query `token` 或 header `X-Dashboard-Token`**（任一匹配放行，否则 401；SSE 只能带 query 与 fetch 统一兼容）；CLI `dashboard` 命令生成随机 token（Guid N）拼入 URL——**CLI 打印的完整 URL 即访问凭证**；前端 JS 从 `location.search` 取 token，**fetch 全局包装自动附带 token query（零逐点改动）+ EventSource 单点带 token**。**④ 交互态自动开浏览器**：dashboard 启动后 `Process.Start(UseShellExecute)` 自动打开默认浏览器（失败静默）；无头态仍被守卫拦截。**⑤ 启动自检补 Large**：健康检查 localEndpoints 此前只有 Classifier/Small/AgentPro——**Large 角色漏查**（配置错误任务跑起来才发现）；补 `("Large", options.Large)`——冒烟可见 `[OK] Large law-unified`。**验证**：构建 0 错误；tests 532→**536/536**（+4：AuthToken 无 token 401 / 错 token 401 / 正确 token 200 / header 200，迷你带 token 实例）；无头冒烟逐字节兼容正常（健康检查增 Large 行）。**边界**：token 明文在 URL（本地回环单用户工具，非 HTTPS 场景可接受）；无 token 配置（tests）→ 中间件跳过，行为与历史一致。
> **10-07 SVG 依赖子图（用户确认后实施；此前 UI 层 #3 表格为既定降级形态）：** 评估结论——数据已够（`/api/deps` 返回全部 edges，前端过滤即可，**零后端改动**）、无第三方库（铁律）下手写分层布局可行、风险集中在"全工程图蜘蛛网"故明确不做。**实施**：① 纯函数 `layoutOneDegree(center, direct, reverse)`——确定性分层布局（中心 selected + direct 出边左列 + reverse 入边右列，节点宽 150/高 28 固定不重叠，动态 viewBox）；② `renderDepSvg()`——原生 SVG：绿边=直接依赖（中心→direct，箭头 marker depArrowG）、红边=反向依赖（reverse→中心，depArrowR），节点截断 14 字符 + `<title>` tooltip 全名，hover 提亮（CSS），**点击节点 → `selectDepFile()` 联动切换选中文件并刷新表格**；③ **节点 > 25（DEP_NODE_MAX）→ 降级提示 + 保留表格视图**（兜底不变）；④ 暗色主题样式（depRect/depC/depD/depR/depText/depEdge）。**验证**：构建 0 错误；tests 510→**511/511**（+1：嵌入单页含 layoutOneDegree/renderDepSvg/selectDepFile/depArrowG/DEP_NODE_MAX 标记）；无头冒烟正常。**边界**：SVG 仅限单文件 1 度子图（全工程图明确不做——布局质量收益不成比例）；表格保留为超限降级；前端 JS 无独立单元测试路径（C# harness 以嵌入标记断言兜底，视觉验证靠浏览器人工）。
> **10-07 UI 层（用户确认 ①→②→③ 顺序开展）：调权状态卡片 + 记忆卡片 + 终端命令历史。** **① 路由调权状态卡片**：新 `/api/tuning` 端点（时代过滤语义与 ModelRouter.GetTuningStatsAsync 完全一致——epoch 非空只统计 30B 样本；阈值来自 `DashboardOptions.Options` 的 AgentOptions.Routing* 配置；返回 writeDowngrade/readUpgrade/complexDrop 三态 {样本/成功率/阈值/是否触发/原因} + thresholds 段），前端"路由调权状态"卡片照 stats 模式渲染。**② 记忆卡片**：新 `/api/memory` 端点（AgentMemoryStore 直连 agent_memory.db，返回 stats{total/modelCount/taskCount/totalHits} + rows{id/kind/topic/hitCount/count/createdAt/lastHitAt/feature}），前端"记忆"卡片（统计行 + 列表表格）。`DashboardOptions` 增加 `Options`（AgentOptions）+ `MemoryDbPath` 两个字段（CLI 组装处传 options + agent_memory.db 路径，tests 两处构造同步更新）。**③ 终端上键命令历史**：`ReadLineWithTabCompletion()` 扩展 ↑/↓——进程内内存历史（**诚实降级：退出即失，不做磁盘持久化**），上限 100 条防无限增长；↑ 时保存当前编辑半行（draft），↓ 越过最旧一条后恢复 draft；无头分支不变（E2E 逐字节兼容）。**验证**：构建 0 错误；tests 508→**510/510**（+2：/api/tuning 三态+阈值结构 / /api/memory stats+rows 结构）；无头冒烟（help/exit）正常。Dashboard 现共 9 端点 + 8 卡片（实时流/依赖图谱/stats/**调权状态**/**记忆**/任务列表/详情回放/备份 diff）。**已知边界**：记忆卡片为只读视图（无管理操作，管理走 CLI memory 命令）；命令历史进程内不跨会话；SVG 依赖图仍为表格形态（留待单独评估）。
> **10-07 记忆治理 + 路由调权收尾（用户确认后按 ①→②→③ 实施）：** 盘点确认两方向机制已完备、仅 3 个确定性缺口。**① 记忆启动自动治理**：`agent_memory` 此前只靠手动 `memory prune/dedup`（routing_decisions 90d / task_events 30d 都有启动清理，唯独记忆没有）——CLI 启动 Banner 后自动 `PruneExpired(MemoryPruneDays, MemoryPruneMinHits) + Deduplicate()`（阈值走既有 AgentOptions.MemoryPrune* 配置，默认 30 天/1 命中），0 条清理时静默，非 0 打印一行 `[记忆治理]` 日志。**② 调权参数配置化**：`minSamples/minSuccessRate/badRate` 原硬编码在 ModelRouter 三个调用点——提取为 `AgentOptions.RoutingTuningMinSamples(3)/RoutingDowngradeMinSuccessRate(0.6)/RoutingReadUpgradeBadRate(0.6)/RoutingReadUpgradeMinSuccessRate(0.5)/RoutingDropAgentProMinSamples(5)/RoutingDropAgentProMinSuccessRate(0.5)`，默认值与硬编码完全一致（行为零变化），改调权敏感度只改配置。**③ 调权状态可观测**：stats 面板新增"路由调权状态"小节——L0.5 写降级 / L0.5b 读升级 / P2 ComplexReason 降级各自显示 {样本数 / 成功率 / 阈值 / 当前判定（触发 or 保持）+ 原因}，复用时代过滤查询（epoch 已配置 → 仅 30B 样本），零新增数据结构；PrintSummaryAsync 签名加 options 参数。**验证**：构建 0 错误；tests 502→**508/508**（+6：调权参数默认值与原硬编码一致）；无头冒烟（help/exit 重定向）正常——启动治理空库静默无输出污染。**收尾结论**：记忆治理（去重内建+手动+启动自动+feature 过滤注入）与路由调权（落库+展示+90d 清理+时代过滤+参数配置+状态可观测）两方向闭环完成，无遗留缺口。
> **10-07 UI 层 #3/#4/#5（用户确认顺序：先 4+5 小补丁，再 3 工程图谱）：** **#4 终端小补丁**：① Tab 命令补全——`ReadLineWithTabCompletion()`（交互态 ReadKey 自制行编辑：退格/Tab 唯一匹配自动补全+空格/多候选打印列表重绘提示符；**无头守卫 `Console.IsInputRedirected||IsOutputRedirected` 退回 Console.ReadLine——E2E 输出逐字节兼容**）；命令列表与主循环分支一致（use/rollback/stats/memory/plan/orch/diff/dashboard/help/exit/new）；② dashboard URL 交互态 `[link]` 超链接样式（无头态 dashboard 命令已被无头守卫拦截，link 安全）。**#5 前端小补丁**：实时流卡片头部加过滤按钮组（全部/工具/错误/完成）+ 自动滚动 checkbox——**过滤只影响展示不丢数据**（eventsCache 保留最近 300，切换过滤即时重渲染）；SSE 渲染统一走 `renderEvents()`（append 路径单一）；trimBox 死代码删除。**#3 工程依赖图谱**：`/api/deps` 端点（无 path → `{files,edges,topReverse}` 全工程概览 + 改动风险 Top5；path=相对文件 → `{direct,reverse}` 直接/反向依赖）——**与 find_symbol/analyze_deps 同一 symbol_index.db + DependencyAnalyzer 引擎（单一来源，无 DTO）**，DashboardOptions 加 SymbolDbPath 字段（CLI 传 metrics.db 同目录 symbol_index.db）；前端"工程依赖图谱"卡片：全工程概览表格 + 文件下拉选择 → 单文件反向/直接依赖表格 + 粗粒度提示；SVG 图未做（表格为既定降级形态，SVG 布局质量风险留待后续评估）。**验证**：构建 0 错误；tests 498→**502/502**（+4：deps 无工程根 400 / 符号索引可建 / 概览结构含文件+Top / 单文件 direct 含类型级引用）；无头冒烟（help/exit 重定向）逐字节正常——Tab 补全无头分支退回 ReadLine 实测无影响。**已知边界**：deps 每次请求重扫索引增量 + 重算边（51 文件毫秒级，可接受；超大工程可加缓存）；依赖为类型级粗粒度（含注释/字符串误报，前端已提示）；Tab 补全不做历史补全（上键翻历史由终端原生滚动覆盖）。
> **10-07 UI 层 #1/#2 事件持久化 + 任务详情回放（用户确认方案后实施）：** 用户确认 UI 层按 1→2 开展（每实施完更新文档）。**#1 事件持久化（TaskEventStore，`Infrastructure/Events/TaskEventStore.cs`）**：任务事件旁路落库（metrics.db 同库 `task_events` 表，主键 (task_id,seq)，payload=事件 JSON 字符串）；事件循环第三消费者——`renderer.RenderEventAsync` → `eventHub.Publish` → `eventStore.RecordAsync`（fail-safe 静默，绝不阻塞任务/渲染）；**JsonOpts 单一来源移到 TaskEventStore.JsonOpts**（存储=SSE=回放同一 camelCase+字符串枚举格式，DashboardHost 本地 JsonOpts 删除，消双路径漂移）；启动时 `PruneOlderThan(30)` 生命周期清理。**#2 任务详情回放**：`/api/tasks/{taskId}/events` 端点（返回 StoredEvent 数组，payload 为 JSON 字符串与 SSE 格式一致）；前端历史任务行点击回放——**SSE 渲染逻辑抽取为单一 `renderEventLine()`**（实时流与回放共用同一视觉函数，消双路径），失败任务自动滚动定位到第一个 error 事件并描边高亮，detail 卡片显示事件计数。**接线**：ConsumeTaskAsync 加 taskId 参数（两处调用点传 taskId），循环内 seq 递增落库；DashboardOptions 加 EventStore 字段。**验证**：构建 0 错误；tests 492→**498/498**（+6：TaskEventStore 记录/读取/清理/fail-safe 4 + Dashboard 回放端点冒烟 2——含 error 事件 payload 内容断言 + 无事件空数组）；无头冒烟（help/exit 重定向）正常。**已知边界**：浏览器刷新不重放历史（历史走 /api/tasks/{id}/events 点开回放）；task_events 保留 30 天（启动时清理）。
> **10-07 Diff + 真 UI 层（本地 Web Dashboard，用户确认定稿方案后实施）：** 用户保留 Spectre 终端现状，新增 Diff 能力 + 真 UI 层，最终"终端 diff 看改动、浏览器实时看任务"。方案经 3 澄清（旁路接线/SSE 仅当前任务/diff ±3 上下文裁剪）+ 4 补强（byte 方向表/BoundedChannel 背压/Web SDK Library/EmbeddedResource 分发）+ 1 简化（diff 无 DTO）+ 1 边界（dashboard 无头守卫）全部采纳后实施。**① FileDiffService 引擎（`Infrastructure/Diff/FileDiffService.cs`，零第三方依赖）**：统一 hunk 模型（`DiffResult{TooLarge,TooLargeReason,Hunks}`、`DiffHunk{OldStart,OldCount,NewStart,NewCount,Lines}`、`DiffLine{Type(Same/Add/Remove),OldNo,NewNo,Text}`、`DiffOptions{ContextLines=3}`）+ `PlainDiffFormatter`（无头通道）；算法 = 共同前缀/后缀剥离 + 中间段 byte 方向表 LCS（2500²=6.25MB）+ 回溯 + ±3 上下文统一 hunk 合并；**降级只针对中间变化段**（5000 行改 1 行 → 中间段 1×1 恒可 diff；3000 行全不同才 TooLarge——初版按全文 2500 行误判大文件已修）；裁剪后补全前缀/后缀 Same 行（full diff 有上下文可显示）。**② 终端 diff 命令**：SpectreRenderer.DiffBlock 红绿渲染（hunk 头黄/+绿/-红/上下文灰）；主循环 `diff [taskId]`（无参=最近任务；非工程模式提示；无备份提示含 8 上限清理说明；PlainText 走 PlainDiffFormatter）。**③ TaskEventHub（`Infrastructure/Events/TaskEventHub.cs`）**：多播总线，每订阅者 `BoundedChannel(1000, DropOldest)` 背压（浏览器后台不吃内存）、Subscribe 返回 (Reader, IDisposable)、无订阅者 Publish 零开销。**④ Agent.Dashboard 项目（新工程，Web SDK + OutputType=Library）**：`DashboardHost` Kestrel `127.0.0.1:0` 随机端口仅回环（**StartAsync 返回停止委托而非 StopAsync Task——StopAsync 调用即执行，直接返回会在启动后立刻停服，tests 实测暴露**；`_running` 静态字段持 app 防 GC）；端点：`GET /`（嵌入 index.html）、`/api/stats`（summary+失败类别+epoch 时代隔离）、`/api/tasks?take=`、`/api/backups`、`/api/diff?taskId=&rel=`（**直接序列化 FileDiffService 模型，无 DTO——diff 语义单一来源在引擎**）、`/events`（SSE 逐事件 `data: {json}`，JsonStringEnumConverter camelCase——枚举序列化默认数字，前端需字符串）；index.html 深色终端单页纯 CSS 零外网（SSE 实时流按类型着色 >300 截断、stats 条形图、历史任务表、备份树+Diff 红绿查看器）。**⑤ 接线**：slnx 加 Agent.Dashboard；Cli csproj 加 ProjectReference + **`FrameworkReference Include="Microsoft.AspNetCore.App"`**（Web SDK 框架引用不自动传递，缺失报编译错误）；Program.cs `services.AddSingleton<TaskEventHub>` + 显式解析；`ConsumeTaskAsync` 在 `renderer.RenderEventAsync(evt)` 后 `eventHub.Publish(evt)`（**旁路多播：渲染器直接消费不变，hub 第二消费者，450+ tests 零冲击**）；主循环 `dashboard` 命令（幂等重复只提示；**无头守卫 `Console.IsInputRedirected||IsOutputRedirected` → 提示"需交互终端"不启动 Kestrel——保护 E2E 输出逐字节兼容**）。**构建 3 轮修复**：MSB4066（EmbeddedResource 误放 PropertyGroup → 移 ItemGroup）/ CS0037（值类型元组 FirstOrDefault 返回 default 元组非 null → foreach + nullable 元组）/ CS0136（diff 分支 taskId 与外层冲突 → diffTaskId）。**验证**：构建 0 错误；tests 485→**492/492**（+7：TaskEventHub 订阅/发布/退订 2 + DashboardHost 真起 Kestrel HttpClient 冒烟 5——GET / 嵌入页、/api/stats 结构、/api/tasks 数组、/api/backups 数组、无工程根 diff 400）；无头冒烟（Start-Process 文件重定向 help/exit）正常。**已知边界**：SSE 只覆盖当前任务（历史走 /api/tasks 轮询，前端按 taskId 分组）；上键翻历史仍未做（终端原生滚动覆盖）。DEVELOPER.md tests 数漂移 466→492 已顺手修正。
> **10-07 终端增强（用户确认方案 1：Spectre 继续增强，不做真 TUI/Web UI）：** 三子项按"追加式终端可落地"的形态实施（上键翻历史诚实降级——终端原生滚动已覆盖，自制重绘与 Live 同坑）。**① 任务状态携带（近似常驻状态栏）**：`SpectreRenderer.TaskPrefix` 属性（Program.cs 任务循环维护 `[N步|service|秒]` 前缀，渲染到 ToolCall/ToolResult/FileWritten 高频事件行前）+ 任务开始横幅 + 完成汇总行（`[任务汇总] N 步 / 秒 / service`）——真常驻需全屏 TUI 不做。**② --verbose 启动开关**：工具结果默认截断 300 单行（防刷屏），`--verbose` 时输出全文多行。**③ q 停当前任务（仅交互态）**：任务期间主循环 await 事件流 ReadLine 不可用 → 后台 `KeyAvailable` 轮询（50ms），按 q → `CancellationTokenSource.Cancel()` → Orchestrator 阶段间 `ThrowIfCancellationRequested` 响应（AgentLoop 尽力，长 LLM 调用需等当前步完成——已知边界）；取消任务不归档（不污染成功率指标），工程可写时仍自动回滚半成品改动。**无头路径（PlainTextRenderer）逐字节不变**：汇总/取消行、前缀、监听全部 `sp is not null` 保护（E2E 断言依赖，实测冒烟 help/exit 正常）。**验证**：构建 0 错误；tests 466→**468/468**（+2：Spectre TaskPrefix/Verbose 默认值）。
> **10-07 P2 agent_pro 自动调权（用户确认开启；数据实证 30B 时代 agent_pro 81 样本 93.8%、Planner 68/68，调权门槛达标）：** 让路由闭环只看 30B 真实表现，4B 历史不再污染能力判定。**① 时代过滤闭环查询**：`IMetricsCollector.GetServiceStatsSinceAsync(serviceId, tier, since, take)`（SQL 加 `started_at >= epochUnix`，LIMIT take 不变）+ SqliteMetricsCollector 实现。**② ModelRouter 接入**：新增私有 `GetTuningStatsAsync`——`options.LawUnifiedEpoch` 非空 → 用 Since 版（30B 时代），null → 全量（兼容旧行为）；L0.5 写任务降级判定与 L0.5b 读任务升级判定两处全部改走时代过滤（此前全时代混合：agent_pro 4B 35% 会压住 30B 93.8%，读升级永不触发；写降级 small 判定同样被 4B 历史干扰）。**③ L3 ComplexReason 自动调权（P2 显式一环）**：`RoutingIntelligence.TrySuggestDropAgentPro`（纯函数，保守双条件——样本 ≥5 且成功率 <0.5 才降级 large，数据不足/表现好保持 agent_pro）+ ModelRouter L3 分支接线（serviceId==AgentPro 时查询 agent_pro/SmallModel tier 时代表现；当前 30B 数据 Rule 层 61.5% ≥0.5 → 保持 agent_pro 不触发，样本不足时同样不冒险）。**当前数据下的实际行为**：写任务降级（small 30B 80% 但耗时不如 large）与读任务升级（small 读 80% ≥60% 阈值）均如实不触发；调权机制全部基于 30B 时代数据，随 orchestrator 任务自动积累生效。**验证**：构建 0 错误；tests 459→**466/466**（+7：时代过滤 SQL 2（仅 30B 保留/成功数）+ 全量对照 2 + DropAgentPro 三态 3）。
> **10-07 轻度优化批次（用户确认 A+B+C+D，E 经核查取消）：** 四项纯增量、确定性、零风险改动。**A · run_command 白名单补 grep/Select-String**：模型此前想用 grep 被白名单拒（只能 find/findstr，或被迫全量 read_file）——补两个 token 后可用正则检索工程内容，Description 同步。**B · CLI help 命令**：主循环 new 分支后新增 `help`/`?` 分支，renderer.System 一行打印完整命令表（use/stats/memory/rollback/plan/orch/new/exit）。**C · routing_decisions 90 天生命周期清理**：`RoutingDecisionStore.PruneOlderThan(days)`（DELETE created_at < cutoff，失败静默）+ Program.cs 启动接线（每次启动删 90 天前明细——调权仅用近 10 条，历史明细无统计价值，防表无限增长）。**D · 配置结构自检（tests +9 断言）**：tests 新增「RoutingRules.json 结构自检」分组——回溯仓库根读源 JSON（RoutingRules.json 只复制到 Infrastructure 输出，tests 输出无；PropertyNameCaseInsensitive 缺失初版 3 断言 FAIL 后修复）、JSON 可解析、规则数==8、target 合法（small/agent_pro/large）、**无重名（防"重名死规则"回归）**、priority>0、type 合法、regex Pattern 非空可编译、keyword_any Keywords 非空。**E 取消**：memory list 已有默认 20 条 + 可传 `memory list [N]`，输出上限已具备，无需改动。**验证**：构建 0 错误（6 警告均为历史既有）；tests 450→**459/459**（+9）；CLI 冒烟（Start-Process 文件重定向 + WaitForExit；cmd `<` 重定向经 PowerShell 管道会挂起为已知坑）——help 命令行正常打印（23 行输出含完整命令表）、启动清理接线无异常、exit 正常退出。
> **10-05 只读工程组合边界（ReadOnlyGuard，用户确认实施）：** 工程模式 + 未授权写（use 不带 --write）+ 写任务引用工程内已有文件 → 必然失败（模型写工程文件被写白名单拒，空跑 10-15 分钟）。**新增 `Project/ReadOnlyGuard.cs`**（纯确定性静态，无 LLM，与 CompletionGate 同族）：`ShouldBlock(input, projectRoot, projectWriteEnabled, out hitFile)` 四条件全满足才拦——工程模式 && !可写 && `IntentOverride.IsWriteTask` && `FindExistingProjectFileRef` 命中（任务输入引用的文件名在工程根内实际存在）；豁免 = 显式写目标（TryGetWriteTarget）在工程外 → 放行（"在 X:\test.txt 创建"不拦）。**公共正则抽取（消双路径漂移）**：`TaskClassifier.FileReferencePattern` = `([\w\-\.]+\.[a-z]{1,5})(?=\s|$|/|\\)`（捕获组取干净文件名 + lookahead 边界不消耗字符，IsMatch 语义与历史版 `(\s|$|/|\\)` 完全等价；csproj 6 字母后缀天然不匹配与历史一致）；TaskClassifier 5.5 分支与 ReadOnlyGuard 共用。**CLI 接线**：主循环【自动分发】注释后、taskId 创建前——命中即 `[只读工程] 任务引用了工程内文件 {hit}，但当前为只读模式（use 未带 --write），无法修改。` + 引导两行（use --write 路径 / 重述需求不提及工程内文件——注意工程上下文仍可能引导模型触碰工程文件，存在失败可能，弱化保证诚实提示）+ continue（不建 DI 容器）。**验证**：构建 0 错误（6 警告均为历史既有）；tests 440→**450/450**（+10：拦截/无引用不拦/工程外引用不拦/读任务不拦/显式写目标工程外放行/可写不拦/沙箱 null 不触发/同名 src\App.cs 命中/csproj 后缀不截断/大小写归一）；**CLI 冒烟实证**（UTF-8 无 BOM 文件重定向喂 stdin，`use X:\lawcheck_demo` → 任务"给 OrderService.cs 的 Method1500 加一行注释"）：`[只读工程] 任务引用了工程内文件 OrderService.cs…` 触发、零 `[工作目录]/[路由]/[工具调用]` 模型执行痕迹（总输出 1384 字节）——拦截在任务启动前生效。**测试方法教训**：PS 管道喂 CLI（runner.py b64 / `$input | & cli`）均卡死——管道编码（GBK/UTF-8 混流）使 use 行乱码被当任务跑模型；**cmd 文件重定向 `<` 为字节流无 BOM 最稳**（2 秒完成）。
> **10-05 E2E 复测（num_gpu 14 生效后）：3/4 保持，零超时。** 4 用例实测：工程增量-ShippingService **FAIL（316s，三态="安全拦截生效-模型未接入"）**——与基线一致（30B 增量修改能力边界，安全层恒定兜底，非漏洞）；单文件 hello_e2e.py PASS 30s（产物存在）；多文件 C#学生系统 PASS 88s；读查 Order.cs PASS 18s。**总耗时 453s（22:16:20→22:23:53）**。**num_gpu 14 性能结论**：无显著推理加速（与 10-03 层数实验一致——A3B 每 token 激活 3B，tok/s 增幅小，瓶颈在 CPU 层）；**真实收益 = 稳定性**——CPU 层 40→34，与 dotnet 编译/工具执行的算力争抢缓解，4 用例零 run_command 超时（8 层时代"模型推理与编译抢 CPU → 超时"形态未再现）。本次无代码变更，工作区干净（git dfc2e93）。
> **10-05 运行环境优化·GPU 卸载层（用户确认做 A 后实施，含多轮实测；用户最终拍板"到此为止不再优化"）：** 用户任务管理器观察"GPU 利用率 100% 但功耗仅 20W、任务大部分跑 CPU"，怀疑跑核显。**诊断（nvidia-smi 实证）**：llama-server.exe 一直在 GPU 0（RTX 3060）CUDA 推理——Ollama 官方 Windows 版只有 CUDA 后端，物理不可能用 AMD 核显；核显 2% 是桌面显示合成。**"GPU 占比低"真相 = law-unified Modelfile `num_gpu 8`**（8/48 层 ≈17%，与 ollama ps 83/17 精确吻合）。**卸载层数逐级实测**：8→12（75/25，显存 4924MiB）→14（71/29，5674MiB，稳定）→15（69/31，5916MiB **成功一次但余量 228MB 边缘**，随后 CUDA OOM ×2）→ **回 14 稳定**。推算每层 ≈375-432MB（权重+KV），15→16 层权重本身 >6GB 必爆（q4 KV 实验无效，已回 q8_0）。**根因确认**：83%→71% CPU 推理是 30B Q4（19GB）> 6GB 显存的物理必然 + MoE（A3B 激活仅 3B）功耗天然低 + 3060 Laptop 功耗上限 95W（非 140W）——非配置问题。**两个工程教训**：①OOM 后 **llama-server 僵尸进程残留占 CUDA 上下文 → 后续所有加载请求全 OOM**（现象：14/15 层同时失败、显存仅 647MiB 却报 OOM）——`Stop-Process` 杀残留 llama-server 即恢复；②`num_gpu` 是 Ollama 原生 options，**SK OpenAI 兼容端点（/v1）无法请求级注入**（现有请求级注入仅 Temperature 标准字段）——等价路径 = 最小 Modelfile 覆盖重建 tag（`FROM law-unified:latest` + `PARAMETER num_gpu N`，blob 复用秒级、不占额外磁盘）。**当前生效配置**：law-unified `num_gpu 14` + `OLLAMA_KV_CACHE_TYPE=q8_0`（用户级 env，重启 serve 生效）+ `OLLAMA_FLASH_ATTENTION=1`；Modelfile 源 `workspace\test-tmp\law-unified.numgpu14.modelfile`（改 8/15 均基于它重建）。**Windows 图形设置（用户手动，B 方案）**：ollama.exe + llama-server.exe 已设"高性能"（独显）；日常应用设"节能"（核显）待用户补——CUDA 计算不经过 WDDM 图形适配器选择，此设置仅管显示合成负载分配，对推理无影响。**超出"不换模型"约束的剩余路径（用户暂不做）**：Q3_K_M 量化（~15GB → 可卸 17-18 层 ~37% GPU，有损）或换 7B dense（全 GPU 但违背约束）。
> **10-05 批次3 终·B 必然触发验证（结论：机制成立，30B 边界，承认）：** 5 轮 E2E 构造"必然引用缺失成员"任务（lawcheck_demo 增量：新增 OrderSummary.cs 用 Order.Title/Items → CS0117 必触发）。**前置缺陷修复**：Planner 幻觉 csproj 名（OrderSystem.csproj）→ 阶段 3.2 工程模式下 csproj 一律用工程根实际文件（Directory.GetFiles 排除 bin/obj + run_command 里 --project 幻觉名替换为实际名）——E2E 实证卡死 3 次后修复，任务得以跑到编译阶段。**B 注入机制两次实证触发**（日志"类型缺失 Order：注入完整定义（Order.cs，101 字符）"）；注入源扩展：fileContents（仅计划文件）找不到 → 回退工程根扫 .cs 读 class 定义（工程已有类型 Order.cs 不在 fileContents，初版永不触发）。**效果判定：失败**——30B 生成阶段幻觉任务未提的成员（CustomerName/OrderItem），注入 Id/Status 定义后 small/agent_pro/large 修复全部"输出与原文相同"不按定义修正 → 按用户预置标准（修复后编译未通过 = 边界）**承认：完整定义注入方向在 30B 上不成立**（与 offset 引导同类边界；代码保留——机制正确、零成本、未来换强模型/云端可复用）。附带实证：修复升级链"输出与原文相同"熔断工作、P-D 拦 30B 重复定义 Order 正确、回滚还原正常。tests 440/440。
> **10-05 批次3 续·compile_error/other 分布驱动修复（用户确认 A+B）：** 分布实证（17 compile_error + 8 other）：compile_error 高度集中——**13/17 是工程已有类型理解不足**（CS0117/CS1061 成员缺失 7 条全在增量任务：模型用 Order.Items 但没改 Order.cs；CS0101 重复类型 5 条；CS0246 跨文件 1 条），CS5001 无入口 4 条为生成质量边界；other 8 条 = **2 个已知模式漏词**（"修复失败：模型输出与原文相同"×3 + "输出与计划预期不完全一致"×5）。**A 词表补洞**：FailureCategorizer +`validator_mismatch`（严格校验不一致，优先于 validator_failed）+ `repair_unimproved`（修复无改进）。**B 类型缺失定义注入**（修复循环内）：`ExtractMissingType` 确定性提取（`“X”未包含“Y”的定义`/CS0246/找不到类型，弯直引号兼容）→ 从 fileContents 找 `class X` 文件 → **完整定义内容注入修复上下文**（非位置指引——30B 不会跟位置去 read_file 已 6 轮实证）；约束：只在修复阶段、每任务一次、只注入 1 个符号。E2E 实证：orchestrator 增量任务**父失败回填生效**（"父失败回填 2 条 Executor 子记录"）；B 注入路径本次未触发（模型生成绕开了缺失成员）；另发现组合边界：`use` 不带 `--write` 时 orchestrator 增量写工程文件被白名单拒 → 必然失败（写保护合理行为）。tests 432→**440/440**（+8：Categorize 2 + ExtractMissingType 6）。
> **10-05 批次3·Executor success 语义（用户确认 C+A 后实施）：** 现状核实：Executor 子指标 success="content 非空"（生成调用成功，286/286=100% 真实）≠ 顶层任务成功率（68.8%，验证门后）。**C 父失败回填**：`IMetricsCollector.BackfillFailedSubMetricsAsync`（SQLite UPDATE：routing_tier='OrchestratorExecutor' AND success=1 AND finished_at≥since AND user_input 前缀匹配（substr 500 对齐子记录截断）→ success=0 + failure_category='validator_failed' + error_summary 附"[父任务未通过验证（回填）]"）；`OrchestratorMetricsDecorator` Error 分支调用（顶层 orchestrator 失败即回填本任务窗口内 Executor 成功记录，时间窗隔离同输入历史任务）。**A 展示标注**：stats 汇总新增"Executor 生成成功率"行（GetServiceStatsAsync small/OrchestratorExecutor），标注"← orchestrator_sub 子指标（父失败已回填），≠ 任务成功率"消除 100% 误读。**FailureCategorizer** 加"未通过验证"/"父任务未通过验证" → **validator_failed**（新类别）。tests 426→**432/432**（+6：Categorize 2 + 回填 4——窗口内回填/窗口外不碰/原失败不动/标注附言）。
> **10-05 批次4 后续·修复重试无进展熔断（用户确认"先做1再选型"后实施）：** 修正批次4 推断：`修复失败：模型输出与原文相同`熔断**已存在**（fixedContent==currentContent 立即终止），真实缺口是**修复重试间错误签名未变化仍重试满 3 次**（3 条"重试 3 次"实证同错误反复修复无效）。落地：`ExtractErrorSignature`（public static，错误码 CS\d+ / [A-Za-z]+Error / 文件(行号 / line N 指纹）+ 循环内 `newSig == lastErrorSig && retries > 0` → 提前终止"修复重试无进展：错误签名未变化"。tests 422→**426/426**（+4：C# 签名 / Python 签名 / 相同指纹 / 无错误 null）。
> **10-05 批次4：Orchestrated 失败拆解（纯 SQL 分析，零改动）：** 109 条 orchestrator 任务失败 34 条（68.8% 成功率），按 error_summary 前缀拆解阶段归属：**Validator 24 条（70.6%，"任务未通过验证（重试 N 次）"）** / Executor 修复 8 条（23.5%，"修复失败：模型输出与原文相同"）/ Planner 2 条（5.9%）。Validator 细分：compile_error ~13（CS5001 无入口点、lawcheck_demo 语法错）+ 严格校验不一致 6（expected_output 不符）+ timeout 3 + not_found 2。**结论**：Planner/Executor 名义 100% 但真实成功率 68.8% → 瓶颈在验证层；批次1 完成门 + 批次2 类别已开始覆盖，但 compile_error 与严格校验不一致仍是两大头。**新发现**：Executor 修复死循环 8 条（模型输出与原文相同，重试满额浪费）→ 待实施"修复无进展熔断"（输出指纹与原文相同 → 提前终止判失败，不再重试满额）。
> **10-05 批次2：失败类别补洞（数据驱动，用户确认后实施）：** 查 metrics 失败分布：64 条失败中 **53 条 failure_category=null**（历史在 Categorize 引入前）+ 未归类样本集中在 3 类熔断/完成门消息。**补词表（只补实证存在的消息模式）**：`写入文件但验证未通过` → **write_unverified**（H2 熔断，写了文件但验证失败——目标可能已落地）；`未产生任何写入` → **write_not_attempted**（H4 只读循环）；`但该目标不存在`/`与用户指定不符` → **target_mismatch**（完成门 B，写了别处≠目标）；`写入被拒绝`/`写目标未落地` → 并入 **tool_rejected**（完成门 C）。优先级放 timeout 之后、max_steps 之前（H2/H4 消息原会被"任务终止"误归 max_steps，现在更精确）。**历史一次性迁移**：python 脚本（词表对齐 FailureCategorizer）重算 null 行 53 条 → 再重算 'other' 行 15 条（词表缺失导致的假 other，纯未知 other 重算后仍 other 无害）→ changed 7。迁移后分布：compile_error 17 / empty_response 12 / max_steps 9 / other 8（纯未知）/ write_unverified 6 / write_not_attempted 4 / timeout 4 / not_found 3 / runtime_error 1，**0 条 null**。tests 418→**422/422**（+4：三类新类别 + 完成门 C 归 tool_rejected）。注意：迁移只重算 null 与 other——已完成；后续新失败由 Categorize 自动打，词表漂移时用同样方式重算。
> **10-05 批次1 复测修复（真实任务积累暴露两处接线缺口，用户要求跑真实任务积累数据后修复）：** 跑 4 类真实任务（t1 沙箱写 hello.py=ok / t2 显式授权 notes.txt / t3b 无上下文幻觉建文件=诚实失败 / t4 读 PDF_RAG=ok）积累数据，暴露：**① 熔断区完成门顺序 bug**——fuseGate 在 H2/H4 之后，t2 实证 write_file X:/notes.txt 成功（目标落地）但 `type X:/notes.txt` cmd 正斜杠失败 → H2 先判失败（完成门没机会放行）→ metrics ok=0 误伤。修复：**熔断区 fuseGate 提前到 H2/H4 之前**——目标已落地 → 直接 FinalAnswer 收尾（验证命令失败是模型自选验证方式问题，目标达成才是用户要求）；目标缺失/写被拒未重试 → 判失败。**② 最大步数分支未接完成门**——t2 复测模型反复 type 验证循环达 20 步 → MaxSteps 直接 Error（目标已落地仍 ok=0）。修复：MaxSteps 分支前同样过完成门（三处接线：正常收尾 / 熔断 / 最大步数）。**t2b 复测完整成功**：write_file → `type X:\notes.txt`（模型改反斜杠）退出码 0 → 正常收尾 → metrics ok=1/susp=0。**t3b 数据无效记录**：测试脚本 use 与任务分属两个独立 CLI 进程（use 状态不跨进程），模型在沙箱幻觉建 src/BigService.cs（153 字节 vs 真实 272KB）→ H4 兜底诚实失败——测试设计缺陷非系统缺陷；真实验证工程增量需同一会话。**边界记录**：create_dir X:/（授权根本身）被 PathBoundary 子路径语义拒绝（无害，随后 write_file 成功）。tests 418 中一条"目标不存在"断言用 X:\test.txt 当样例被真实文件破坏 → 改用测试临时目录（TestTmp 下 never.txt）。**验证**：构建 0 错误；tests **418/418**；metrics 实证 t2b ok=1/susp=0。
> **10-05 批次1：验证层总盘点（完成门 + 显式写授权 + 可疑语义，用户确认后实施）：** 目标 = 让 metrics 成功率数字反映真实（"写了文件" ≠ "写到用户要求的目标"）。**背景**：E2E 暴露假完成根因——用户"在x盘根目录新建 test.txt"→ write_file 绝对路径被拒 → 模型改写 workspace 内相对路径 → `_everWroteFile=true` → 正常收尾 → metrics 记 success=1（546 任务 485 成功 88.8%，其中含此形态假完成）。**A 目标提取**（`Project/ProjectPathDetector.cs` +3 方法）：`TryGetWriteTarget`（通道1 完整绝对路径：盘符起点分片 + Directory/File.Exists + LooksLikeFile；通道2 自然语言 `TryExtractDiskRoot`"X盘根目录"→`X:\` + `TryExtractFileName`）+ `LooksLikeFile`。**B 完成门**（新建 `Project/CompletionGate.cs` 纯静态）：Evaluate(isWriteTask, wroteAny, writeRejected, target, targetIsFile) → CompletionVerdict?——写被拒且无写成功 → 判失败（非可疑）；提取到目标但目标不存在 → 判失败 + isSuspicious=true（"写了别处"=假完成嫌疑）。AgentLoop 接线：`_writeRejected/_writeTarget/_writeTargetIsFile` 字段 + 收尾区与熔断区（H2/H4 之后）双完成门。**C 写被拒未重试判失败**：写工具失败记 `_writeRejected=true`，收尾时过门。**D 模板可疑语义升级**：模板 Error 消息改"任务未真正实现…未实现用户需求"（触发装饰器可疑信号 `AgentLoopMetricsDecorator`）。**E 显式写授权（合并实施）**：`AgentRequest.ExplicitWriteTarget/IsFile` + 写三工具（WriteFile/EditFile/CreateDir）构造加 `explicitWriteRoots` 并集 + **敏感区兜底**（SensitivePathGuard.IsDenied 白名单通过也拒）→ DI AddAgent 透传 → CLI 任务分发区 `TryGetWriteTarget` 解析 → 非敏感即 `[显式授权] 检测到写目标` 注入；Orchestrator 顶层失败加 `[目标未落地]` 标注。**修复真实缺陷**：`.NET \w` 默认匹配 Unicode 中文——"新建一下test.txt"被整串吞入正则 → 改 ASCII 类 `[A-Za-z0-9_\\/.]+`（CJK 天然边界）+ 通道1 剥 CJK 顺序修正（"new.log 文件"不再被当文件名）；熔断区/收尾区 gateVerdict 变量名冲突 CS0136（改名 fuseGate）；tests 两处签名/参数修正（out _、EditFileTool old_string/new_string）。**验证**：构建 0 错误；tests 402→**418/418**（+16：目标提取 7（X盘根目录+文件名→文件级/X盘大写归一化/无文件名目录级/完整路径已存在/形似文件/目录/读语境不触发）+ 完成门 6 + 显式授权 4（授权根内放行/根外越界/敏感兜底拒绝/EditFileTool 一致））；**E2E 复测（X:\test.txt 场景）**：`[数据闭环] L0.5 写任务降级 small` → `write_file X:/test.txt 已写入（2 字符）` → `[完成] 我已在X盘根目录成功创建了test.txt…` → **X:\test.txt 内容"你好"实证存在**——授权→写入→完成门全链路落地（此前必被拒的路径现在真正落地）。**顺带修复**：CLI 主循环 input Trim()（管道 `echo exit` 尾随空白漏判被当任务执行——metrics 实证 "exit" 任务 ok=1）。metrics 实证：真实任务 ok=1/susp=0。**遗留边界**：管道 EOF 后 CLI 偶发把残留输入当任务（已修 exit 路径）；批次2 待办：完成门落地后重跑积累数据 → FailureCategorizer 词表补洞（ShippingService"任务未通过验证"前缀缺失）；批次3 Executor success 语义（content 非空 vs 编译通过）待选型。
> **10-04 输入路径自动识别（方案 A，用户确认后实施）：** 双根保护落地后 E2E 暴露体验断点——用户直接输入"读取一下 D:\My Projects\PDF_RAG 这个路径的项目"（未先 use）→ 沙箱模式 list_dir 越界拒绝。**新增 `Project/ProjectPathDetector.cs`**（纯确定性静态，无 LLM）：`TryExtractProjectPath`（按中文/半角标点分片（**不含 ':'——盘符冒号是路径组成部分，作分片符会把 X:\ 切开——首版实测 4 FAIL 后修正**）→ 正则 `[A-Za-z]:\\` 定位盘符起点截取 → Directory.Exists 校验；失败且尾部 token 含 CJK（中文语境词）→ 剥最后空格段重试 ≤3 次——有效路径空格段（PDF_RAG）不误剥）+ `HasReadContext`（读取/分析/看看/项目/工程/结构…）+ `HasWriteContext`（写/创建/生成/修改…，写优先）+ `TryGetAutoUsePath`（提取成功 && 非写 && 读语境 && `SensitivePathGuard.IsDenied` 敏感源头不触发）。**CLI 接线**：use 分支与自动识别共用新抽的 `ApplyUse(provider, options, rest, writeFlag, autoSource)`（设置工程根 + 扫描/缓存 + 可写提示 + 双根重叠自检；autoSource 非空 → 打印 `[自动识别] 检测到工程路径 X:\...（只读），已设置工程根。`）；主循环 use 分支后插入——`ProjectRoot is null && TryGetAutoUsePath` → 自动只读 use（显式 use 优先不覆盖；写任务不触发）。**验证**：构建 0 错误；tests 392→**402/402**（+10：提取 5（标点分片/含空格不误剥/尾部 CJK 剥离/无路径/不存在非 CJK 不剥）+ 语境 2 + 组合 3（读通过/写不触发/敏感不触发））；E2E 复测：原话"读取一下 D:\My Projects\PDF_RAG 这个路径的项目，分析一下"→ `[自动识别]` + 工程块 + 任务正常（不再越界）。
> **10-04 双根保护（读放开/写白名单/敏感源头拒，用户确认方案后实施）：** 目标 = Trae 式"给路径就干活"体验，解决三件事：读放开、写限制、prefix 假阳性。**读模型 = 黑名单排除（默认允许 + 例外硬拒）**：系统区（C:\Windows / Program Files / Program Files (x86) / ProgramData）+ 凭据区（%USERPROFILE%\.ssh/.aws/.kube/.docker/.config\gcloud/.git-credentials/.npmrc/AppData 两处 Credentials）在读取动作【之前】源头拒绝（返回"路径涉及敏感区"）——**内容物理不进 chatHistory / SQLite / 记忆库**；**写模型 = 白名单准入（WriteRoots = workspaceRoot ∪ projectRoot(工程可写)，其他一律拒绝）**。**关键洞察（用户提出）**：源头拒绝必须双管齐下——不只读工具，**run_command 也要堵**（模型 `type %USERPROFILE%\.ssh\config` 可把敏感内容打进 stdout → 进持久层）。**新增 `Project/PathBoundary.cs`**（逐路径分段比较，根治 StartsWith 前缀假阳性：`X:\proj2` 不再被 `X:\proj` 放行，`..` 跳脱拒绝）+ **`Project/SensitivePathGuard.cs`**（源头守卫，四条判定线：①长名逐段匹配 ②8.3 短名双向展开——GetLongPathNameW/GetShortPathNameW P/Invoke，`C:\PROGRA~1` 绕过被拦 ③符号链接/junction **逐段** ResolveLinkTarget——reparse point 在目录段上，只对整路径解析会漏（首版实测 FAIL 后修正），普通路径零开销 ④UNC 长路径前缀 `\\?\` 剥除；用户自定义 denylist = `workspace\sensitive_denylist.txt`（# 注释/空行跳过/trim 归一化，不存在静默，幂等加载）；启动自检 `FindWriteRootOverlaps`（工程根命中 denylist → WARN 防读写双拒误加）。**模式分档**：工程模式（用户显式授权 projectRoot）读默认放开 + 黑名单硬拒；**沙箱模式保持严格**（仅限 workspaceRoot，隔离语义不被读放开破坏）。**run_command 敏感拒**（跨模式生效）：命令串先 `Environment.ExpandEnvironmentVariables`（%USERPROFILE% 绕过）再正则提取路径 token（引号包裹/盘符/UNC）→ IsDenied；误伤边界（echo "C:\Windows" 字面输出）以提示兜底；工程模式既有路径边界换 PathBoundary。**写工具 3 个**（WriteFile/EditFile/CreateDir）白名单判定换 PathBoundary（行为不变、假阳性根治）；list_dir 加 500 条目上限（读放开防刷屏）；**已知边界（诚实记录）**：脚本内敏感读（python script.py 内部读敏感文件）不做——触发前提是用户明确要求读（用户意图即授权），下一轮单独做"写入内容敏感扫描"。**现有测试改不删**：唯一受影响的越界断言 `dir C:\Windows` 语义升级为敏感源头拒（断言改"敏感"）；其余 14 处越界断言不变（写仍限/沙箱仍限/run_command 既有边界保留）。**验证**：构建 0 错误；tests 372→**392/392**（+20：PathBoundary 5 + 敏感区 8（含 8.3 短名/符号链接实测、用户 denylist 追加）+ 写白名单 3 + run_command 敏感拒 2 + 启动重叠 2）；E2E 冒烟 CLI 启动接线正常（LoadUserDenylist 不炸、run_command 未被敏感拒误伤、顺带跑通完整日期差任务）。
> **10-04 Spectre P1+P2 最终态（用户确认执行 + 多轮真终端复验驱动修正）：** ①**P2 命令块面板化**（无头路径零改动，`if (renderer is SpectreRenderer spec)` 分支）：stats 默认子命令交互态概览 BarChart（总任务数/成功/失败/成功率 %/可疑，数据源 `GetSummaryAsync` 二次读取）；memory list/stats 交互态 Table；rollback 无参交互态备份 Table + MultiSelectionPrompt 多选恢复（空格多选/回车执行，按 rel 逐条 Rollback）。②**P1 实时刷新区 → 两次修正后弃用 LiveDisplay（关键教训）**：初版按"事件 Channel + 后台线程 + LiveDisplay.StartAsync 阻塞回调消费队列"落地（回调阻塞式 API 与事件流模型的解法）→ 用户真终端复验发现残留（漂浮竖线、绿框缺底边、"任务完成"分隔线有起点无终点）→ **第一轮误判为 Live 重绘帧叠加，改追加输出流**（每事件直接 Write）→ 用户再诊断出**真根因：Spectre 终端宽度误算（认为仅 80 列，120+ 列内容宽度算错导致边框错位）**，并实证 `AnsiConsole.Write(Panel)` 一次性输出（LocalAI 横幅/配置框）四边完整 → **最终修复**：`RendererFactory.Create(true)`（Spectre 初始化处）写入 `AnsiConsole.Profile.Width = Math.Max(80, Console.WindowWidth)` + 关键 Panel `.Expand()`（撑满宽度/内容自动折行/边框按实际宽度画）双保险；事件形态收敛为**仅路由决策/任务完成/任务失败用 Panel（黄/绿/红），规划/写入/工具调用/工具结果用彩色 MarkupLine**（防一次任务 20 框视觉碎片）；最终视图灰显区取 final 之前的事件（一个事件一个渲染入口，无重复）。**教训固化**：①任务事件是单向追加流，非原地刷新进度条——Live 类重绘 API 不适用；②终端 UI 边框类缺陷先怀疑宽度计算，不先归因渲染 API；③追加输出 + 显式宽度 + Expand 是无残留交互渲染的稳定组合。**验证**：构建 0 错误；tests 372/372 保持；E2E 无头冒烟 ×3 命令块无头路径逐字节兼容（面板仅交互态生效）。用户复验通过（"可以了"）。提交链：8a1f89e → d4e2c60 → 1a6abee → 2dc1e01 → 6ba122b → 71c4020。
> **10-04 Spectre P0：UI 层第一步（渲染器抽象下沉 Infrastructure + 无头降级，用户确认后实施）：** 用户探讨 UI 层方案（WPF/WebView2/终端增强三路线）后确认先做 Spectre.Console 终端增强 P0。**改哪些**：①新增 `src/Agent.Infrastructure/Rendering/` 四文件——`IRenderer.cs`（渲染契约：Banner/ConfigBlock/Info/Notice/Warn/Error/System/Prompt/ReadLine/RenderEventAsync 异步签名）、`RendererBase.cs`（Console 直出实现 + **纯函数 FormatEvent/Truncate/FormatToolArgs**——事件格式化唯一来源，tests 直接断言）、`PlainTextRenderer.cs`（无头路径，输出与旧 CLI 逐字节兼容）、`SpectreRenderer.cs`（交互路径：Panel 横幅/配置块 + 彩色事件流 + 任务开始/结束 Rule 头尾；**构造不碰 AnsiConsole**，非终端可构造可测）、`RendererFactory.cs`（Create(interactive)：false→PlainText / true→Spectre）；Infrastructure csproj 加 Spectre.Console 0.49.1。②Program.cs 装配一行 `RendererFactory.Create(!Console.IsInputRedirected && !Console.IsOutputRedirected)`（管道/重定向→PlainText，E2E 零影响）；横幅/配置块/输入/工作目录/自动编排/回滚/两处事件循环全走 renderer；**删除本地 RenderEvent/Truncate/FormatToolArgs**（下沉 RendererBase，消灭 CLI 单点）。③**顺手消灭双路径漂移实例**：orch 命令块（256 行）原本持有一份与 RenderEvent 重复的事件 switch 且 ToolResult 截断 500 vs RenderEvent 300 不一致——统一走 `renderer.RenderEventAsync`，截断统一 300。**实施中发现并诚实记录**：Spectre 0.49 LiveDisplay API 为回调阻塞式（Start/StartAsync 必须在回调内自驱动工作），与内核"外部事件流驱动渲染"模型不兼容——**实时刷新区 P0 未落地**（P1 方案 = 事件 Channel + 后台渲染线程 + LiveDisplay 阻塞回调消费队列，或升级 Spectre 后重评）；P0 以 Panel + 彩色事件流交付同等信息量。**验证**：构建 0 错误；tests 353→**372/372**（+19：工厂无头/交互选择 2、FormatEvent 七事件映射固化 9、Truncate/FormatToolArgs 边界 4、兼容抽样 3、PlainText 不抛 + 截断统一 1——防搬移改坏格式）；E2E 无头冒烟 ×2：stats 命令（Banner/ConfigBlock/命令块逐字节兼容）+ 写任务（[工作目录]/[路由]/[工具调用]/[工具结果]/[完成] 事件流格式全兼容，数据闭环 L0.5 降级 small 实证）。完整 e2e.ps1 4 用例未重跑（渲染层不触及路由/工程/回滚逻辑，372 断言 + 双冒烟已覆盖）。交互态（Panel/彩色事件流）需用户在真实终端人工验收。提交见 f083c68 后一提交。
> **10-04 全面系统评估（重新评估整个系统内核，用户要求）：** 当前基线：git main @ e94e2a8（**91 commits**）、构建 0 错误、tests **353/353**、工作区干净。**架构现状**：三层项目（Core 抽象 12 文件 / Infrastructure 主体 55 文件 14 模块 / Cli 入口单文件）；**单模型三角色**（law-unified 30B 一模型承载 small/large/agent_pro，Modelfile + 请求级 temperature 区分；classifier 仍 qwen2.5:1.5b）；10 个工具（原 6 + analyze_code/find_symbol/analyze_deps/remember）；**5 库**（sessions/metrics/project_context/agent_memory/symbol_index，metrics 三表 task_metrics+routing_decisions+tool_rejections + failure_category 归类）；**三层漏斗路由**（L0 查询词/L0.5 写任务直连 large/L0.5b 读任务数据闭环/L1 8 规则/L2 意图+确定性覆盖/L3 分发 + RoutingIntelligence 数据闭环调权 + TaskFeatureClassifier 特征归因）。**能力六层**：①路由（规则+意图+数据闭环）；②工程感知（只读/可写回滚/符号索引/依赖分析/类型冲突 P-D/工程缓存签名失效重扫/目标文件注入）；③跨任务连续性（run 级工作目录+任务归档记忆+失败教训注入+记忆治理）；④任务安全（交互拦截/路径边界双通道/白名单/P-D 拦截/大文件重定向/防假完成 H2-H4/循环熔断/项目名幻觉校验/模板检测）；⑤度量（metrics 三表+失败类别自动归类+特征归因+时代隔离+可疑成功明细）；⑥工具治理（分页续读/超时双轨/重定向收紧/命中行号引导）。**评估结论（强/弱/边界）**：**强**——安全层恒定兜底（P-D 100% 拦截、回滚跨任务隔离已审计、防假完成防线全生效）、工程图谱链路打通（find_symbol 增量刷新 + analyze_code 大文件实证命中 [BigService.cs:1503]）、数据闭环机制工作（L0.5 写任务降级 small 按真实历史调权）。**弱**——模型工具组合能力（大文件"定位→offset 读→edit_file"全链路 30B 不采纳指引，批 6 已承认边界；情况 A 工具循环无强制写检查；LLM HttpClient 600s 对 large 多轮不足）。**边界（已承认）**——①大文件符号定位→精确读取→修改超出当前模型能力（三通道拒绝消息/命中行号/操作指引已铺，模型不采纳即诚实失败）；②30B Executor 增量修改纪律弱（P-D 拦截消息附类型清单+动作指引后仍整体重写，安全层恒定兜底）；③ShippingService 类任务标注模型能力边界（E2E 3/4 基线固化）。**待推进（按序）**：3B 自动调权（routing_decisions 数据积累达标后启用）→ 情况 A 强制写（有依据才加）→ LLM 超时调大 → 性能组（num_ctx 动态/蒸馏，待确认）。**给开发者**：完整维护文档见 `docs/DEVELOPER.md`（模块地图/路由详解/工具清单/安全机制/测试体系/开发工作流/已知边界）。
> **10-04 增量引导收尾 + E2E 断言语义校准 + #66 判定完成（按用户建议顺序 ①②③ 推进）：** ①**E2E incremental 断言语义校准**（`scripts/e2e/e2e.ps1` 三态细分）：原把"安全拦截但模型能力失败"（P-D 拒后模型不换路、dotnet run 验证旧代码假完成）与"侧路假完成漏洞"（模型在工程根外建项目文件）混为一标——改为三态：`[安全拦截生效-模型未接入]` / `[安全拦截]`（无拦截信号但完成且 demo 未动=疑似漏洞）/ `[工程未接入]`。run10=3/4 验证。②**增量修改引导（#65）**：P-D 拦截消息从"不能做什么"升级为"不能做什么 + 下一步动作"（数据驱动）：`TypeConflictDetector.BuildExistingTypesHint` 增加 `targetRelative/targetExists`——目标已存在 → 附"请先 read_file {target} 查看当前内容，再用 edit_file 局部修改（不要 write_file 整体重写）"；目标是新文件 → 附"本文件只应定义清单之外的新类型；若需修改已有类型请在其所在文件内 edit_file 局部编辑"。WriteFileTool/EditFileTool 传目标文件状态。tests 308→**311/311**（+3）。**构建坑（字节级定性）**：run11/12 新引导不生效根因 = `dotnet build <项目> --no-incremental` 只重建 CLI 自身，**依赖项目 Agent.Infrastructure.dll 未被复制更新**（CLI bin DLL 旧）；`dotnet build LocalCodeForge.slnx --no-incremental` 全量重建后才更新。验证方法：python 字节级搜索 UTF-16LE 字面量（`是新建文件`=b'\x2f\x66\xb0\x65\xfa\x5e\x87\x65\xf6\x4e'，UTF-16 解码搜索曾误报 False——字符串编码细节不可靠，字节搜索为准）。**run13 实证**：拦截消息已含完整两段引导（工程已有类型清单 + read_file/edit_file 动作指引）——但**模型仍不换路**（看到引导后依然 write_file 整体重写被 P-D 拦）= 30B Executor 增量能力上限，非安全漏洞（安全层恒定兜底）。③**#66 多文件规划引导判定完成**：探查结论——Planner 阶段工程图谱引导**已由 10-03 #41 工程图谱增强（BuildEngineeringGraphBlock 注入已有文件清单+类型分布+影响面）+ CheckIncrementalPlanConflicts 计划校验完整落地且已生效**（run13 实证：增量任务已走 Orchestrator、Planner 输出合规=新增 1 + 接入 1、计划校验放行合规形态）；剩余失败集中在 Executor 生成环节（30B 收到拦截消息后仍整体重写），无新增代码空间，**基线固化：E2E 3/4 + 安全拦截恒定，ShippingService 类任务标注为已知模型能力边界**（用户确认方案 A）。提交：282bf8d（①）、e9b6fbd（②）。
> **10-03 改进批次：显存卸载实验 + 工程增量计划校验（用户确认顺序 2→1）：** ①**显存卸载实验**（请求级 num_gpu 基准 4/8/12：生成速度 16.6→18.2→18.8 tok/s 仅 +10-13%，瓶颈在 CPU 层；显存 2381→3939→5475MB，12 层余量 <700MB 有 OOM 风险）→ **Modelfile num_gpu 4→8**（+10% 速度、显存余 2.2GB、CPU 层减 4 缓解编译抢算力；blob 复用重建秒级，已 ollama create 生效）。②**工程增量计划校验**（`Orchestrator.CheckIncrementalPlanConflicts` 静态方法）：ProjectIncremental 特征任务 Plan 中"已有文件 ≥2"判定整体重写倾向 → **Planner 阶段拒绝**（走规划失败升级链，早拦截避免 Executor 白写一轮）——合法形态（新增 1 + 接入 1）已有 ≤1 放行；确定性集合运算非提示词。tests 294→**298/298**（+4：3 已有冲突/1 接入放行/全新增放行/大小写分隔符归一化）。ShippingService 复测：三层拦截网全链路验证通过（P-D 100% 拦截整体重写 + D2 重试校准 + 回滚干净），任务本身仍失败（30B 写幻觉字段）——能力边界已接受。
> **10-03 改进批次二：备份生命周期 + 记忆注入过滤（用户确认顺序 4/5）：** ③**备份生命周期**（`BackupService.PruneBackups(workspace, keepLastN=8)`）：任务结束后保留最近 8 个任务备份目录、删除更早的（防 .backup 无限增长；手动 rollback 仍覆盖最近 8 任务）；CLI 每任务收尾统一挂点。④**记忆注入特征过滤**（任务 5）：agent_memory 表新增 `feature` 列（TaskFeatureClassifier 特征类，兼容 ALTER）；ArchiveTask 归档 + RememberTool 决策写入时打特征标签；`Search(input, limit, feature)`——kind=task 归档记忆跨特征类**剔除**（强特征相关防污染）、kind=model 决策记忆跨类**降权**保留（score-1，决策可能跨任务复用）；`LoadRelevantMemory` 按输入特征类过滤注入。tests 298→**304/304**（+2 Prune + 4 记忆过滤）。E2E 脚本 `scripts/e2e/e2e.ps1`（UTF-8 BOM 必须，PS5.1 读无 BOM 会按 GBK 乱码）——4 用例（工程增量/单文件/多文件/读查）逐用例工程副本隔离 + 断言 + 汇总，落 `workspace\e2e\e2e_<ts>.log`。
> **10-03 D2 路由数据校准（用户确认实施）：** D1 归因接入路由层（纯数据/结构驱动，非提示词）。新建 `Routing/TaskFeatureClassifier.cs`（确定性关键词任务特征分类：工程增量/外部凭据/多文件/单文件/读查/其他——工程增量含 `.cs` 中新增方法等变体）与 `Routing/TaskMetricsAggregator.cs`（启动加载 metrics.db → 按特征类聚合 Total/Success/Rate/AvgMs/失败主因，30 秒缓存，失败归因词表与 D1 一致）。**Orchestrator 重试上限校准**：特征类历史样本 ≥3 且成功率 <50% → ExecutorMaxRetries 3→1（数据依据：工程增量类历史 0 成功，3 次重试纯浪费 CPU 推理约 9 分钟/任务；失败即回滚不再空转）——日志注记 `[D2] 特征类 X 历史成功率 Y%，重试上限校准 3→1`。CLI 接入：启动 Load() + `stats` 新增特征类归因块（每类成功率/样本/均耗时/失败主因，颜色分级）。tests 281→**294/294**（+13：8 分类断言含 `.cs` 中新增变体 + 5 聚合断言：工程增量 3 条 0 成功/失败主因=编译/类型冲突、外部凭据 2/2、单文件 1/1、无样本类 null）。提交 8a51e9c。
> **10-03 D1 失败归因 + 项目名幻觉校验 + hello 调查（用户确认实施）：** **D1 归因报表**（metrics 261 任务/86% 成功/34 失败逐条归因，脚本 q_d1.py）：失败集中三处——①工程增量任务 7（类型重复已 P-D 拦 + **run_command 项目名幻觉**新暴露）；②模型空响应 11（9-22~10-01 天气/IP 任务，免 Key 链路已缓解，10-01 后无新增）；③语义/修复同原文 4（30B 能力边界）。**项目名幻觉校验**：RunCommandTool 工程模式下命令中 `.csproj` 名不在工程根 → 拒绝 + 提示正确项目名（实测模型乱写 OrderSystem.csproj 导致 DiscountService/TaxService 失败）——`CheckProjectName` 确定性拦截，`dotnet run` 无 --project 不受影响。**hello.txt 调查**：2 次失败（10-01 20:48/21:21 模板检测误伤死循环，达到最大步数 20）→ 21:34 起连续 6 次成功——已由 10-01 `IsTaskExpectedOutput` 豁免修复，非当前问题。**验证**：tests 277→**281/281**（+4 项目名校验断言：幻觉名拒绝/真实名放行/无 --project 放行/沙箱不启用）；提交 e568e68。
> **10-04 批 6：大文件定位执行边界（① 重定向收紧 + ② 命中行号引导；三叉验收 → 第三种，承认边界）：** 前批实证"注入成功但执行断点"（模型 find_symbol 命中后仍 read_file 首页循环）→ 按用户三叉验收标准实施：**① 重定向收紧**：ReadFileTool 大文件放行条件 `hasPaging → hasOffset`（仅 limit 不算分页意图——实证模型传 limit 绕过定位；防绕过）。**② 定位后执行引导**：FindSymbolTool 命中返回追加数据驱动操作指引——「请用 read_file 携带 offset={行号-10}（limit=30）读取第 N 行附近，确认后调用 edit_file」——行号来自命中事实，非提示词。**E2E 第六轮三叉判定（X:\lawcheck_demo 加注释任务）**：链路全通（重定向拒绝 ×2 生效、find_symbol 命中 [BigService.cs:1503] + 指引注入），但模型**仍不带 offset**（指引明确给了 offset=1493，模型 read_file 只传 path 被拒 → 转 analyze_code/list_dir 循环）→ 熔断诚实失败。**判定：第三种情况成立（引导+事实都无效）→ 不加③（情况 A 强制写），承认边界**：30B 单模型本地推理下，"大文件符号定位 → offset 精确读取 → edit_file 修改"全链路超出模型当前工具组合能力；系统层已铺完路（拒绝消息/命中行号/操作指引三通道），模型不采纳即承认此边界，任务诚实失败（防假完成防线 H2/H4 持续有效）。**验证**：tests 346→**353/353**（+2 仅 limit 拒绝 + 命中指引断言 + 目标提取 5 例）。git 118e898。
> **10-04 批 5：大文件定位链路打通（A 路由词表 + B 定位引导 + C 假完成修复 + find_symbol 增量 + analyze_code 大文件，用户确认实施）：** 用户复测日志暴露大文件工程任务三缺陷 → 全部数据/工具层修复：**A 路由词表补全**：`IntentOverride.OperationVerbs` 缺单字「加」（「加一行注释」未命中 → 误判 SimpleQA）→ 补「加/注释掉」+ tests。**B read_file 定位引导**：分页头附「如需定位类/方法/符号，请用 find_symbol <名称> 或 analyze_code」。**C 假完成修复（H2 扩展 H4）**：熔断收尾加完成门槛——任务含写动词（IsWriteTask）但本轮未写过文件 → 判失败（防「要求写却没写」假完成）。**大文件首读重定向**：工程模式 + 大文件（>100KB）+ 无 offset → read_file 拒绝 + 引导符号定位（工具层行为约束）。**find_symbol 索引新鲜度**：修复实测缺陷——索引惰性建后从不刷新（工程变更后查陈旧索引未命中）→ Find 前调用 `BuildAndSaveIncremental`（P1-1 增量对比 mtime 只重扫变更文件）。**analyze_code 大文件支持**：符号地图是行级正则（不进上下文），200KB → 5MB 上限（重定向引导死路打通）。**looksLikeCodeRequest 词表对齐**：AgentLoop 独立词表同样缺「加」→ 补全 + 复用 `IntentOverride.IsWriteTask`（消双路径漂移）。**验证**：tests 343→**346/346**（+2 词表 + 增量刷新回归 + analyze_code 300KB 地图 + 重定向语义更新）。**E2E 复测（X:\lawcheck_demo 加注释任务）4 轮**：第 1 轮假完成（熔断以 read_file 收尾）→ 第 2 轮诚实失败+路由修复生效（L0.5 直连 large、熔断判失败）→ 第 3 轮**链路打通实证**（read_file 拒 → find_symbol 命中 [BigService.cs:1503] → analyze_code 出 5004 行符号地图）但模型**跑偏**（读小文件后 run 验证总结别的事，自然收尾假完成——根因 looksLikeCodeRequest 缺「加」绕过规则 1）→ 第 4 轮词表修复后模型仍跑偏 + **LLM HttpClient 600s 超时**（规则 1 强制写只在情况 B 文本分支触发，模型持续工具调用走情况 A 绕过；large CPU 推理多轮累积超时）。**剩余缺陷（待方案）**：① 模型跑偏——任务目标文件不注入（读无关小文件）→ 方案：工程模式从输入正则提取目标文件（数据驱动）注入首轮；② 强制写规则仅覆盖情况 B——情况 A 工具循环无强制写/目标聚焦检查；③ LLM HttpClient 600s 对 large 多轮不足 → 调大。
> **10-04 批 3：① test-tmp 清理 + ② runs 保留策略 + ③ 可疑成功明细（用户确认 1→2→3）：** **① test-tmp 清理**：tests 每次运行生成 test-tmp/\<pid\>/ 且无清理（实测 99 个 pid 目录累积）——tests 启动时清理 >7 天的旧测试目录（保留近期防并发互删；X 盘不落 C 盘 Temp 约定不变）。**② runs 保留策略**：CLI 启动 CleanOldRuns 参数 `maxCount: 50 → 10`（实测 51 个 run 累积的根因就是 50 过宽；keepDays=7 不变）——实证：启动 `[清理] 删除 41 个旧 run 目录`，51 → 11（保留最近 10 + 当前新 run）。**③ 可疑成功明细追踪**：is_suspicious 此前只显示计数（15 条）无可审计明细——stats「可疑成功」行后加明细块（GetRecentAsync 过滤 IsSuspicious && Success，最近 5 条：时间/模式/输入截断 + 「可 rollback 复查或重跑验证」提示），让"表面成功但未真正实现需求"可追踪。**验证**：构建 0 错误；tests 340→**341/341**（+1：可疑成功落库 + GetRecentAsync 过滤出明细）；冒烟 runs 清理实证。**探查降级**：备份膨胀候选经实测降级不做（主 workspace 无 .backup，真实任务备份量小——数据证明不膨胀）。
> **10-04 批 2：① read_file 分页续读 + ② run_command 超时可配 + ③ 失败类别自动归类（用户确认 1→2→3）：** **① read_file 分页续读**：此前 >200KB 直接拒绝（模型读不了大文件，工程大 .cs/长日志成盲区）。改为行级分页——schema 加可选 `offset`（1 起，默认 1）/`limit`（默认 500 上限 1000）；大文件（>200KB）或显式传分页参数时返回分页头「共 N 行，已返回第 X-Y 行，请用 offset=Y+1 续读」；小文件默认全文无头（向后兼容）。**② run_command 超时可配**：30s 对 dotnet build 大工程/长测试偏短（日志多次「>30秒」超时失败）——`TimeoutSecondsSandbox=30` / `TimeoutSecondsProject=90` 双轨，工程模式（_projectRoot 非空）自动放宽，消息动态显示实际超时值。**③ 失败类别自动归类（数据地基，与 3A/C 同族）**：新建 `Metrics/FailureCategorizer.cs`（纯静态词表打标，与 OutputVerifier 信号集对齐 + 工具/任务级信号；优先级高→低：external_credential(401/403/api_key) → compile_error(error CS/MSB/NETSDK) → runtime_error(Traceback/未捕获异常) → timeout → tool_rejected(白名单/越界/P-D/只读/空内容/交互/项目文件) → not_found → empty_response → max_steps → template_flag → other）；task_metrics 表加 `failure_category` 列（PRAGMA 检测 + ALTER 迁移；**修复真实缺陷：新索引 CREATE 若在 ALTER 前执行，旧库无列会 SQLite Error 1 崩溃——索引移到迁移后建**）；RecordMetric 时成功打空、失败按 error_summary 自动打标；`IMetricsCollector.GetFailureBreakdownAsync` 聚合失败分布；CLI stats 加「失败类别」块。**验证**：构建 0 错误；tests 324→**340/340**（+16：分页 4（大文件头/offset 续读/末页无提示/小文件无头）+ 类别词表 11 + 落库聚合 1）；冒烟：真实 metrics.db 迁移成功（failure_category 列存在、53 条历史失败归 other、CLI 启动健康检查全 OK 无崩溃）。**效果**：失败归因可聚合（不再靠人读 error_summary 原文），3B 调权可按类别；模型可读大文件工程代码。
> **10-04 批：A use 输入 BOM 清理补强 + B P-D 熔断语义化 + C 工具拒绝事件落库（用户确认顺序 A+B → C）：** **前置评估（WriteFileTool 影响面）**：metrics.db 506 任务 / 53 失败（10.5%），最近 20 条失败归因——① ShippingService 工程增量验证层失败 10 条（CS0117/CS5001/输出不符，P-D 未触发、模型写错内容，30B 增量能力上限已接受）② use --write 输入 BOM 乱码 8 条（5 条 GBK 错误解码 `锘縰se` 为修复前历史遗留、3 条 `﻿use` BOM 已剥离进入工程模式后任务本身 CS5001 失败——编码已非根因）③ 模型连续 3 次拒写文件熔断 1 条（P-D 拦截直接负面）④ 空答案 1 条。结论：write_file 拒绝覆写影响面小且正面，唯一负面 = 恢复路径断裂。**A · BOM 清理**：根因 10-04 已双管修复（CLI use 分支 TrimStart + E2E runner.py）；补强① 主循环入口统一 `TrimStart('\uFEFF')`（原来仅 use 分支，plan/stats/exit 带 BOM 同样失效）② 启动 `Console.InputEncoding=UTF8`（try-catch 静默）——根治「EF BB BF 被 GBK 解码成乱码 → trim 无效」路径。**B · P-D 熔断语义化**：WriteFileTool 新增 per-task `_pdRejectionCounts`（按 fullPath，OrdinalIgnoreCase）——同路径被 P-D 类型冲突拒绝第 2 次起（count≥2）返回确定性指令「禁止再次 write_file 整体重写 → 必须 edit_file 局部修改 / 新类型写独立新文件名」+ 保留类型清单——消除"3 连拒熔断白跑"（实证：被拒后模型不换 edit_file 反复 write_file）。**C · 工具拒绝事件落库（补度量盲区）**：新建 `Metrics/ToolRejectionStore.cs`（metrics.db 同库建 `tool_rejections` 表：tool/category/path/task_id/message 截断 200/created_at + created/category 索引；RecordAsync fail-safe 静默 + GetSummaryAsync）；类别 10 种：empty_content/interactive/out_of_bounds/project_file/pd_conflict/read_only/bad_args/not_found/drift/old_not_found/not_unique。WriteFileTool 7 处拒绝分支 + EditFileTool 10 处全部「记录后返回」（构造加可空 `ToolRejectionStore? rejections`——tests 直接构造不传 → 静默跳过，向后兼容）；DI 注册单例 + 两工具工厂 `sp.GetRequiredService`；CLI stats 加「工具拒绝」块（总数/按类别）+ 启动显式解析触发建表。**验证**：构建 0 错误；tests 318→**324/324**（+6 C 断言：Store 聚合 3 + 带 store P-D 落库 1 + 不带 store 静默 1）；冒烟 CLI 启动 metrics.db 建表确认 `tool_rejections` 存在（6 列）。**既定路线**：观察 2-3 天真实任务积累 routing_decisions/tool_rejections → 3B 失败率自动调权（routing_decisions 表已就位）；D（diff 示例）已定低优先级跳过（熔断消息已含等价指引）。
> **10-03 P-D + 遗留清理批次（用户确认实施）：** **P-D 写前类型冲突检测**——ShippingService 类工程增量任务 3/3 稳定失败根因（30B 全量重写把已有 `class Order` 重复定义进 OrderService.cs → CS0101）。新增 `Project/TypeConflictDetector.cs`（纯正则 + 文件扫描，毫秒级，无模型调用）：提取类型定义（class/interface/enum/struct/record，剥离注释/字符串防误报），检测新内容是否定义工程其他文件已有类型；`WriteFileTool`/`EditFileTool` 在工程可写模式下写入前拦截（新建或已存在文件均检测，目标文件自身已有类型=partial 语义放行）→ 返回「类型重复定义：X（已存在于 文件）」错误。**遗留清理**：RoutingRules.json 删 `complex_cloud`（85，与 complex_local 150 语义重叠的死代码）→ 规则 9→8 条；`AgentOptions.EnableOrchestration` 10-01 已删除字段（§12 第 4 条标注已解决）。**验证**：构建 0 错误；tests 267→**276/276**（+9 P-D 断言：write 拒绝/新建拒绝/partial 放行/无冲突放行/edit 拒绝/ExtractTypeNames 防注释字符串误报）；CLI 冒烟「RuleEngine 已加载 8 条规则」+ 模型健康检查 OK。
> **10-03 回滚安全批次（P-A/P-B/P-C/P-E + 跨任务测试 + dry-run + §12 审计表，用户确认方案后实施）：** **复测发现 5 问题**：P-A🔴 RollbackTask 取"最近备份"→ 任务多次写入时回滚到任务自身中间坏状态（实证 OrderService.cs 回滚后仍含重复 Order 类）；P-B🔴 备份/新文件标记无 taskId 隔离（A 成功改动会被 B 失败回滚误伤）；P-C🟡 stamp 秒级粒度同秒覆盖备份；P-D🟡 30B 增量修改纪律弱（全量重写多类进单文件）；P-E🟡 demo 工程被任务污染（残留 OrderSystem.csproj + 重复类 + 编译失败）。**修复**：BackupService 全面改造——备份/标记/回滚/清理/列出全链按 taskId 隔离（`.backup/<taskId>/<stamp>/<rel>` + `.backup/<taskId>/.new/<rel>`），RollbackTask 取**最早**备份（P-A）、stamp 毫秒粒度（P-C）、ClearNewMarkers 成功收尾清本任务目录、新增 `DryRunRollbackTask`（rollback --dry-run 预览不执行）；WriteFileTool/EditFileTool 构造注入 taskId（每任务 DI 容器）、CLI 回滚命令按最近任务 taskId 作用域 + rollback all/--dry-run。**P-E**：demo 恢复干净（删 OrderSystem.csproj、重写污染的 OrderService.cs、编译运行通过）。**验证**：tests 258→**267/267**（+H1 三断言 + P-A 最早备份 + 跨任务三场景 + dry-run 三断言）；端到端：工程模式失败任务真实回滚 `[回滚] 2 文件恢复、1 新建清理` + 回滚后 demo 编译 0 错误 + `rollback --dry-run` 展示影响面正常。§12 已落"回滚安全交互矩阵（已审计）"。
> **10-03 隐患批次：H1 回滚跨任务误删修复 + H2 弱验证防护 + H3 工程模式默认严格校验（全面复盘后用户确认）：** **H1（数据丢失级）**：P0-1 的 `.new` 标记无任务隔离/无成功清理——同会话任务 A 成功新建文件（标记残留）会被后续任务 B 失败回滚**误删**。修复：`BackupService.ClearNewMarkers`（删整个 .backup/.new 目录）+ CLI 收尾成功分支调用（成功=改动保留、标记失去意义；失败路径 RollbackTaskFull 本就删标记）。**实证**：H1 跨任务测试 3 条（A 文件保留 / B 文件清理 / 标记清空）。**H2（弱验证）**：循环熔断收尾"工具已验证（read_file）"——写任务在验证未通过时也可能被判完成（P01 实证：模型写完 run_command 失败 → read_file 重复熔断判完成）。修复：AgentLoop 新增 `_everWroteFile`（write/edit/append 成功）+ `_lastRunCommandOk`（run_command 结果）——熔断时若"写过文件且最近 run_command 未成功"→ **判失败**（防假成功）而非假完成；纯读/分析任务（未写文件）维持原熔断收尾。**H3（工程模式默认严格）**：expected 严格校验默认仅在显式开关下生效——工程模式（ProjectRoot 非空）改动验证要求逐项兑现。修复：Orchestrator 严格判定条件 = `StrictOutputVerification || ProjectRoot is not null`（普通生成保持宽松；只读工程任务无 run_command 不触发）。**验证**：构建 0 错误、tests 255→**258/258**（+3 H1 跨任务）；端到端：工程模式失败任务真实触发自动回滚（"[回滚] 3 个文件恢复备份、1 个新建文件已清理"——P0-1 链路实证），回滚后 demo 恢复到含此前成功改动的状态（备份语义正确）；H3 判定由编译 + P0-2 严格单元覆盖（本任务失败为模型修复能力边界：编译错误理解不足致修复输出相同，非本轮回归）。
> **10-03 复盘改进批次：P0-1 工程失败自动回滚 + P1-1 符号索引增量失效 + P0-2 expected 严格校验开关（用户确认方案后实施）：** **P0-1 工程模式失败自动回滚（数据安全闭环）**：此前 `BackupService.RollbackTask` 仅 CLI 手动 `rollback` 命令，use --write 任务失败后工程改动不还原。新增：`BackupService.MarkNewFile`（工程可写模式写**新建**工程文件 → 标记 `.backup/.new/<rel>`，备份仅覆盖已存在文件、新文件需单独追踪）+ `RollbackTaskFull`（恢复全部备份 + 删除 .new 标记新文件，返回 (Restored, RemovedNew)；stamp 遍历排除 `.new` 防把空标记当备份恢复）；`WriteFileTool` 工程可写分支接 MarkNewFile；CLI 收尾（orchestrator/agent 双路径）任务失败（无 FinalAnswer）且工程可写 → `AutoRollbackOnFailure`（恢复+清理，纯确定性无模型，失败尽力而为不抛）。**实证**：成功任务改动保留不回滚（OrderService.CalculateTotal2 端到端保留）；回滚单元覆盖（恢复备份/清理新文件/.new 不残留）。**P1-1 符号索引增量失效（mtime）**：此前 symbol_index.db 惰性全量重建、文件变更后索引过期。新增：`SymbolIndexer.EnumerateFiles`（抽公共文件枚举，含 8MB/300 上限）+ `BuildFile`（单文件扫描，Build 复用）；表加 `file_mtime` 列（Initialize 迁移 ALTER ADD，旧库兼容）；`BuildAndSave` 全量构建也存真实 mtime（增量对比基准——否则旧行 mtime=0 导致增量全重扫，首版测试实证 2 文件全重扫后修复）；`BuildAndSaveIncremental`（磁盘 mtime vs DB 对比，仅重扫变更/新增文件 + 删除磁盘已消失文件记录，返回 (Total, Rescanned)）；analyze_deps 已有索引时切增量重建。**实证**：改 A.cs 增量仅重扫 1 文件、新符号可见、未变文件保留、删文件记录清除（6 条测试）。**P0-2 expected_output 严格校验开关**：此前宽松校验（任一 stdout 行被 expected 包含即通过）→ "假通过"（预期缺失仍判成功）。新增 `OutputVerifier.IsTaskExpectedOutputStrict`（expected 每一非空行须在 stdout 行集归一化后整行精确匹配，顺序无关）+ `AgentOptions.StrictOutputVerification`（默认 false 保持现状）+ Orchestrator 验证阶段 strict 分支（模板豁免后独立判定）。**实证**：宽松/严格差异（缺行宽松过、严格拒）、空 expected 不适用、顺序无关（4 条测试）。**验证**：构建 0 错误、tests 242→**255/255**（+13：P0-1 回滚 3 + P1-1 增量 6 + P0-2 严格 4）；端到端：工程模式成功任务正常完成且改动保留（P0-1 不误伤）、demo 连续任务索引增量无碍。**P2-1（失败教训自动入库）核查后确认已存在**（CLI `ArchiveTask` + `TaskMemoryExtractor` 任务完成/失败自动写 kind=task 记忆）——本轮无新增。
> **10-03 复盘执行：① 补 agent_pro 30B 样本 + ② 工程图谱端到端验证（发现并修复 2 个真实缺陷）：** **① 样本补足**：2 个 orchestrator 任务（库存/客户管理系统，各 4 文件 + dotnet run 验证）全过，**30B Planner 累计 5/5 稳定**，agent_pro 30B 时代样本 2→**4**。**② 工程图谱端到端验证（X:\lawcheck_demo 测试工程，use --write 可写模式）**：任务"新增 DiscountService/TaxService 接入 OrderService + 编译运行验证"。**三轮暴露 3 个问题、修复 2 个真实缺陷**：(a) **use 语法**：`use --write <path>`（--write 在前），`use <path> --write` 会被当路径——文档补记；(b) **图谱块缺"已有文件清单"** → Planner 无视工程已有文件、全量重建 5 文件（Order.cs/Program.cs 被重写、Program 逻辑跑偏）→ 编译失败——**修复**：BuildEngineeringGraphBlock 增加"已有文件清单"（EnumerateExistingFiles：工程根递归代码/工程文件、过滤 .git/bin/obj/node_modules/workspace/.vs，前 40 个），提示"已存在勿整体重写、修改用 edit_file 增量"——**修复后实证**：Planner 增量规划生效（graph4 规划仅 2 文件：TaxService.cs 新增 + OrderService.cs 修改）；(c) **RunCommandTool 工程模式 cwd 错误**：工程模式下文件写入工程根但 run_command 仍在 run 目录执行 → `dotnet run` 找不到项目（"找不到要运行的项目"）→ 验证必败——**修复**：RunCommandTool.WorkingDirectory = `_projectRoot ?? _workspaceRoot`（工程模式 cwd 用工程根，dotnet run 自然找到工程 csproj；非工程模式保持沙箱）——**一处修复 AgentLoop + Orchestrator 双路径受益**——**修复后实证**：graph5"OrderService 新增 CalculateTotal（调用 TaxService.CalculateTax + DiscountService.CalculateDiscount）"工程模式任务 **验证通过：dotnet run**，模型正确理解工程已有类型并增量修改（OrderService 注入两个服务字段 + 构造函数 + 方法）。**验证**：构建 0 错误、tests 242/242（图谱块增强无测试变更——已有文件清单为端到端行为）；demo 工程保留为工程模式活样本（含 Order/TaxService/DiscountService/OrderService/Program + csproj）。
> **10-03 ① agent_pro 时代隔离 + ② 工程图谱增强（依赖分析）：** **① 时代隔离（数据如实反映 30B）**：新增 `IMetricsCollector.GetEpochBreakdownAsync(serviceId, epoch)`（SQL 按 started_at 拆当前时代 ≥epoch / 历史 <epoch，两段独立聚合）；`AgentOptions.LawUnifiedEpoch`（可配置，Program.cs 实证设为 **2026-10-02 23:00**——law-unified 创建实证 5d829c5=22:58 / c1f4069=23:12，此前试设 15:30 会把 30B 工程问答任务误归 4B，已修正）；CLI stats 输出"agent_pro 时代隔离: 30B 时代 n 条 / 成功率 | 4B 历史 n 条 / 成功率"。**实证结果**：law-unified 时代 agent_pro 仅 1 条（员工系统 OrchestratorPlanner 子指标 23:39，B+ 后首个 orchestrator）100%；4B 历史 20 条 35%——**30B 能力不再被 4B 污染，样本随 orchestrator 任务自动增长**。**② 工程图谱增强（多文件规划/依赖分析）**：新增 `DependencyAnalyzer`（确定性：符号索引的类型级符号 + 词边界引用扫描 → 文件依赖边 File→DependsOnFile，无 LLM，粗粒度标注）；`SymbolIndexStore.GetAll(projectRoot)`；新工具 **`analyze_deps`**（path → 直接依赖 + 反向依赖=改动影响面，复用 symbol_index.db 惰性索引）；Orchestrator 规划阶段附加**工程图谱块**（类型分布摘要 + 用户提及文件的改动影响面）供多文件规划。**验证**：构建 0 错误、tests 232→**242**（+10：时代隔离拆分 4 + 依赖边/反向依赖/工具端到端 6）；端到端：stats 时代隔离输出实证、工程模式依赖分析任务全过（analyze_deps 正确给出 OrderService→Order.cs 直接依赖 + Program.cs 反向依赖，模型基于工具结果总结准确）。
> **10-02 ②③④ 顺序完成（prefix cache / 数据闭环 / 记忆治理收尾）：** **② prefix cache 保活端到端验证**：API 直测同前缀 3 次请求，第 2/3 次 `prompt_eval_cached_count=184/206`（~90% 前缀 KV 命中稳定）；CLI 同会话两任务耗时 23.9s→17.5s（-27%，模型常驻无加载）。结论：`OLLAMA_KEEP_ALIVE=24h` + KV 缓存正常，CPU 推理每请求省 ~180 token 前缀预计算。**③ 数据闭环判定：不降级（机制正确工作）**——small/tier=Rule 写任务 n=23 成功率 96% 平均 99.4s vs large/tier=Rule n=22 成功率 95% 平均 35.5s：三条件中"省时"不满足（small 慢 2.8 倍，同模型下 large 更快），TrySuggestDowngrade 如实不触发，保持 large 直连写任务。**④ 记忆治理收尾（用户确认）**：AgentOptions 新增 `MemoryPruneDays=30 / MemoryPruneMinHits=1`（minHits=1 语义="仅 0 命中可清理"，0 会让 `hit_count<0` 永假致治理失效——已修正默认值）；AgentLoop.RunAsync 任务启动时自动 `PruneExpired(30,1)` + 日志"[记忆治理] 过期清理 N 条"；纯数据层无模型参与。验证：构建 0 错误、tests 227→**232**（+5 治理断言：默认值/过期清理/有命中保留/无命中删除）；端到端任务正常、治理无副作用（记忆库 41 条近期归档未被误删）。
> **10-02 agent_pro 专项诊断 + B+ 子调用指标（样本自动积累）：** **诊断结论（数据驱动）**：agent_pro 35% 成功率（20 任务 7 成功）是 **4B 历史样本污染**——按 started_at 拆分：4B（qwen3.5:4b）19 条成功率 31.6%（空响应 11 条全集中在天气类外部任务，已被上下文治理 + 4B→30B 升级解决）；**30B（law-unified）4 条 4/4 全成功**（工程问答/读取/封装 web_serch 工具）。**A 主动补样本**：跑 3 个 orchestrator 复杂任务（订单/待办/图书，各 4 文件 + dotnet run 验证通过）——30B Planner 3/3 稳定；但发现 orchestrator 内部调用（Planner=agent_pro、Executor=small）**不记 task_metrics**（只记顶层），样本未增。**B+ 落地（用户确认）**：Orchestrator 注入 IMetricsCollector + 新增 `RecordSubMetricAsync`（纯记录、失败容忍）——Planner 调用后记 `service_id=agent_pro / routing_tier=OrchestratorPlanner / mode=orchestrator_sub / success=plan 有效`；Executor 每文件生成后记 `service_id=small / routing_tier=OrchestratorExecutor / success=content 非空`。**效果**：每次复杂任务自动 +1 agent_pro 样本、+N small 样本（每文件）——30B Planner 能力可观测、可参与数据闭环调权。**验证**：构建 0 错误、tests 227/227；端到端员工管理系统任务全过，子指标落库实证（agent_pro/OrchestratorPlanner success=1 · 32.2s；small/OrchestratorExecutor ×4 success=1），agent_pro 样本 20→21、small 39→43。
> **10-02 单 tag + 请求级 options 落地（§14-3y 方案 3，取代任务粒度清场）：** 用户在三 tag+清场方案之后拍板"单 tag + 请求级 options（每任务参数运行时注入）"。**核心**：三个角色不再用独立 tag，统一指向 **`law-unified`**（30B Custom Model，FROM anarko:30b + num_gpu 4 + 32K + q8_0 KV，Modelfile temperature 0.5 仅兜底）——**磁盘/内存各一份 19GB 常驻，无 tag 切换、无重载、无双驻**；角色差异由**请求级 temperature 注入**：`ModelEndpoint.Temperature`（double? 新字段）→ AgentOptions 新增 `GetEndpoint(serviceId)` + `GetEffectiveTemperature(serviceId)`（端点优先、未配置回退全局 0.2）→ AgentLoop 构造 OpenAIPromptExecutionSettings 时按当前 serviceId 取温度（small 0.3 / large 0.7 / agent_pro 0.5；classifier 走全局）。PlannerService/ExecutorService 硬编码 0.1 保留（规划 JSON 确定性，请求级覆盖 Modelfile）。**移除 OllamaStopService 任务粒度清场**（组件文件 + DI 注册 + AgentLoop 清场接线 + using 全删——单 tag 无多 tag 共存问题，清场反而破坏常驻）。**配置**：Program.cs + AgentOptions.cs 三角色 Model 全部 = law-unified + 各自 Temperature（classifier 保留 qwen2.5:1.5b；dashscopeKey 仅预留）。**验证**：构建 0 错误、tests 216→**227/227**（新增 11 条：GetEndpoint 映射 5 + 生效温度 3 + 回退 2 + 三角色同 tag 1）；端到端复测 3 任务全过（写×2 → law-unified + 读纯工具），`ollama ps` **law-unified 单驻一份 19GB（91%/9% CPU/GPU，UNTIL 24h）**——无清场日志、无重载、无双驻。旧 tag law-small/law-large/law-agentpro 保留在 Ollama（同 blob，可回退）。
> **10-02 单模型三角色落地：30B 一模型承载 small/large/agent_pro（§14-3y）：** 用户拍板"Qwen3-Coder-30B-A3B 单模型承接三角色，Modelfile 各自配置，不额外拉取模型"。**背景澄清**：此前 Small=qwen3-coder-flash-opt 实为 30B（FROM anarko/qwen3-coder-flash:30b），与 Large 的 A3B（UD-Q4_K_XL）同一底层权重——"双 30B 常驻"实为同模型两 tag 各 19GB。**Custom Model 多名称实测**：磁盘 blob 共享成立（创建 law-* 后 blob 增量 KB 级）；**但多 tag 同时常驻不成立**——双 tag 共存 FreeGB 掉 0.6（内存爆，已止损）。**显存边界实测**：3060 6G（Doubao 占 ~1.3GB），num_gpu=12/10/8+FA 全 CUDA OOM；**4 层可跑**（91%/9% CPU/GPU，19GB RAM，统一层数）；0 层纯 CPU ~18 tok/s。**MAX_LOADED_MODELS=1 实测未生效**（Ollama 0.35 驱逐按 VRAM 预算，4 层只占 ~1GB 不触发；RAM 不管）。**落地（用户选方案 1）**：新建 `Ollama/OllamaStopService.cs`（任务粒度清场：每任务开始 stop 全部 law-*，POST /api/stop，404/400/异常全容忍，10s 超时；只清 law-* 开头模型）——DI 注册（AgentLoop 前）、AgentLoop.RunAsync 顶部调用（日志"任务粒度清场：已清理 law-* 模型，保证单驻。"）；**三角色 Custom Model**：law-small（temp 0.3）/law-large（temp 0.7）/law-agentpro（temp 0.5），均 FROM anarko:30b + num_gpu 4 + num_ctx 32768 + q8_0 KV（Modelfile 源 %TEMP%\lawcheck\Modelfile.law-*）；工作台配置 Small/Large/AgentPro → law-*（Program.cs + AgentOptions.cs 同步，classifier 保留 qwen2.5:1.5b，云端 dashscopeKey 仅预留）。**端到端复测**：构建 0 错误、tests 216/216；三任务（写×2→law-large、读→纯工具）+ orchestrator 复杂任务（Planner-Executor 全过）；ps 全程单驻 1 个 law-*（无双驻、无内存爆）。**负载观察**：写任务直连 large（L0.5）、orchestrator 用 large、读任务纯工具无模型调用——law-small/law-agentpro 实际模型调用集中于轻问答与升级链兜底。
> 状态更新（截至 10-01 22:10）：15:47 修复编译错误（构建 0 错误）；18:10 修复「规则 F 冲突」打通 C# 多文件；20:30-22:10 工程感知 P0-P3 全量落地 + 路由 A+B + 终止条件 1+2+3（见下方状态块）；
> **10-01 跨任务文件连续性 A+B+C（§14-3w 补足）：** 用户测试日志暴露"接着更改上一个脚本"失效（任务2 read_file pyst.py 文件不存在 → 模型被迫重建而非更改，自定义改动丢失）。**方案 A（核心）· run 级工作目录**：一次 CLI 运行 = 一个工作空间——工具根从 task_&lt;id&gt; 子目录改为 run 根（workspace），task 目录不再创建（避免空壳误导 list_dir），taskId 仅作记忆归档溯源；打印改"[工作目录] &lt;run&gt;（任务归属 task_xxx）"；orchestrator/agentLoop 两分支 RunAsync/AgentRequest 全部传 run 根。**方案 B · 写动词词表补全**：IntentOverride.OperationVerbs 加 写/更改/改/改为/改变/变更/换成/重写/生成/追加/覆盖/清空/格式化/打印/输出（"写一个…脚本""更改这个脚本"此前未命中 L0.5 直连 large）。**方案 C · 豁免归一化**：OutputVerifier.IsTaskExpectedOutput 比较前 NormalizeForMatch（小写 + 非字母数字折叠空白 + 折叠连续空白，中文保留）——"Hello, World!" ↔ "hello world" 匹配，模板误判根治（此前 pyst.py hello 输出被 [检测] 模板误判）。tests 136→143（B 词表 5 项 + C 归一化 2 项）143/143。**端到端复测实证（abc_verify）**：任务1 "写一个名为 pyst.py 的脚本" → `[工作目录]…（任务归属 task_028c7232）` `[L0.5-写任务] 写任务 → 直连 large` → write_file → 完成（无模板误判）；任务2 "更改 pyst.py 脚本…" → 同 run 根 `read_file pyst.py → print("Hello, World!")` 命中 → 完成"已改为判断整数偶数的脚本：新增 is_even 方法…运行验证通过"——**真更改而非重建**。已提交 02522bf。
> **10-02 prefix cache 保活确认（性能方案 Step 1，仅此项）：** 用户确认只做保活（Ollama 层，非提示词/代码）。**现状排查**：OLLAMA_KEEP_ALIVE=30m（偏短，模型卸载→前缀缓存失效）；Ollama 为 Desktop 托盘形态（`ollama app.exe` 自管 `ollama.exe serve`，Get-Service 无 ollama 服务）。**根因链（反复验证得出）**：所有 Start-Process 拉起的进程继承"常驻 PowerShell 进程"的旧环境快照（30m），注册表改 24h 不进进程；且手动起的 serve 端口被托盘 serve 占用→请求打到托盘（30m）。**解法**：同一命令内 `$env:OLLAMA_KEEP_ALIVE='24h'` 后重启托盘（`ollama app.exe`，继承 24h 再拉起 serve）；注册表 User=24h 已持久化（后续任意新进程/重启均 24h）。**验证**：`ollama ps` UNTIL 由 `29 minutes from now` → **`24 hours from now`**（显式 API keep_alive=24h 请求也验证了请求参数通道可用，但 SK /v1 兼容端点不传该参数，故走服务默认 env）。**复测**：写任务一次完成（list_dir→write_file→run_command→完成，无空响应/模板误判）。**用户拍板：不使用云端模型（云端仅预留未来兜底）**——Large 角色已由 DashScope 云端 qwen3.8-27b 切为**本地 Ollama `modelscope.cn/unsloth/Qwen3-Coder-30B-A3B-Instruct-GGUF:UD-Q4_K_XL`**（Agent.Cli/Program.cs + Agent.Core/AgentOptions.cs 默认值两处同步；dashscopeKey 变量保留预留，缺 Key 提示改为"云端兜底未启用"中性语义）。**端到端复测（local_large_verify）**：写任务一次完成（write_file→run_command→完成，无空响应/模板误判，Stock 类实例总价正确），全程 132.8s（含首次加载 18GB + CLI 编译）；`ollama ps` 确认 30B-A3B 加载且 **UNTIL=24 hours（保活生效）**，99%/1% CPU/GPU。后续任务模型常驻 + 前缀缓存，预计显著更快。其余性能方案（GPU offload 调参/量化下探等）用户明确不做。
> **10-02 三项候选落地：工程级测试自动执行 + 多文件修改规划 + 工程图谱（§14-3w 收官；性能方案另给待确认）：** 用户拍板实施（性能仅给方案）。**① 工程级测试自动执行**：新建 `Project/TestRunnerService.cs`（确定性 Process，无 LLM）——DetectTest 四级检测（*.Tests.csproj 含 xunit/nunit/mstest → dotnet test；console 断言形态 → dotnet run；pytest.ini/pyproject 含 pytest → pytest -q；package.json "test" → npm test）+ RunAsync（150s 超时杀进程 + Summarize 提取 passed/failed）；**AgentLoop 工程完成门正式接入**（48451eb 的 TestProjectDetector 此前为死代码）：工程模式 + 本轮写过文件 + 检测到测试入口 + 每任务至多自动跑一次（autoTestRun）→ 自动运行测试，结果以 run_test Tool 消息注入 chatHistory，模型基于真实结果收尾/修复（3 轮防死循环保留）。**② 多文件修改规划 = 任务级回滚**：BackupService 新增 `RollbackTask(workspaceRoot, projectRoot)`（每个相对路径取最近 stamp 备份 = 上次写入前状态，返回恢复数）；CLI `rollback` 无参 = 任务级全量回滚（列备份改为提示行），`rollback all` 同义，`rollback <rel>` 单文件。**③ 工程图谱**：新建 `Project/SymbolIndexer.cs`（确定性正则跨文件提取 class/interface/enum/record/struct + 方法 + 属性 + Python class/def 缩进判方法；排除 git/bin/obj/node_modules/workspace/.vs；上限 300 文件/8MB）+ `SymbolIndexStore`（SQLite symbol_index 表 project_root+symbol 复合主键 + idx_symbol，HasIndex/BuildAndSave/Find）+ `Tools/FindSymbolTool.cs`（`find_symbol name=<符号>`，首次调用惰性构建索引、SQLite 持久化秒查，无工程模式拒绝）；DI 注册一行。**修复两处真实缺陷（端到端暴露）**：①首次端到端"RoutingIntelligence 未命中"——CSharpTypeRe 修饰符组 `(?:...)*\s*` 无法跨空格匹配 `public static class`（单修饰符测试过、多修饰符漏检）→ 改 `(?:(?:mods)\s+)*` 跨空格重复；②修 ① 时把 `(class|...)` 捕获组误改非捕获组 → Groups 错位（kind 取类名/名字空，Order 全部类断言崩）→ 还原捕获组；③方法正则加 NonMethodKeywords（if/for/while 等控制流不再误记为方法）。tests 192→216（TestRunner 检测 4 + 任务级回滚 3 + 符号索引 6 + find_symbol 4 + 无工程拒绝 1 + 多修饰符/控制流 5 + 探针 1）。**端到端实证（find_symbol_verify3）**：真实工程"找到 RoutingIntelligence 类定义"→ 模型第一步 find_symbol 命中（1 处 [文件:行号] class）→ analyze_code 结构 → read_file 全文 → 完整收尾——工程图谱链路真实生效（此前两步 read/list 兜底）。
> **10-02 编辑冲突检测 + 工程完成门 + 只读防幽灵（完成门专项复测修复）：** 用户拍板最后两项候选落地。**① 编辑冲突检测**：EditFileTool 找不到 old_string 时查最近备份（BackupService.ListBackups 按 rel 匹配）——若有备份且当前内容与备份不一致 → 返回"自最近备份后内容已变化（漂移）"诊断（外部/前一任务修改），提示 read_file 确认或 rollback；无备份保持原错误。**② 工程完成门**：新增 `Project/TestProjectDetector.cs`（确定性检测：tests/test 目录 / *.Tests.csproj / pytest.ini / pyproject 含 pytest / package.json test script）；AgentLoop 记录本次写工具成功修改的工程文件（_modifiedProjectFiles），step 尾部 HasRunTestCommand（run_command 历史含 dotnet test/pytest/go test/npm test/mvn test）——修改过代码 + 有测试项目 + 未跑测试 → 正常交付前强制一轮"运行相关测试验证或说明原因"（3 轮不强求防死循环）；**熔断收尾同样附完成门提示**（确定性附加，不强制重试）。**③ 工程只读防幽灵（复测暴露）**：工程模式未授权（use 未带 --write）时，写工具若相对路径在工程根内存在对应文件 → 拒绝并提示"工程只读模式，请 use --write"（此前误报"文件不存在"→ 模型写任务目录幽灵文件 + 误建 csproj）。tests 181→192（TestProjectDetector 5 项 + 冲突诊断 2 项 + 只读拒绝 3 项 + 任务目录不受影响 1 项）192/192。端到端：工程只读写任务 → edit_file 被拒并指引 --write，无幽灵文件；熔断收尾无修改不附完成门提示（条件正确）。已提交 48451eb。
> **10-02 读任务数据闭环 + analyze_code 工具引导：** 用户拍板两项候选一起落地。**① 读任务数据闭环（L0.5b）**：`IntentOverride` 新增 ReadVerbs 词表 + `IsReadTask`（读动词+代码语境，与 IsWriteTask 对称互斥、写优先）；`RoutingIntelligence` 新增 `TrySuggestReadTier`（保守四条件：small 样本≥3 且成功率<0.6、agent_pro 样本≥3 且成功率≥0.5、agent_pro 省时——全满足才升级，失败由升级链 agent_pro→large 兜底）；`ModelRouter` L1 规则命中块内插入 L0.5b——规则命中 small 且 IsReadTask → 查 small/agent_pro Rule tier 历史 → 达标直连 agent_pro，查询失败/数据不足保持 small。**② analyze_code 工具引导**：AgentLoop 消息构造处，工程模式 + IsReadTask → 注入"优先调用 analyze_code 获取类/方法/行号索引，再按需 read_file 读具体行段"（确定性词表触发，非提示词堆砌）。tests 169→181（IsReadTask 6 项：读取/分析/查看结构 true、纯问答/修改/无语境 false；TrySuggestReadTier 6 项：样本不足/成功率高/pro 样本不足/pro 成功率低/不省时/全达标）181/181。端到端实证：工程读任务保持 small 一次完成；"分析 ReadFileTool.cs 结构"任务 small 空响应→large，**模型先 analyze_code 拿符号地图再 read_file 全文**，输出带行号成员清单+执行流程——符号地图链路真实生效。已提交 26d79a9。
> **10-02 agent_pro 工程理解专项：CodeAnalyzerTool 符号索引（§14-3w 候选④）：** 给 4B 中介大脑一个确定性"代码地图"，缩短"读+改"两步链（read 逐行探索 → 一次 analyze_code 拿结构）。**工具 `analyze_code`**：纯正则+行号扫描（无 LLM、确定性可测），C#/Java/JS/Go 识别 class/interface/enum/record/struct + 方法 + 属性（`{ get` 模式），Python 识别 class/def（缩进>0 判方法）；输出 `语言 | 总行数 | [行号] 符号… | 符号总数`；双根越界检查（同 read_file）、MaxBytes 200k、非代码文件提示"（非代码文件，无符号索引）"。DI 注册一行（RememberTool 后）。tests 158→169（C# 类/属性/方法行号、Python 函数/类/缩进方法、文件不存在、越界拒绝、非代码文件降级 11 项）169/169。端到端：工程模式读任务一次 read_file 完成（CompactBlock 缓存命中 + 记忆注入 2 条）。模型仍可自由选 read_file/analyze_code。已提交 ec37629。
> **10-02 记忆治理：去重内建 + forget/过期/dedup（§14-3w 候选②）：** 记忆膨胀治理（同 topic 任务归档重复积累 + 无删除/过期机制）。**去重（数据结构性，内建于 Add）**：agent_memory 表加 `last_hit_at`/`count` 列（PRAGMA 检测 + ALTER 迁移，兼容旧库）；Add 同 kind+topic → 合并更新（content 取最新、count+1、last_hit_at 刷新）否则 INSERT——任务归档同类失败自动合并为一条，count 承载真实次数，记忆不膨胀。MemoryHit record 加 Count/CreatedAt/LastHitAt（默认参兼容）；Search 返回时间列；BumpHit 刷新 last_hit_at。**治理方法**：List(limit)/GetStats(Total+按 kind+总命中)/Forget(id)/PruneExpired(maxDays,minHits)（COALESCE(last_hit_at,created_at) 早于 cutoff 且 hit_count<minHits 删除）/Deduplicate()（存量重复：kind+topic 分组 MIN(id) 保留、count=SUM 合并、删其余）。**Builder 计数升级**：失败计数改 `failures.Sum(Count)` + 单条 count>1 附"（同类已记录 N 次）"——合并后真实次数仍注入。**CLI 命令**：`memory`（默认 stats）/ `memory list [n]` / `memory forget <id>` / `memory prune [days] [minhits]`（默认 90/2）/ `memory dedup`。tests 148→158（同 topic 合并 count=2/最新 content/不同 topic 独立/检索带 count/BumpHit 刷新时间/forget/prune 过期删除/dedup 合并存量 count 累加 10 项）158/158。**端到端实证（真实 agent_memory.db）**：`memory stats` 20 条（model 2/task 18）→ `memory dedup` 清理 5 条存量重复 → `memory prune 90 2` 删 0（数据均新）→ stats 15 条。已提交 f2efac5。
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
> **10-01 测试临时目录迁移 X 盘（§14-3v）：** 用户要求不在 C 盘跑测试。tests/Agent.Infrastructure.Tests 临时目录由 `Path.GetTempPath()`（C:\Users\...\Temp\lawcheck\tests_*）迁移至 **`<workspace>\test-tmp\<PID>`**（workspace 已 gitignore，不入库；按进程隔离防并发互删；4 处测试块统一替换）。已创建测试目录并清理 C 盘 Temp 旧残留。验证：构建 0 错误、tests 57/57，目录落点实证在 X 盘（test-tmp\28608 等）。CLI 模型任务运行目录本就在 X 盘 workspace\runs，不受影响。已提交 d7a62d8。
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

本地优先（Local-first）的 AI 编码 Agent 工作台：.NET 10 控制台应用，用户在 CLI 输入自然语言需求，系统在**本地 Ollama 单模型三角色（law-unified 30B 一模型承载 small/large/agent_pro，Modelfile + 请求级温度区分；classifier 独立 qwen2.5:1.5b，全部本地推理）**之间做智能路由，自动完成「规划 → 生成代码 → 写文件 → 运行验证 → 出错自动修复」的闭环，并用 SQLite 记录会话、任务指标、跨任务记忆与工程符号索引。



| 项目   | 说明                                                                                                                     |
| ---- | ---------------------------------------------------------------------------------------------------------------------- |
| 解决方案 | `LocalCodeForge.slnx`（新版 XML solution 格式）                                                                            |
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
| `Routing/`                         | 三层漏斗路由（L0 查询词/L0.5 写直连 large/L0.5b 读数据闭环/L1 规则引擎/L2 意图覆盖/L3 分发 + RoutingIntelligence 数据闭环 + TaskFeatureClassifier/TaskMetricsAggregator D1/D2 归因） | \~1500 |
| `Project/`                         | 工程感知全套：ProjectScanner 只读扫描 / ProjectContextStore 签名缓存 / BackupService taskId 隔离回滚 / SymbolIndexer+Store 符号索引 / DependencyAnalyzer 依赖边 / TypeConflictDetector P-D / TestProjectDetector+TestRunnerService / CheckIncrementalPlanConflicts | \~2000 |
| `Tools/`                           | 10 个工具：list_dir/read_file(分页+重定向)/write_file(P-D+熔断)/edit_file(冲突检测)/create_dir/run_command(白名单+超时双轨)/analyze_code/find_symbol/analyze_deps/remember + PyCodeGuard 守卫 | \~1600 |
| `Metrics/`                         | SqliteMetricsCollector 三表（task_metrics+routing_decisions+tool_rejections）+ FailureCategorizer 失败归类 + ToolRejectionStore + 2 装饰器 + 时代隔离 | \~900 |
| `Memory/`                          | AgentMemoryStore（去重/治理/特征过滤）+ MemoryPromptBuilder（失败教训注入）+ TaskMemoryExtractor（任务归档） | \~600 |
| `Quality/OutputVerifier.cs`        | **run_command 输出判定公共验证器**（凭据/stderr 剥离/结构/通用失败+双容错/占位；agent+orchestrator 双路径共用**单一来源**，根治双路径不一致，§14-3e） | 236   |
| `Quality/PythonLintService.cs`     | ruff 优先，py_compile 降级；F821/F811 阻断                                         | 189   |
| `Session/SqliteSessionStore.cs`    | turns 表存对话历史                                                                | 108   |
| `KernelFactory.cs`                 | 注册命名 ChatCompletion 服务（三角色同 tag + classifier）                          | 72    |
| `DependencyInjection.cs`           | `AddAgent(options, workspaceRoot, dbPath, ...)` 一站式装配（工具工厂注入 taskId/rejectionStore 等） | 200   |
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

3. **【已解决 10-03】RoutingRules.json 两条同名规则** `complex_keywords`：10-01 已改名 complex_local（150，→agent_pro）/ complex_cloud（85，→云端 large），但 85 与 150 语义重叠（150 先命中）实为死代码；**10-03 已删除 complex_cloud**，规则 9 → 8 条（冒烟验证「RuleEngine 已加载 8 条规则」）。现复杂任务统一走 complex_local → agent_pro（单 tag law-unified，行为等效）。

4. **【已解决 10-01】`AgentOptions.EnableOrchestration` 未接线**：10-01 P0 可持续化批次已删除该配置项（全项目无读取，grep 0 匹配），实际分发走静态 `TaskClassifier.ShouldOrchestrate`——删除而非接线（保留单一事实来源）。

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

20. **【已修 10-04】PS 5.1 -File 模式向子进程 stdin 注入 UTF-8 BOM**：管道、`StandardInput.Write`、`StandardInput.BaseStream.Write` 全绕不过（捕获实证 `b'\xef\xbb\xbfuse --write ...'` 37B 含 EF BB BF）——CLI `StartsWith("use ")` 不命中、use 行被当任务执行 = E2E"假完成"根因。已双管修复：CLI `use` 前 `TrimStart('\uFEFF')` 剥离 BOM + E2E 改用 runner.py（python subprocess 喂 stdin）。**接手注意**：任何从 PS 5.1 向原生 exe 管道传多行 stdin 的场景都会踩此坑（-Command 与 -File 行为不同）。

21. **【已修 10-04】E2E incremental 断言语义**：incremental 用例把"安全拦截但任务失败"（P-D 拒后模型不换路、dotnet run 验证旧代码假完成）与"侧路假完成"（安全漏洞，模型在工程根外建项目文件）都标为 `[工程未接入-假完成]`——语义失真，易误读为安全漏洞回潮。已校准为三态（提交 282bf8d）：`[安全拦截生效-模型未接入]`（有拦截信号但模型能力失败）/ `[安全拦截]`（无拦截信号但完成且 demo 未动=疑似侧路漏洞）/ `[工程未接入]`。run10/13 实证：incremental 恒为 `[安全拦截生效-模型未接入]`，安全层恒定生效。

22. **【依赖说明】E2E 基建依赖 python311**：`scripts\e2e\runner.py` 用 python subprocess 启动 CLI 喂 stdin（确定性，绕 PS BOM 坑）。系统有 Python311（已装 requests），接手需确保 `python` 在 PATH（E2E 脚本直接调 `python`）。

### 多文件回归中的观察（2026-09-22 18:25）



* `expected_output`**&#x20;是软指标**：3 个任务的输出格式与 Plan 预期略有差异（如学生管理预期 "平均值为：85.0" 实际 "88.10"），验证只看退出码 0 → 通过。这是设计行为；若要严格校验输出格式需另加机制（会降低成功率）。

* **Status 字段建模不稳定**：plan 测试里出现过 "枚举类型 Status"，实际执行多为 string 属性 —— 模型对类型建模有漂移，不影响编译运行；想统一可加一条提示词约束。

* **错误&#x20;**`using <项目名>;`**&#x20;未再出现**：规则 G 的约束在生效（此前出现过一次 `using OrderSystem.Order;`，被 Executor 正确忽略）。



***

## 13. 构建与运行



```
\# 前置：已安装 .NET 10 SDK；已启动 Ollama（ollama serve）并 pull 四个本地模型

cd <repo>

\# 构建（已恢复通过）

dotnet build LocalCodeForge.slnx

\# 运行（会做本地模型健康检查）

dotnet run --project src/Agent.Cli

\# 可选：大模型兜底需要

setx DASHSCOPE\_API\_KEY <你的key>   # 改后需重开终端
```

工作区根目录取 `AppContext.BaseDirectory` 上跳 5 级（从 bin/Debug/net10.0 回到项目根），产物落 `workspace/runs/`；`RoutingRules.json` 以 `CopyToOutputDirectory=PreserveNewest` 拷到输出目录。

**建议的快速验证路径**：`dotnet run --project src/Agent.Cli` → 输入 `用 C# 写一个待办事项项目，分 Todo 模型、TodoService、Program，多文件组织` → 观察 \[规划]/\[写入]/\[完成] 事件与产物文件数 → `stats` 看指标。

**E2E 端到端复测（4 用例：工程增量-ShippingService / 单文件-hello python / 多文件-C#学生系统 / 读查-Order.cs）**：

```
powershell -NoProfile -File scripts/e2e/e2e.ps1
```

* 结果落 `workspace/e2e/e2e_<ts>.log` + `case_<kind>_<rand>.log`；最近 run13 = 3/4（single/multi/read PASS，incremental `[安全拦截生效-模型未接入]`，见 §12-21）。
* 依赖 python311（`scripts/e2e/runner.py`，python subprocess 喂 stdin 绕 PS BOM 坑，见 §12-20）。
* incremental 用例要求工程模式增量修改 demo 工程（ShippingService 接入），当前受 30B 增量修改能力上限制约，非安全漏洞——**已知能力边界，基线固化 3/4 + 安全拦截恒定（10-04 用户确认方案 A）**。



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

3o. **【已完成】D2 路由数据校准（10-03）**：`TaskFeatureClassifier` + `TaskMetricsAggregator`（metrics→特征类成功率/失败主因聚合）+ Orchestrator 重试上限校准（高失败率特征类 3→1，省 CPU 空转）+ CLI stats 特征类归因块；tests 294/294；提交 8a51e9c。

3p. **【已完成】显存卸载层实验 + 工程增量计划校验（10-03）**：num_gpu 4→8（基准 16.6→18.2 tok/s、显存余 2.2GB 无 OOM 风险）取 8 + `ollama create law-unified` 重建；`Orchestrator.CheckIncrementalPlanConflicts`（Plan 中已有文件 ≥2 判定整体重写倾向 → Planner 阶段拒绝，早拦截）；tests 298/298；提交 466810c。

3q. **【已完成】改进批次二（10-03）**：备份生命周期 `BackupService.PruneBackups(keepLastN=8)` + agent_memory 加 feature 列 + Indexed search 过滤 + ArchiveTask/Remember 打标签（跨类剔除 task 归档 / 降权 model 决策）+ E2E 脚本（工程接入 diff 防假完成，`scripts\e2e\e2e.ps1`）；tests 304/304；提交 441a99e。

3r. **【已完成】方案 A：工程模式写前拦截项目文件（10-04）**：E2E 暴露"侧路新建项目假完成"——工程模式下模型避开 demo 工程，在 CLI run 目录新建 OrderSystem.csproj + 全套文件 → `dotnet run` 编译通过=假完成，demo 工程实际未接入。实施：WriteFileTool / EditFileTool `insideProject` 块（P-D 检测前）工程模式下 `.csproj/.sln/.slnx/.fsproj/.vbproj` 禁止修改/新建 → 退出码 126；tests +4 → 308/308；提交 00e5bc5。端到端验证链暴露真正根因：**PS 5.1 -File 模式向子进程 stdin 注入 UTF-8 BOM（EF BB BF，管道 / StandardInput.Write / BaseStream.Write 全绕不过，捕获实证 37B）→ CLI `StartsWith("use ")` 不命中、use 行被当任务执行=E2E 假完成**。

3s. **【已完成】E2E BOM 根因修复（10-04）**：双管齐下——① CLI 侧 `use` 命令前 `TrimStart('\uFEFF')` 剥离 BOM（Program.cs，产品级管道调用鲁棒性）；② 测试侧新增 `scripts\e2e\runner.py`（python subprocess 启动 CLI 喂 stdin，确定性无 BOM 注入，UTF-8 严格 / GBK 回退解码），e2e.ps1 弃用 PS 直写 stdin。**E2E run9 = 3/4**：single 31s / multi 81s / read 29s 全 PASS（耗时较此前 59-160s 大降）；incremental 安全层全生效（use 工程模式 + P-D 类型拦截触发并附工程已有类型清单 + 无侧路项目文件写入）；剩余失败 = **30B 增量修改能力上限**（被 P-D 拦后不读工程、不换 edit_file 局部改、直接 dotnet run 验证旧代码假完成——与 ShippingService 幻觉字段同类，用户已接受现状，非安全漏洞）。tests 308/308；提交 f247faf。

3t. **【已完成】P0 双出口同步：Dashboard 调权 dropPro 层级错配（10-14，架构审查任务8-A1）**：10-11 CLI 侧修复 P2 调权错配（agent_pro 层级实为 Rule——SmallModel 过滤永远 0 样本，P2 从未真正评估）时 `/api/tuning` 端点漏改，web 端同源 bug 存活。实施：`DashboardHost.cs` 删 `proL3`（AgentPro+SmallModel）查询，`dropPro` 判定与 `complexDrop` 展示复用 `proRule`（AgentPro+Rule，与 CLI `proRuleP2` 同语义，且避免 CLI 侧 proRule/proRuleP2 的重复查询——审查任务7-B4）。**验证**：构建 0 错误；tests 813/813（`/api/tuning` 结构断言不受影响）。

3u. **【已完成】P0 CompletionGate.PathsEqual 删误导性 TrimEnd（10-14，审查任务5-A12）**：`TrimEnd('\\','/')` 对 GetFullPath 归一化后的非根路径是死代码（已无尾分隔符），对盘符根（"X:\"）反而削成非规范形态 "X:"。实施：纯 `GetFullPath + OrdinalIgnoreCase` 比较，语义不变（两侧同函数归一化）。**验证**：构建 0 错误；tests 813/813（完成门 B/D 区段不受影响）。

3v. **【已完成】P0 测试工程名词边界（防误报，10-14，审查任务5-B6）**：`Contains("Test")` 子串匹配误报 `Contestant.csproj`/`MyTester.csproj`/`Latest.csproj` 等非测试工程（测试门是失败判定依据，误报会错杀正常工程）。实施：`TestRunnerService` 新增 `IsTestProjectName` 词边界正则（Test/Tests 须为独立词——start/分隔符后，或小写→大写 PascalCase 段如 UnitTests/MyProjectTests；词尾须 end/分隔符/大写数字；全大写 TESTS.csproj 宁漏检不误报），`DetectTest` 与 `TestProjectDetector.HasTests` 双出口同源改调（9/22 双路径教训）。**验证**：tests 新增 3 项断言（Contestant.csproj→false / 正例 MyApp.Tests·UnitTests·MyProjectTests / 负例 Contestant·MyTester·Latest·Testing）813/813；构建 0 错误。

3w. **【已完成】P1-4/P1-7 FileGrammar 文件系统语法单源（10-14，架构审查任务5-A1/A5 + 任务8-C13）**：新建 `Project/FileGrammar.cs`（public static，零 LLM）承载四类语法单源——①`ExcludedDirNames`（原 `ProjectScanner.ExcludedDirs` 最全实现提升为全层共享，符号索引/依赖分析两份顶层 `StartsWith` 行级复制删除）；②`CodeExtensions`（`.cs/.py/.js/.ts/.java/.go`，原 SymbolIndexer.CodeExts 键集与 DependencyAnalyzer 内联集合两份归一）；③`EnumerateCodeFiles`（惰性枚举 + IO 容忍，上限策略留给调用方）；④盘符两语法 `TryExtractChineseDiskRoot`（"X盘"，自 ProjectPathDetector 归位）/ `TryExtractLiteralDiskRoot`（"X:\" + 存在性校验，自 UseCommandParser 归位）——**归位非合并**（复核发现两处 TryExtractDiskRoot 语义不同：中文盘 vs 字面盘，审查任务5-A3 的"行级复制"判断有误，特此更正）。**顺带修两处潜在误伤**：原 `StartsWith("bin")` 误伤 `binary/` 目录、`Contains(".git")` 误伤 `.gitignore` 同类名文件；且排除判定从"仅顶层"升级为"任意层级段名匹配"（`src/obj`、`web/node_modules` 现已排除）。`DependencyAnalyzer` 私有 `EnumerateCodeFiles`（26 行内联复制）删除，改调单源。**验证**：构建 0 错误；区段 1449 改写为 FileGrammar 断言（段级排除/嵌套排除/枚举过滤/两盘符语法/空根）12 项全 OK，tests 813→820 全绿。

3x. **【已完成】P1-5 删除 TestProjectDetector（生产零调用死代码，10-14，审查任务5-A2）**：全 src 检索确认 `TestProjectDetector.HasTests` 在生产代码**零调用**（仅自身定义与注释引用；测试是唯一消费者）——审查任务5 判定其"被完成门消费"与实际不符，特此更正。测试门语义由 `TestRunnerService.DetectTest` 唯一承接（P0 词边界收严后已是事实上的唯一实现）。实施：删除 `Project/TestProjectDetector.cs`；区段 1449 测试改写为 FileGrammar + P-D 索引路径断言；`Contestant.csproj` 词边界负例迁入区段 1551（`DetectTest` 端到端）；`sections_baseline.txt` 区段 1449 标题同步。**验证**：构建 0 错误；tests 820/820。

3y. **【已完成】P1-6 P-D 写前类型冲突查询化（写路径 O(工程全扫)→O(增量+单查)，10-14，审查任务5-A4）**：`TypeConflictDetector.GetExistingTypes` 原每次写文件**全工程 `*.cs` 内容扫描 + 正则提取**（bin/obj 生成码亦计入）；而 `SymbolIndexStore` 的 mtime 增量索引就在旁边（find_symbol/analyze_deps/依赖图谱同源）。实施：①`GetExistingTypes`/`DetectDuplicateTypes` 增加可选 `SymbolIndexStore` 参数——索引路径 = 增量刷新（空索引自动全量构建）+ `GetAll` 单查 + kind 过滤（class/interface/enum/struct/record）+ **仅 .cs 口径**（与全扫一致，.ts/.js 索引符号不计入）；无索引/工程过大清空/异常 → 回退原 `ScanExistingTypes`（行为不变，防 P-D 失效）；②**精确复核**：索引正则不剥注释/字符串（多行字符串里独立成行的 `class X` 会入索引），冲突命中回读定义文件用精确提取口径复核（按文件缓存，命中才读盘，无冲突零开销）——拦截语义与全扫严格一致，防幻影误拦；③`WriteFileTool`/`EditFileTool` 构造器加可选 `symbolDbPath`，DI 传 `symbol_index.db`（与 find_symbol/analyze_deps 同目录约定），tests 直构不传 → 走全扫回退（既有断言零改动通过）。**验证**：新增 4 项断言（索引命中真类型/非 .cs 不计入/真重复拦截含定义文件/**字符串幻影经复核不拦截**/无 store 回退一致）全 OK；构建 0 错误；tests 820/820。**过程发现（待跟进）**：`SqliteConnection` 默认连接池在方法返回后仍持有 db 文件句柄——库文件放在待删目录内会导致 `Directory.Delete` 抛"文件被另一进程占用"（测试夹具已规避；生产侧 `workspace/` 清理若遇同类失败，候选方案为连接串 `Pooling=False`）。

3z. **【已完成】P1 交付后自检：补齐第四份复制 + 修过时注释 + 登记"故意不合并"清单（10-14）**：按用户要求对 P0/P1 交付做全量自检（grep 旧模式 + 死代码残留 + IDE 诊断），查出三处问题并全部修正。①**遗漏（真）**：`Orchestrator.EnumerateExistingFiles`（规划提示的"已有文件清单"）内含**第四份**排除规则行级复制 + 第五处扩展名内联集合（代码 6 项 + 工程清单 5 项）——架构审查任务5 只识别出 3 处（SymbolIndexer / DependencyAnalyzer / ReadOnlyGuard），**漏了 Orchestrator**；实施：`FileGrammar` 新增 `ProjectManifestExtensions`（.csproj/.sln/.slnx/.fsproj/.vbproj；`ProjectScanner.DepExtensions` 与 Orchestrator 各一份 → 归一），`ProjectScanner.DepExtensions` 改引用单源，`Orchestrator.EnumerateExistingFiles` 改走 `IsExcludedRel + CodeExtensions + ProjectManifestExtensions`（并修正该方法签名行"`)    {`"格式缺陷）。②**改错**：`TestRunnerService` 内注释仍引用 P1-5 已删除的 `TestProjectDetector.HasTests`（悬空引用误导后来人）→ 改为记录收敛结论。③**防复发**：`FileGrammar` 头注释登记三处**故意不合并**的异用途清单（`ProjectScanner.CoreFileLines.sourceExts` 13 项含 .rs/.cpp/.vue/.razor 的体积排序榜 / `ProjectPathDetector.KnownFileExtensions` 24 项无点前缀的自然语言文件名词表 / `DashboardHost.TextExts` 31 项产物预览展示面），避免下次审查再当遗漏"归一"。**验证**：构建 0 错误；tests 820/820（修正后零回归）。**自检同时确认**：P0 三项代码残留为 0（`SmallModel|proL3`、`TrimEnd` 均无匹配）、`IsTestProjectName` 双出口接线完整（4 处）、7 个改动文件 IDE 诊断 0 问题。

4a. **【已完成】P2-1 调权状态单源 RoutingTuningReporter（10-14，架构审查任务8-A1 + 任务7-B3/B4）**：新建 `Routing/RoutingTuningReporter.cs`（public static，零 LLM）——把"时代过滤查询 + 三处调权判定 + agent_pro 层级"收敛为一处：`GetStatsAsync`（epoch 过滤查询，ModelRouter 运行时决策复用同一口径）/ `BuildAsync`（三类样本各查一次；agent_pro 一次供读升级与 Complex 降级共用）/ `RateOf`（成功率口径）。三个消费方改调：①CLI stats「路由调权状态」小节（删内联 `TuningAsync` + 三判定 + `proRule`/`proRuleP2` 重复查询，**输出文案逐字不变**）；②Dashboard `/api/tuning`（删端点内联副本，**JSON 字段逐个不变**，前端零改动）；③`ModelRouter.GetTuningStatsAsync` 委托 reporter（判定仍内联——运行时决策不能等批量报告）。**结构性收益**：10-11 的 P2 层级错配（agent_pro 实为 Rule 却按 SmallModel 过滤 → web 端永远 0 样本）从此**不可能再分叉**（单一查询点，双出口只做渲染）。**验证**：区段 2004 新增 3 项断言（agent_pro Rule 时代样本=2——层级若再错必然得 0；读升级与 Complex 降级样本数/成功率同源一致；0 样本成功率=0）；构建 0 错误（首版新增 3 条 CS8604 已随 `TuningVerdict.Reason` 可空声明消除，警告数回到基线 9）；tests 820→823 全绿。

4b. **【已完成】P2-3 Dashboard/工具 store 单例化（10-14，任务8-A2）**：原 `/api/memory`、`/api/deps` **每请求** `new AgentMemoryStore/SymbolIndexStore`（构造即 `CREATE TABLE` + `PRAGMA` 列迁移），且 Write/Edit 工具、find_symbol、analyze_deps 各自另持一份 store 实例。实施：①DI 注册 `SymbolIndexStore` 单例（沿用 metrics.db 同目录约定；`AgentMemoryStore` 本已是 DI 单例）；②`DashboardOptions` 增两个**可选**注入参数（`MemoryStore`/`SymbolStore`）——CLI 传 DI 单例；未注入时 `StartAsync` 按路径建**一次**实例并全程复用（tests 现有 6 处构造零改动）；③`WriteFileTool`/`EditFileTool` 构造器参数由 `string? symbolDbPath` 改为 `SymbolIndexStore? symbolStore`（工具侧不再自建实例）。**收益**：进程内 symbol_index.db 只一份 store；请求路径上零建表、零 PRAGMA。**验证**：构建 0 错误；tests 823/823（Dashboard 区段 6 处构造 + `/api/tuning`、`/api/deps`、`/api/memory` 断言全过）。**遗留**：`DashboardOptions.MemoryDbPath/SymbolDbPath` 保留为"未注入时"的回退路径（P3 可随 tests 改造一并移除）。

4c. **【已完成】P2 批次门禁自检（10-14；用户新增约定：每批次完成后先自检任务列表，确认无遗漏/改错再开下一批）**：约定已写入工作流规则（文档同步 → 构建 0 错误 + tests → 提交 Git → **批次门禁** → 推送）。**首次执行即查出一处遗漏并修正**：4b 声称"进程内 symbol_index.db 只一份 store"，但 `FindSymbolTool`/`DependencyAnalyzerTool` 仍各自 `new SymbolIndexStore(dbPath)`（实为三份）——实施：两工具构造器参数由 `string dbPath` 改为 `SymbolIndexStore store`（DI 注入单例），`Suites_Tools` 4 处直构（632/643/864/870）同步改传实例。**同批确认无遗漏**：①调权判定调用点仅剩 reporter 单源 + `ModelRouter` 有意内联的运行时决策（CLI/Dashboard 两副本清零）；②`GetServiceStatsSinceAsync` 业务调用仅剩 reporter；③`DashboardOptions.MemoryDbPath/SymbolDbPath` 仅存于 record 声明与 `StartAsync` 未注入回退（已记账 F5）；④6 个改动文件 IDE 诊断 0 问题；⑤store 构造点复查仅剩 DI 两处（113 SymbolIndexStore / 177 AgentMemoryStore）。**验证**：构建 0 错误（修正过程中出现的括号失衡 CS1002/CS1513 与 4 处测试构造签名不匹配已修，警告数回到基线 9）；tests 823/823。

4d. **【已完成】P2-2 SSE 与回放序列化同源 + [WireName] 特性（10-14，任务8-A4/B7）**：原 SSE 走 `ToWire`（34 行手工匿名对象、逐类型挑字段），回放走"存储 payload + 首位补 type"——record 新增字段时回放能看到、SSE 看不到，同一任务两视图悄悄分叉（机制不对称 = 漂移温床）。实施：①SSE 改走 `Serialize(evt, GetType(), TaskEventStore.JsonOpts)` → `JsonObject.Insert(0,"type",…)` → `ToJsonString(JsonOpts)`，与回放/存储**同一条路径**（逐字段同源）；②删 `ToWire`（34 行）；③`Agent.Core` 新增 `WireNameAttribute`，8 类事件就地标注 wire 短名，`DashboardHost.WireTypeMap` 改**反射构建**（原 `nameof` 手工表 8 项：新增/重命名事件需手工同步两处，漏改则该事件两视图不一致）；未标记类型兜底类名原样（不吞）。**验证**：新增 2 项断言（8 类 wire 短名与前端 `evt.type` 契约逐一相符；`ToolResultEvent` 同源序列化 → `{name,output,success}` 且 type 由宿主补而非序列化自带）；构建 0 错误（警告基线 9）；tests 823→825 全绿。前端 `index.html` 渲染零改动（字段已逐一核对全等）。

4e. **【已完成】P2-4 /api/deps 加 CancellationToken + 依赖图 30s TTL 缓存（10-14，任务8-A3）**：原端点每次请求都"增量索引检查 + `GetAll` + `DependencyAnalyzer.Build` 全量边重算"，且 lambda 未接 `CancellationToken`（浏览器断开也停不下）。实施：端点改 `async (string? path, CancellationToken ct)`，索引前后各设 `ct.ThrowIfCancellationRequested()`（索引与边计算为同步 CPU+IO，无法逐层传导，只在节点间检查）；新增 30s TTL 依赖图缓存（`ConcurrentDictionary` 按工程根键控、条目上限 8、超限整体清空；概览与 `?path=` 两路共用同一份图，重复请求不再重算）。**验证**：构建 0 错误；tests 825/825（Dashboard 区段 `/api/deps` 概览 + 单文件依赖断言全过，第二次请求走缓存路径亦被覆盖）。

4f. **【已完成】P2-5 SymbolIndexStore 批量插入（10-14，任务5-B4）**：`BuildAndSave`/`BuildAndSaveIncremental` 原逐行 `ExecuteNonQuery`（300 文件工程单次全量构建持锁上千次，增量每文件同样逐行）。实施：新增私有 `InsertBatch`——按批拼 multi-VALUES `INSERT`（每批 ≤100 行 = 600 参数，远低于 SQLite 变量上限），全量与增量两条路径共用；增量侧改为每文件一次批插。**验证**：构建 0 错误；tests 825/825（符号索引增量断言照旧：`BuildAndSave ≥3 符号`、`BuildAndSaveIncremental` 重扫计数与总数）。

4g. **【已完成】P2-2/4/5 批次门禁自检（10-14）**：按约定自检本批次，查出并修正**验证盲区一处**：P2-2 新增断言只校验了 `[WireName]` 标注存在，**未验证宿主反射映射真的生效**——若 `GetNestedTypes()` 返回空，wire 名会静默退化为类名，前端事件全部不渲染且无任何报错（最坏形态）。实施：`DashboardHost.WireType` 提升为 public（宿主映射的真实验证入口），新增 2 项断言：8 类事件经宿主解析逐一等于契约短名 + 未知类名兜底原样（不吞）。**同批确认无遗漏**：①`ToWire` 全域仅存于注释（无代码残留）；②`SymbolIndexer` 已无逐行 `ExecuteNonQuery` 插入（余下调用均为 Initialize 迁移 / DELETE 清理 / `InsertBatch` 单次执行）；③`DashboardHost` 内无任何每请求 store 构造；④本批补齐 3 项断言，覆盖此前**无任何测试覆盖**的 SSE 载荷与 wire 契约。**验证**：构建 0 错误（警告基线 9）；tests 825→827 全绿。

5a. **【已完成】P3-1 工程上下文缓存：签名计算内联 + 内存缓存上限（10-14，任务5-A14/B3）**：原流程要求调用方手工串`ComputeSignature`（一次 Walk）→ `TryGet` →（未命中）`Scan` → `Put` 四步——顺序错或漏算签名即退化为每任务全量重扫。实施：①新增 `ProjectContextStore.GetOrCompute(root, out bool cacheHit)` 封装全链（命中路径仍是 1 次签名 Walk，与原实现同——价值是**封装防漏**，不是省一次 Walk，不夸大收益）；②`AgentLoop.LoadProjectCompactBlock` 收敛为一次调用（缓存命中/未命中日志原样保留）；③内存缓存原为**无界 Dictionary**（多 use 不同工程根时进程内线性增长）→ 8 条上限 + FIFO 淘汰（`RememberInMemory`，SQLite 回填与 `Put` 两处同走）。**验证**：构建 0 错误（警告基线 9）；tests 827/827。

5b. **【已完成】P3-2 敏感区守卫：静态构造去 P/Invoke + 正向判定缓存（10-14，任务5-A7/A8）**：①原静态构造对 13 个内置拒绝根各做一次 `GetShortPathNameW`（P/Invoke）——**首次触发本类任意成员即阻塞**（网络盘/异常用户目录可挂住进程）；现静态构造只登记长名（零 P/Invoke），8.3 短名在首次 `IsDenied` 时经 `EnsureShortRootsExpanded` 加锁一次性展开（含启动后追加的用户/测试根，避免"启动期集合"与"懒展开期集合"两套短名不一致）。②判定结果缓存**只缓存"已判定为敏感"的正向结果**（有界 4096、超限整体清空）；**明确不做负向缓存**——两次判定之间若有人把中间目录换成指向敏感区的 junction，陈旧 false 会放行读取，这是安全漏洞而非性能取舍（理由已写入代码注释，不留给后人误加）。**验证**：构建 0 错误；tests 827/827，关键断言 `8.3 短名绕过被拦（C:\PROGRA~1）`、`符号链接最终目标命中敏感区（link→C:\Windows）` 均 [OK]——惰性展开未削弱任一判定线。

5c. **【已完成】P3-5 防复发：哨兵契约单测 + help 补 dashboard + 漂移自查脚本（10-14）**：①**哨兵单测**（把注释级契约钉成可执行断言）：新增 3 项——`GetRunRoot` 命名启发式（新建区段 2450：`task_` 前缀目录的 `.backup` 落**父目录**而非任务目录内，改名即静默错位）、`CommandHub` 无任务哨兵（`CurrentTask = (0,null)`，前端"空闲"显示依赖此契约）、`PathBoundary` root 带/不带尾分隔符等价（区段 2596）。**另纠正一处审查误判**：`..` 跳脱越界断言区段 2596 **本就存在**（任务5-B12 称缺失，实际已覆盖）——不重复添加，改为补一条尾分隔符等价断言。②**help 补 `dashboard`**（`Program.cs` help 命令行此前只列 8 个命令，dashboard 不可发现——web UI 已自动起却无人知道能手动开）。③**漂移自查脚本** `scripts\drift-check.ps1`（仓库无 CI，先落本地门禁工具）：扫描 src 注释中的三类漂移标记并输出按文件分布，超阈告警 + 退出码 1（可后接 CI）；基线 **51 处/19 文件**，阈值 60。**过程教训两条（已写入脚本头注释）**：PS 5.1 把无 BOM 的 UTF-8 脚本按 ANSI 读 → 中文注释乱码**破坏引号配对导致解析失败**（脚本改纯 ASCII，中文关键词用码点拼装）；关键词若用单字（漂/双/单）会把"单独/双重"全命中（291/63 假阳性）→ 必须整词。**验证**：构建 0 错误（警告基线 9）；tests 827→**831/831**（新增 4 项哨兵）；脚本双向验证（阈值内退出 0、超阈退出 1）。

5d. **【已完成】P3 批次门禁自检（10-14）**：按约定自检 P3 子批 A/B，查出并修正**遗漏一处 + 说谎日志一处**：P3-1 只收了 `AgentLoop` 调用方，**漏了 CLI `use` 命令路径**——`Program.cs` 原为「先无条件全量 `ProjectScanner.Scan`（含目录树/上下文块构建，最贵一步）→ **之后**才 `TryGet`」，即**缓存命中也照付扫描成本**，且命中分支打印「（工程上下文命中缓存，未重新扫描）」**与事实不符**（扫都扫完了）。实施：`use` 路径同样改走 `GetOrCompute(proj, out cacheHit)`，日志按实际 `cacheHit` 分支输出（「已重新扫描并写入缓存」）。收益：重复 `use` 同一未改动工程时真正省掉全量扫描。**同批确认无遗漏**：①`SensitivePathGuard` 静态构造确无 P/Invoke（仅 `Path.Combine` + 长名登记）；②哨兵 4 项在库且全绿；③`drift-check.ps1` 双向退出码验证通过（阈值内 0 / 超阈 1）；④9 个改动文件 IDE 诊断 0 问题；⑤警告数保持基线 9。**验证**：构建 0 错误；tests 831/831。

5e. **【已完成】P3-4 API 收敛：内容符合性单入口 + 备份保留份数配置化（10-14，任务5-B1/B2/A11）**：①`ContentConformance` 原两个提取方法（`ExtractRequirementTokens` 标准 / `ExtractRequirementTokensExtended` 扩展）**要求调用方懂语义才敢选**（审查任务5-B1）→ 合并为**单入口** `ExtractRequirementTokens(input, RequirementScope scope = Standard)`，范围由 `RequirementScope.Standard` / `.WithBareVerbs` 显式表达；`Extended` 方法删除，`AgentLoop` 3 处调用 + tests 3 处同步改调，`CompletionGate` 注释中的失效方法名一并更正。行为等价：扩展形态仍仅在标准提取为空时启用（已有引号/注释 token 不叠加），`WithBareVerbs` 仍由调用方按"有无显式写目标"决定。②`BackupService.PruneBackups(workspace, 8)` 的 `8` 为魔法数 → 新增 `AgentOptions.BackupKeepLastN`（默认 8），`Program.cs` 启动清理改读配置。③`GetRunRoot` 的显式注入**本轮不做**（需贯穿 8 个 public 方法签名，收益低于风险；其行为已由 P3-5 新增的区段 2450 哨兵测试锁定，改名即红）。**验证**：构建 0 错误（警告基线 9）；tests 831/831，关键断言 `内容符合性·扩展提取（内容：xxx 冒号形态与 写入一句 xxx 动词形态可提取）` 在单入口下仍 [OK]；全仓 `ExtractRequirementTokensExtended` 仅存于注释（无代码引用）。

5f. **【已完成】P3-3 Dashboard 端点按域拆组（10-14，任务8-A5）**：`DashboardHost.MapEndpoints` 原为 **352 行单方法内联 15 个端点**（审查任务8-A5），改为「vendor 静态文件 + token 中间件 + 单页 + 5 组调用」：`MapStatEndpoints`（stats / tasks / tuning / memory）、`MapBackupEndpoints`（backups / rollback×2 / diff）、`MapFileEndpoints`（files / deps）、`MapEventEndpoints`（回放 + SSE，**两侧同源故同组**——P2-2 已消除其机制不对称）、`MapCommandEndpoints`（commands / queue / cancel / current-task）。**纯搬移、零行为变更**——端点全为字面量路径、无通配歧义（`/api/tasks` 与 `/api/tasks/{id}/events` 段数不同），分组导致注册顺序变化但不影响路由匹配与中间件语义。**验证**：构建 0 错误（警告基线 9）；tests 831/831，其中 Dashboard 区段 **15 个端点断言逐条全绿**（含鉴权 4 项、files 越界 400、deps 概览/单文件、tuning 三态、rollback dry-run/执行、事件回放、SSE 相关）——覆盖度本身即为"零行为变更"的证据。**`Program.cs` Main 拆分本轮不做**：Main 近千行、含大量跨块局部状态（workspace / options / provider / commandHub / dashboardUrl / lastTaskId），机械抽函数的风险高于收益，留作独立一轮。

6a. **【已完成】F1-F6 待跟进项清理（10-14）**：P3 后遗留的 6 项逐项落地，其中 1 项经侦察后改为"文档化边界"而非改码。**F3**`ReadOnlyGuard.FindExistingProjectFileRef` 改走新增的 `FileGrammar.EnumerateAllFiles`——原为裸 `EnumerateFiles(root,"*",AllDirectories)`，**不排除 bin/obj**（构建产物里的同名文件会被误判为"工程内已存在"→ 错拦只读工程任务，**属正确性修复而非纯性能**）。**F4**`FileGrammar` 枚举模式 `*.*` → `*`：原模式漏掉无扩展名文件（Makefile/LICENSE 等），与"先枚举再按扩展名过滤"的自述语义不符。**F2**超大工程（>300 文件/8MB 上限命中者）符号索引恒为空，原每次写文件都白跑一次全工程枚举 + 一条无意义 `DELETE`再回退全扫 → 进程内记忆"建不出索引的工程根"（有界 32），后续直接走回退路径（工程缩小需重启才重试，属保守可接受）。**F6**`/api/files` 目录列表改用 `DirectoryInfo.EnumerateFileSystemInfos()` 复用同一对象读长度/时间（原每条目 3 次 stat）+ **条目级 try/catch**（枚举后条目被删不再让整端点 500）。**F5**`DashboardOptions` 删除 `SymbolDbPath`/`MemoryDbPath` 两个路径字段，store 改为**必填实例注入**（CLI 传 DI 单例、tests 6 处构造改传实例）——消除"路径 + 实例"双来源二义性，宿主不再保留回退分支。**F1（经侦察改为文档化）**：`SqliteConnection` 默认连接池会在方法返回后仍持有 db 文件句柄（本次 P1 测试夹具曾因此 `Directory.Delete` 失败）；**全 src 检索确认生产无影响面**——`Directory.Delete` 仅出现在 `BackupService`（删 `.backup/<taskId>` 与 `.new` 标记目录，从不含 db 文件）。故不改 8 处连接串（`Pooling=False` 有性能代价且当前无收益），改为在数据落盘文档写明边界："进程持有 store 期间不要删除 workspace 目录"。**验证**：构建 0 错误（警告基线 9）；tests 831/831（ReadOnlyGuard 5 项、/api/files 4 项、deps 概览+单文件全绿）。

6b. **【已完成】完成门入参 record 化（CompletionContext，10-14，落地审查任务5-B2）**：`CompletionGate.Evaluate` 原为**7 个位置参数**（5 必填 + 2 可选）——调用点靠记忆核对顺序，`AgentLoop` 三处收尾判定各自重复同一段"按有无显式写目标选提取范围"的参数拼装。实施：①新增 `CompletionContext` record（IsWriteTask / WroteAny / WriteRejected / Target / TargetIsFile / WrittenFiles / RequirementTokens）；②新增 `Evaluate(CompletionContext)` 作为**推荐入口**（内部一行委托到位置参数版——判定逻辑仍只有一份实现，不构成双路径）；③`AgentLoop` 三处收尾判定（熔断收尾 / 正常收尾 / 最大步数收尾）全部改走 record 入口；位置参数版保留（tests 既有调用与外部兼容）。**验证**：构建 0 错误（警告基线 9）；tests 831/831，完成门 B/C/D 共 9 项断言全绿（行为等价）。

5g. **【已完成】P3-3 Main 分派块抽离（10-14，架构审查任务7-B1）**（条目号原误用 3z，与 P1 自检条目撞号，已改为 5g 并归位到 P3 系列末位）：`Main` 由 **970 行降至 704 行**（文件 1822→1889，块体搬出而非删除），10 个 `// ═══` 分派块抽为 static 具名处理器（`IsExitCommand` / `HandleNewCommand` / `HandleHelpCommand` / `HandleDashboardCommandAsync` / `HandleMemoryCommand` / `HandleStatsCommandAsync` / `HandleUseCommand` / `AutoDetectUseFromInput` / `HandleDiffCommand` / `HandleRollbackCommand`），与既有 `ApplyUse`/`ArchiveTask`/`Print*` 助手同一风格。搬移铁律：只改两类东西——块内 `continue;`→`return;` + 参数列表增补；业务逻辑/输出文案/调用顺序逐字不动。状态传递两档：只读依赖走参数，需回写的仅 `sessionId`（new）与 `pendingTask`（use 一步到位）走 `ref`；`lastTaskId` 在 diff/rollback 中**只读**（回写发生在任务执行段），故无需 ref。**明确不抽**：任务执行核心（~170 行，与 loop/任务级超时令牌/consoleReader/归档/自动回滚/sessionWriteTargets 交织，收益 < 风险）、plan/orch（带【临时测试】标记，待单独决策）。**两处工程细节**：①`StartDashboardAsync` 局部函数**保留在 Main**（捕获 dashboardUrl 等状态），dashboard 处理器以 `Func<bool, Task<string?>>` 委托传入——同一闭包，无需把状态铺成 9 个参数；②抽取 `IsExitCommand` 后编译器不再知晓 `input` 非空，故调用点显式保留 `input is null ||` 以恢复对后续全部分派的类型收窄（否则新增 CS8602）。**唯一超出纯搬移的改动（已验证等价）**：use 分派门控由两段（`TryParse` 真 → use 块；否则 `IsUseCommand` 真 → 打印用法）合并为单一 `IsUseCommand` 门——因 `TryParse` 真 ⟹ 输入匹配 `^use(\s|$)` ⟹ `IsUseCommand` 真（UseCommandParser.cs:19-21 与 44-45），两分支并集恰等于后者；处理器内仍先 TryParse 后IsUseCommand，顺序与输出不变。**验证**：构建 0 错误（警告基线 9，无新增）；tests **831/831**（基线核对：HEAD `e93ad4f` 完成门 record 化已将基线从 P1 的 820 提至 831，本次零漂移）；无头冒烟 11 个处理器逐项命中（help/new/memory 三子命令/stats 三子命令/裸 use 拦截/diff/rollback/dashboard 守卫/exit），`exit` 后进程退出码 0、无异常输出。

5h. **【已完成】P3-6 收尾批次：单源归位 + 机制诚实（10-14，任务6-A10/8-B3/8-C8/5-A6）**：①**LAW_E2E 判定归一**（审查任务6-A10）——`ModelRouter.IsE2ERun` 本已是公开单源（注释亦自称"唯一逻辑源"），但 `AgentLoopMetricsDecorator`/`OrchestratorMetricsDecorator` **各自复刻**同一句 `string.Equals(GetEnv("LAW_E2E"),"1")`（三处同义判定，改一处漏两处的漂移向量）→ 两处改调 `Agent.Infrastructure.Routing.ModelRouter.IsE2ERun`，零行为变更（表达式逐字相同）。②**SSE 中途接入标记**（审查任务8-B3）——订阅即建连，此前事件永不到达，任务进行中打开/刷新页面的用户会以为"事件丢了"且无任何提示；现建连后先发一帧 `{"type":"joined","at":<unix>}`（非 `AgentEvent`，不入事件流与存储），前端 `onmessage` 显式拦截该 type（不入 `eventsCache`、不渲染成事件行，改为副标题提示"中途接入、历史走回放"）。③**假异步命名归位**（审查任务6-A6，范围比原判断更大）——经核实**两个**方法同步：`TaskEventStore.RecordAsync`/`GetEventsAsync`（`ExecuteNonQuery`/`ExecuteReader`），而 `IMetricsCollector.RecordAsync`（`await using`+`OpenAsync`）、`RoutingDecisionStore`/`ToolRejectionStore.RecordAsync` 是**真异步** → 本库约定「Async 后缀=真异步」成立，TaskEventStore 是异类；改名 `Record`/`GetEvents`（src 2 处 + tests 12 处 + 文档 2 处），**恢复约定而非破坏一致性**（若只改 TaskEventStore 反而制造新的不一致，故曾评估后否决）。④**`ScanVersion` 文档化边界**（审查任务5-A6）——内容哈希式失效不可行（签名已含 TotalBytes/MaxMtimeTicks，任何内容改动都会改签名；扫描器无法自省版本），残险仅为"扫描逻辑变更未递增常量"的人工纪律，按 F1 先例写入类注释纪律项。**验证**：构建 0 错误（警告基线 9）；tests **832/832**（+1 断言：`GET /events` 建连首帧含 `"type":"joined"`）；首轮编译即由CS1061 抓出 2 处漏改接收者（`store1`/`store2`），改名类改动有编译期兜底。另修正文档编号撞车：上条 Main 拆离条目误用 `3z`（与 P1 自检条目重复）→ 改为 `5g` 并归位到 P3 系列末位。

7a. **【已完成】P4-16 进程执行单一原语（10-14，落地"统一执行链路"铁律，审查任务5-B5）**：全库有**三份**手写进程执行实现（`RunCommandTool.InvokeAsync` 的 Task.WhenAny 超时竞态、`TestRunnerService.RunAsync` 的 BeginOutputReadLine 双流泵 + CTS、`PythonLintService` 的 ruff check 与 py_compile），同一套"重定向双流 + 等待退出 + 超时终止"语义出现三个口径（超时值 30/90/150s 三档、编码设置不一、**ruff/py_compile 完全没有超时**仅靠 ct，卡死即无限等待）。实施：①新建 `Execution/ProcessRunner.cs`（public static，零 LLM）——`RunAsync(psi, timeoutMs, ct) → ProcessRunResult(ExitCode, Stdout, Stderr, TimedOut)`，统一重定向/无窗口/UTF-8 解码、超时或外部取消一律`Kill(entireProcessTree)` + 回收部分输出且**不抛异常**；②四条执行链改调原语，**策略各自保留不合并**（合并会改变行为，已逐条核过：run_command 走 cmd.exe+chcp 包装、白名单**不含 pytest**、cwd 基准是 run 目录 vs 测试执行的工程根、超时 30/90s vs AgentLoop 传的 150s——直接委托会把测试超时从 150s 砍到 90s并让 Python 工程测试直接被白名单拒）；③**顺带补齐缺失超时**：ruff check 60s、py_compile 单文件 30s（超时按"工具异常不阻断"处理，与既有 ruff 退出码 2 语义一致）。**对外表现逐字对齐**：TestRunnerService 超时/取消原本经 catch(OperationCanceledException) 返回"测试超时"，RunCommandTool 原本经 WhenAny 竞态路径返回 124 超时失败——两者与新原语的 `TimedOut` 分支一一对应。**覆盖面已实测核实**：`Process.Start`/`new Process` 残余仅 4 处 + 原语自身，恰好等于文件头登记的两类例外（`ruff --version` 与 `py -c` 同步可用性探测；浏览器打开与 ollama serve 非命令类启动）。**验证**：新增 3 项断言（成功退出双流分别回收 / 超时→TimedOut=true 不抛 / 外部取消同路径），区段 1551 标题同步；构建 0 错误（警告基线 9）；tests 832→**835/835**。

7b. **【已完成】P4-17 diff 双读消除 + P4-22 备份落点死启发式删除（10-14，任务5-A10/8-B2）**：①**/api/diff 每文件读两遍**——`FileDiffService.Compute` 内部已把两文件读为行序列，宿主对非 TooLarge 又各`ReadAllText` 一遍取 Monaco 全文。实施：`FileDiffService.ComputeWithText(before, after)` 返回 `(DiffResult, Original, Modified)`——`ReadAllText` 一次即按 `ReadAllLines` 同一规则切行（`\r\n|\n|\r`、**保留中间空行**、末尾换行不产空行、BOM 双侧一致），行序列与 `Compute` 逐行等价（新增 3 项断言锁定：CRLF/中间空行/末尾换行三个差异点上结果完全一致 + 全文与 `ReadAllText` 一致 + TooLarge 返 null 不把超大文本塞 JSON）。`DiffResult` 形状未变 → `/api/diff` JSON 契约零变化。②**`BackupService.GetRunRoot` 的 `task_` 命名启发式删除**（审查任务5-A10原建议"改显式注入 runRoot"）——侦察结论与建议相反且更强：**该分支不可达**。全库 15 处 `BackupService.*` 调用点（CLI 7 / Dashboard 3 / Write+Edit 工具 5）传入的都是同一个 run 目录（`workspace` = `yyyyMMdd-HHmmss-xxxx`），无任何代码创建 `task_` 前缀目录；每任务隔离早已由 `.backup/<taskId>/` 承担。其配套文档"写工具传入任务目录 runs/<run>/task_xxx"亦已失效（写工具现收 DI 绑定的 run 根）。故**删除而非注入化**（注入化需改 15 处签名、误接风险高于收益；对现存路径零行为变化）。连带发现并修正**两处锁定死行为的测试**：区段 2450 哨兵（原断言"task_ 前缀→落父目录"，实测能通过只因它自己构造了生产中不存在的形态）改为锁定现行契约（落点恒为传入根，含"task_ 名字不再上浮"与"改名后落点不变"）；`Suites_Tools` 区段 8（模拟旧架构"工具收 task 目录、CLI 收 run 目录"）改为验证**现行生产接线**（工具与 CLI 收同一 run 根 → 能列出、能回滚）。③**顺带修一处测试偶发**：`P1-1 增量仅重扫 1 文件` / `新符号可见` 依赖"写文件后 mtime 递增"，而部分文件系统 mtime 分辨率低于写操作耗时 → 增量索引判"未变更"，本批首次运行偶发失败 1次（复跑即绿）。已显式 `SetLastWriteTimeUtc(+2s)` 使其确定（增量失效判定是被测行为，测试不应依赖文件系统时间粒度）。**验证**：构建 0 错误（警告基线 9）；tests 835→**839/839**，**连跑 3 次全绿**（偶发消除）；`sections_baseline.txt` 区段 2450 标题同步。

7c. **【已完成】P4 后两任务真实实测（10-14，本地模型 law-unified / qwen2.5:1.5b，Ollama 在线）**：①**读查 Order.cs → PASS（115s）**——`force_file_ops → small` 规则命中、历史记忆命中 3 条注入、`read_file` 正确返回 Order.cs 内容、`[完成]` 正常、退出码 0；验证链 = 路由→记忆→读工具→完成门全通。②**工程增量 ShippingService → 受控失败（198s），基础设施全通**——任务本体未做成（30B 模型多文件 ShippingService 的**已知能力上限**，代码注释"ShippingService 任务 3/3 稳定失败"即此，**非本次重构引入**），但本次重构的关键路径全部被真实打了一发且按设计工作：`use --write` → ProjectContextStore/FileGrammar 接入成功；`run_command`（ProcessRunner，P4-16）真实执行 `dotnet run` 并正确返回 `退出码：1` + CS0117 编译错 stdout；**P-D 类型冲突查询化（P1-6）在真实场景命中**——模型第 2 次修复做全量重写时被精确拦截（`检测到类型重复定义：Order/OrderService/TaxService/DiscountService` + 提示"工程已有类型…共 5 个"）；**备份落点=工作区根（P4-22）**——`.backup/c9d0d3ab/<stamp>/Program.cs` 落在 run 目录而非工程根，taskId 隔离正确；**AutoRollbackOnFailure**——`[回滚] 工程改动已还原：1 个文件恢复备份、0 个新建文件已清理`。**回滚干净性逐项核实**：Program.cs 已还原（无 ShippingService/OrderItem）、无侧路新建文件、备份内容 = 修改前原始 `class Program`（含 `class Program` 不含 `ShippingService`）。**结论**：两轮重构（P0→P4）未破坏真实任务执行；写链路安全网在模型犯错的真实场景按预期兜住；唯一"失败"是模型能力问题而非代码问题。

7d. **【已完成】P5-23 ProjectPathDetector 按域拆 partial（10-14，审查任务5-A15）**：原单文件 14 方法/308 行，按三个调用域拆为 3 个 `partial` 文件——主文件 `ProjectPathDetector.cs`（读/分析语境识别 + 自动 use 判定 + 共享词表与 CJK 判定）、`ProjectPathDetector.WriteTarget.cs`（写目标提取：TryGetWriteTarget/TryExtractFileName/TryExtractDiskRoot/LooksLikeFile + KnownFileExtensions）、`ProjectPathDetector.PathResolve.cs`（路径清洗与绝对路径解析：CleanPathSuffix/TryResolveAbsolutePath/TryResolveAbsoluteDirectory/Resolve）。**纯搬移零行为变更**（public API、方法体、词表逐字不动；partial 为同一类型，跨文件私有成员互通，调用方与 tests 零改动）。**验证**：构建 0 错误（警告基线 9）；tests 839/839（路径自动识别/写目标/盘符解析既有断言全绿，无新增无删减）。

7e. **【已完成】P5-24/P5-27 API 口径全量对齐 + 双端输入上限统一（10-14）**：①**/api/stats 全量对齐**（用户定口径"全量对齐"）——原端点只回 `summary/failures/epoch`，CLI stats 另展示 5 小节（可疑明细/Executor 生成成功率/特征类归因/路由决策/工具拒绝）但数据源各独立。实施：`DashboardOptions` 增 3 个**可选**字段（`FeatureAggregator`/`DecisionStore`/`RejectionStore`，默认 null——tests 直构不传 → 字段省略，零破坏），CLI `StartDashboardAsync` 从 DI 注入三 store；`/api/stats` 补 5 小节：`suspicious`（近 50 条过滤 IsSuspicious&&Success 取 5）、`executor`（GetServiceStatsAsync small/OrchestratorExecutor）、`featureBreakdown`（6 特征类 Get）、`routingDecisions`（decisionStore.GetSummaryAsync）、`toolRejections`（rejectStore.GetSummaryAsync）——**`JsonOpts` 为 WhenWritingNull，null 字段自动省略**（未注入即省略，语义清晰）。前端渲染为后续项（本批只做后端口径，Dashboard 数据面成为 CLI 超集）。②**双端输入上限统一 8000**（用户定口径）——原 web `/api/commands` 硬编码 2000、终端无限制。实施：`AgentOptions.MaxCommandInputLength`（默认 8000）单源；web 超限 400（文案明示上限值）；CLI 自动分发前超限打印 `[输入超限]` 提示跳过（与 web 同源同限）。**验证**：构建 0 错误（警告基线 9）；tests 839→**840/840**（+1 断言"超 8000 字符→400"；/api/stats 断言扩展为校验 suspicious/featureBreakdown 两数组，null 字段按 JsonOpts 省略已注明）。**过程修正**：首版测试误断言 executor/routingDecisions/toolRejections 三个可空键——其 null 时被 WhenWritingNull 省略，已改为断言始终存在的两个数组。

7f. **【已完成】P5-24 前端渲染（10-14）**：`/api/stats` 全量对齐的后端 5 小节补前端渲染——`index.html` `fetchStats` 增：可疑成功明细（`suspicious` 列表）、Executor 生成成功率（`executor`）、特征类归因（`featureBreakdown`，含 `topFailureCause`）、路由决策（`routingDecisions` 的 `byService`/`byFeature`，元组序列化为 `item1/item2`）、工具拒绝（`toolRejections` 的 `byCategory`）。空/无数据小节以 `length`/`total` 守卫自动不渲染（数据源未注入或为空时界面干净）。**验证**：构建 0 错误（警告基线 9）；tests 840/840（嵌入单页断言仍过；前端改动不触发 C# 测试——已逐行复核 JS 模板字面量配对，`esc` 函数复用既有定义）。

7g. **【已完成】A 级：基础设施健康探针（A2）+ Executor 提示词强化（A1，⚠ 待拍板，10-14）**：①**A2 E2E 基础设施健康探针**（纯工程化，无原则冲突）——原 `e2e.ps1` incremental 用例用 demo 基线 diff（post-rollback 状态）判 `touched`，**干净回滚后 caseDir==基线 → touched=false → 被误判"工程未接入"**（实为"接入+写+失败+回滚干净"），这正是"无法区分基础设施回归 vs 模型失败"的根因。改用日志标记（`[回滚]`/`[写入]`/`run_command`/`类型重复定义`）区分五态：完成 / 侧路假完成 / 受控失败（回滚干净）/ 安全拦截（P-D 命中）/ **基础设施回归**（写后失败未回滚，或回滚后仍有残留）；结果行附 `[回滚=.. 写入=.. 命令=.. P-D=..]` 健康信号。②**A1 Executor 修复提示词强化**（⚠ **与 DEVELOPER 铁律#1『放弃提示词工程』冲突**，效果未实测验证，待用户拍板保留或回退）——`FixFileAsync` 修复原则下增"修改已有工程文件的两条铁律"：①增量修改禁止全量重写（不把其他文件已定义的类型重新声明进当前文件 → CS0101 被拒）②只引用真实存在的成员（不凭空引用 `Order.Items`/`OrderItem` → CS0117/CS0246）。针对 10-14 实测 ShippingService 失败的两条根因（全量重写重定义已有类 + 幻觉成员）。**验证**：构建 0 错误（警告基线 9）；tests 840/840；`e2e.ps1` PowerShell 语法校验通过（未跑全 E2E——需真实模型，慢）。

7h. **【已完成】回退 A1 提示词强化（10-14，用户拍板"执行2"）**：7g 中 A1（`FixFileAsync` 增两条铁律）与 DEVELOPER 铁律#1「放弃提示词工程」冲突，用户选择回退。已删除该 9 行，`ExecutorService.cs` 恢复原样。A2（基础设施健康探针）保留。**工程化路线待续**：根因实为 30B 模型跨文件引用已有类型的**能力问题**（实测：Orchestrator 已注入 Order.cs 完整定义，模型仍幻觉 `Order.Items`/`OrderItem`），非结构缺陷；工程化杠杆为「模型 A/B」（Executor 换更强 coder 模型，库里 `qwen3-coder-local`/`task-expert`/`code-forge 7b` 未接线）或「任务分解」。**验证**：构建 0 错误（警告基线 9）；tests 840/840。

7i. **【已完成】A1 模型 A/B 使能器（10-14，工程化配置非提示词）**：根因判定（30B 模型跨文件引用已有类型的能力天花板，Orchestrator 已注入定义仍幻觉成员）→ 工程化杠杆选「模型 A/B」。实施：`AgentOptions.ExecutorModel`（string?，默认 null=沿用 Small）+ 环境变量 `LAW_EXECUTOR_MODEL` 覆盖 + `ExecutorService` 构造注入 `AgentOptions`、生成/修复两处 `settings.ModelId` 请求级覆盖（Ollama /v1 接受任意已拉取 tag，不改路由/生成逻辑）。**验证**：构建 0 错误（警告基线 9）；tests 840/840（默认 null 行为不变）。A/B 实测结果见下条。

7j. **【已完成】A1 模型 A/B 实测结果（10-14）**：用同一 ShippingService 工程增量任务对比 Executor 模型（环境变量 `LAW_EXECUTOR_MODEL` 切换；任务文本/工程副本/参数全同）。**基线 `law-unified`**（30B，默认）：198s，受控失败——Program.cs 幻觉 `Order.Items`/`OrderItem`（Orchestrator 已注入 Order.cs 完整定义仍幻觉）→ 修复 2 轮 → 第 2 轮全量重写被 P-D 拦截 → 回滚干净。**候选 `qwen3-coder-local`**（5.2GB coder）：336s，受控失败——P-D 拦截 **2 次**（同款全量重写）、一度编译通过（`run_command 退出码：0`）但验证仍失败、修复退化到 `模型输出与原文相同` → 回滚干净。**结论**：两款模型失败**同类**（跨文件复用已有类型能力不足 + 全量重写旧习），换 coder 模型**无改善且慢 70%**；安全网（P-D/备份/回滚）两轮均按预期兜住，与模型无关。**工程含义**：这是 30B 级模型的能力墙，非配置可解——单换 Executor 模型不是有效杠杆；剩余杠杆为 (a) 真正更强的模型（本地现无更大可用候选）或 (b) 任务分解（但 A/B 显示根因是"类型复用"而非"任务规模"，预期收益有限）。**A/B 使能器（`ExecutorModel` + `LAW_EXECUTOR_MODEL`）保留**，后续可随时复测其他模型。**验证**：两轮均 `[回滚]` 干净、无残留；A2 探针（e2e.ps1）会把两轮都判为「受控失败（安全网回滚）」= 基础设施健康、模型未达成。

7k. **【已完成】B2 Dashboard 全局异常处理 + D1 一键门禁（10-14）**：①**B2 全局异常处理中间件**——`DashboardHost` 原仅 3 处局部 `try/catch`、无 `UseExceptionHandler`，未处理端点异常 → **裸 500**（无结构、无日志）。实施：`MapEndpoints` 首部加 `UseExceptionHandler` → 500 + 结构化 JSON `{error}`（与端点既有 error 形状一致，前端统一消费）+ `LogError` 记录（Method/Path/异常）。**测试缝**：环境变量 `LAW_DASH_TEST_THROW=1` 时注册抛错路由 `/api/__test_throw`（生产不设置 = 该路由不存在，零影响），集成测试确定性验证中间件生效。②**D1 一键门禁 `scripts/verify.ps1`**（纯 ASCII 规避 PS 5.1 无 BOM 编码陷阱；**以退出码为准**，不解析中文输出）：构建(0 错误) → tests(0 失败) → drift-check(阈值内)，全绿 `exit 0`；+ versioned `pre-commit` 钩子（`scripts/git-hooks/pre-commit`，已安装到 `.git/hooks/`）→ 提交前自动跑门禁，失败阻断（`--no-verify` 可绕过）——把"人肉门禁"变自动。**验证**：构建 0 错误（警告基线 9）；tests 840→**841/841**（+1 B2 断言，日志确证 `ExceptionHandlerMiddleware` 捕获并记录）；`verify.ps1` 端到端 **ALL GREEN（exit 0）**。

7l. **【已完成】C1 Program.cs 命令处理器外提 partial（10-14，可维护性）**：P3-3 抽出的 10 个命令处理器（IsExitCommand / HandleNew·Help·Dashboard·Memory·Stats·Use / AutoDetectUseFromInput / HandleDiff·Rollback）当时虽已搬出 `Main`，但仍在**同一个 1904 行文件**内。实施：按域外提 `Program.Commands.cs`（372 行，`internal partial class Program`）——从 P3-3 处理器区（350 行）**字节级搬运**（保留 UTF-8 BOM 与原换行风格，零手抄，仅把 `internal class Program` 改 `internal partial class Program`）。`Program.cs` **1904→1554 行**。**纯搬移零行为变更**（同类型 partial，私有成员互通，Main 分派点不动）。**验证**：构建 0 错误（警告基线 9）；tests 841/841；无头冒烟 4 处理器（help/memory list/diff/rollback）全命中、退出码 0。

7m. **【已完成】C3 工程增量"类型清单"强约束注入（10-14，工程化数据非提示词——A/B 实测根因的工程化解法）**：根因（A/B 实测）：工程增量任务中 30B 模型**凭空引用不存在的成员/类型**（ShippingService 任务幻觉 `Order.Items`/`OrderItem`），即使 Orchestrator 事后注入 `Order.cs` 完整定义（批次3·B）仍幻觉 → 事后补救无效。实施：①**SymbolIndexer 提取单源化**——新增 `BuildContent(content, rel, style)`（与 `BuildFile` 同一实现，零新增正则），`BuildFile` 委托之；②新增 `Project/ExistingTypeInventory.Build(files, excludeRel, maxTypes)`——复用 SymbolIndexer 提取 + "按行序把成员归组到所属类型"（跨文件不误挂），产出紧凑清单（`- Order (class, Order.cs): Id, Status`）；③`ExecutorService` 新增 `BuildProjectTypeInventory`（从 `_options.ProjectRoot` 经 FileGrammar 枚举读文件；沙箱模式返 null），在 **`GenerateFileAsync` 与 `FixFileAsync` 两处事前注入**该清单（"其成员仅限所列；不得臆造"）。**关键定位：这是数据注入，不是 System Prompt 规则**——与已回退的 A1（加行为规则）本质不同，符合铁律#1"工程化/数据结构性手段"；与既有批次3·B"类型缺失注入"同源思路，但**从事后单类型改为事前全量**。**验证**：构建 0 错误（警告基线 9）；tests 841→**845/845**（+4 断言：含类型与成员 / excludeRel 排除目标 / 无类型返 null / 成员按行序归组不跨文件误挂）。**有效性实测（真跑 ShippingService）已于 10-15 完成（见 7n）**——10-14 两次未产出日志的机制定位为"长任务（本例 14.4 分钟）被执行环境转后台"；改用 detached 单进程 + 唯一日志路径（`workspace/e2e/c3_recheck.ps1`）复跑取证成功：**幻觉现象实测消除，但任务因新数据缺口（成员签名/可访问性）仍失败，成功率提升未达成**——详见 7n。

7n. **【实测完成】C3 类型清单注入有效性复测（10-15，同任务基线对照；7m 待办实测闭环）**：方法——`workspace/e2e/c3_recheck.ps1`（detached 单进程 + 唯一日志，根治长任务被转后台丢日志）跑 `use --write` + 基线同文任务 + `LAW_NO_AUTODASH=1 LAW_E2E=1`；全流程 14.4 分钟（planner 4.2min + executor 3.7min + 修复 2.8min；`task_metrics` 4 条父子记录佐证，日志 `c3run1.log`）。**对照基线（10-14 smoke_incremental，无 C3）**：`ShippingService.cs` 被"类型重复定义"拦死未写成（模型重复定义 TaxService/DiscountService）+ `Program.cs` 幻觉 `Order.Items`（CS0117）/`OrderItem`（CS0246），2 轮修复全败 → 受控失败回滚。**C3 生效结果**：①`ShippingService.cs` 成功写入 944 字符（重复定义消失）；②**幻觉成员/类型完全消失**（无 CS0117/CS0246）；③直接证据：回滚备份区保留的产物（`runs\20261015-104339-c9f2\.backup\1f48f445\105815103\ShippingService.cs`）确认模型调用的是真实方法名 `OrderService.CalculateTotal` / `TaxService.CalculateTax` / `DiscountService.CalculateDiscount`。**任务整体仍失败（新缺口）**：CS0051×2（`public class ShippingService` + public 方法签名用 internal `Order`）→ 修复轮模型重发相同内容 →"修复重试无进展"提前终止 → 干净回滚（副本与基线一致）。**本地诊断实验**：类改 internal 后暴露 CS1503×2——`CalculateTax(subtotal)`/`CalculateDiscount(subtotal)` 传 double 而真实签名是 `CalculateTax(Order)`；**证实两类数据缺口**：清单成员只有名字，无签名（参数/返回类型）、无访问修饰符。**结论**：C3"消除凭空引用"设计目标**实证达成**（幻觉从模型行为层消失，类型复用正确）；任务成功率**未达成**——失败点从"幻觉"转移为"C# 语义细节"。**下一轮候选（待决策，仍为数据注入零提示词）**：清单增强带签名与可访问性（形如 `- TaxService (internal class, TaxService.cs): CalculateTax(Order): double`）。**附带发现**：`orchestrator_sub` 子记录 `is_e2e=0`（LAW_E2E 未继承子记录；有历史回填特征兜底，stats 口径暂不受影响，未修）。
7o. **【实测完成】工程增量任务完整闭环 + 普适性变体验证（10-15，7n 后续；全程数据注入零提示词）**：清单增强#1（cec0f68）类型签名+访问修饰符注入（SymbolEntry.Signature + symbol_index 表 signature 列 + ExistingTypeInventory 输出 internal/public 与成员签名）→ 消除 7n 缺口 CS0051×2/CS1503；清单增强#2（0500bc1）无修饰符类型默认 internal（C# 顶层默认语义补全）→ 消除 internal 泄漏 public 签名；验证层（3f419f6）工程增量 expected_output 降 advisory——编译/运行验证命令（dotnet run/build/test、python、node）跳过 expected 精确断言（Planner 自产期望无工程数据基准，实证真实 1.03/1.06 vs 计划 100.00），退出码/stderr/结构性错误/失败信号检查全保留；E2E 断言（b126d68）完成场景保留产物非脏残留（dirty 仅用于失败回滚后残留=回滚失效判定）；失败点迁移链固化（2e12152）LocateErrorFile 回归断言（CS0120 定位/含空格路径/Python/入口兜底）；变体用例集（e2e.ps1 增量 4 用例：新类复用两服务 / 修改既有方法 / 新类调服务链 / 静态工具类）；TaskClassifier 缺口（2513716）工程可写+文件引用→编排——5.5 原把引用 Program.cs 的工程增量任务误拦出编排（模型裸奔直连 AgentLoop→读文件后连续空响应终止），工程可写模式下文件引用=工程增量任务（新增类+改入口+验证）必须走 Planner 规划与验证闭环，非工程可写保持原行为。**复测结果（10-15 13:48）**：E2E **7/7 全绿**——ShippingService 三连完成（68/69/121s）；变体 C/D 一次编译通过完成（62s/51s）；变体 B 受控失败（P-D 拦截：模型改既有文件带重复类型定义，安全网正确拦截，属模型纪律缺口非架构缺陷）；单文件/多文件/读查无回归。tests 845→**864** 全绿。demo 基线重置：删除历史压测遗留 BigService.cs（267KB 方法轰炸文件），lawcheck_demo=6 文件干净基线（dotnet run 输出 订单总金额: 103）。**遗留**：变体 B（改既有文件）模型纪律弱（整文件重写带重复类型）→ P-D 拦截兜底（非提示词解决）；advisory 全局放宽对"任务输入自带精确输出要求"的漏判面未验证。
7p. **【完成】开源推广主线（10-15，7o 后续）**：GitHub 改名 LocalAiWorkbench → **LocalCodeForge**（BMDPLAinsong/LocalCodeForge，remote set-url；本地代码层 slnx/命名空间不动，只改远端）。定位唯一卖点=「把工程感知自动修复闭环做成数据」；产出并推送英文 README（数据注入/回滚/对比/快速开始 + demo.gif 占位）、docs/show-hn-post.md（Show HN 帖）、LICENSE（MIT，BMDPLAinsong）——提交 37387c6（3 files +162，pre-commit 864/864 通过）。打 annotated tag v1.0.0（2c42bc6）并推送；GitHub Release v1.0.0 经 API 创建（id=413658141，URL=github.com/BMDPLAinsong/LocalCodeForge/releases/tag/v1.0.0，完整文案 name 59/body 1431，非 draft）。坑：PS5.1 Invoke-RestMethod 直接传含非 ASCII（em dash）的 string body → GitHub 400；改用 [Text.Encoding]::UTF8.GetBytes 字节体发送成功；凭据经 git credential fill 取（40 字符，进程内使用不落盘不显示）。待办：demo.gif 真实录屏替换占位、awesome 列表 PR、Show HN 发帖。
7q. **【已完成】P0 Web Dashboard 优化四项（10-15，7p 后续；全部 Dashboard 层，CLI 路径零改动保留兜底）**：①**品牌一致性**——index.html 页签 + h1 标题 LocalAI Workbench → **LocalCodeForge**（推广改名同步，无旧名残留断言）。②**健康状态面板**——新增 GET /api/health（模型端点 classifier/small/large/agentPro 各 provider:model@endpoint + temp、executor 覆盖标签、工程根 + 可写标志、epoch、routing/strictOutput/toolLayering/taskTimeout/maxSteps 运行开关）；**数据全来自注入的 AgentOptions + ProjectRoot（无新状态源、无并行口径）**；前端 header 常显 healthLine（fetchHealth，随 refreshAll 轮询）：law-unified · 工程:X:\...（可写/只读）· 路由开 · 严格验证关。③**任务完成浏览器通知**——SSE 实时流 inalAnswer/error 事件挂钩 
otifyTaskDone → Web Notification（tag 固定 lcf-task-done 新替换旧）；权限在用户**主动提交任务时**请求（非页载弹窗）；纯前端、无服务端改动。④**历史任务搜索/过滤 + 一键重跑**——/api/tasks 加 q（user_input/error_summary 关键词）/ status（ok/fail/susp）/ since（started_at≥ISO）三参数，**端点内结构性过滤（上限内内存过滤，SQLite 查询层不扩散接口），无过滤参数行为与历史完全一致**；前端任务卡片加 	askQ 输入 + 	askStatus 下拉 + 筛选按钮；行内**重跑**按钮复用 POST /api/commands 提交链路（stopPropagation 防触发行回放）。验证：dotnet build --no-incremental 0 错误（9 个既有 nullable 警告未新增）；tests **875 通过 / 0 失败**（基线 864 → +11：5 条页面断言 + 6 条 API 过滤/健康断言）；E2E 复测由用户决定。5. **清理 RoutingRules.json 重名规则**，合并语义；**EnableOrchestration 接线或删除**。

7r. **【已完成】Dashboard v2 视觉重构（10-15，7q 之上：视觉系统 + 交互细节；单文件架构与全部测试契约不变）**：**设计上下文（用户确认）**——受众 = 本人长时间盯屏 + 对外演示；方向 = 现代精致暗色（"精密仪器"感：暗但不黑、层次靠明度阶梯与语义色条，拒绝"万物卡片 + 通用阴影 + 霓虹点缀"的泛 AI 审美）。实施：①**CSS 设计系统全量重写**（165→357 行）——oklch 中性阶梯（`--bg/--bg-sunk/--panel~--panel-3/--border~--border-2/--fg~--faint`）+ 低饱和语义色（绿/红/黄/蓝/青）+ 双字体栈（UI 用 Segoe UI Variable/system-ui，数据与代码用 Cascadia Code/JetBrains Mono/Consolas）+ 4px 基准间距与 clamp 流式尺寸 + 统一缓动 `--ease`(ease-out-quart)；卡片改"极细边框 + inset 顶部高光"替代通用投影；事件行改**左侧 2px 语义色条**（扫描效率 > 全行染色，久看不疲劳）；卡片 h2 改"小字号 + 大字距 + 大写"仪表标签；品牌 h1 加斜切方块标记（几何记忆点，非图标字体）。②**DOM 内联硬编码色收敛**：cmdInput / evFilter / taskQ / depFile / taskStatus / filesPreview 共 7 处内联 `#0d1117/#1a1f2e/#2f3a52` → 统一 `.inp`/`.sel` class（顺带补齐此前**未定义却被引用**的 `.dim`）。③**空状态 / 错误态结构化**：新增 `emptyState(title, desc, {err, action})` 四段式（标记 + 标题 + 说明 + 行动），替换 events / dep / stats / tasks / deps 共 6 处"一行灰字"——只说"没有"改为说清"现在什么状态 + 下一步能做什么"；错误态走 `is-err` 红通道并给"重试 / 清空筛选 / 显示全部"按钮。④**键盘可达**：此前全站 **0 处 aria、0 条 :focus 样式**，可点击 div/span（`.tc-head`/`.tp-call`）只有 onclick → 补 `role=button` + `tabindex=0` + Enter/Space 全局 keydown 委托 + `:focus-visible` 描边。⑤**微交互**：按钮 hover/active 位移、过滤按钮 active 改下标线（非实心块）、卡片展开 reveal、提交按钮 `is-busy` 细进度条、在线状态点呼吸动画，并加 **`prefers-reduced-motion` 兜底**。⑥**响应式**：新增 720px 断点（命令条换行、窄屏隐藏次要副标题），保留 1100px 单栏。**红线与验证**：单页被 `Suites_Dashboard.cs` L251–294 以**逐字节**契约锁定 40 项（含 `<div class="colL">\n  <div id="cmdBar"`、`#cmdBar { position:sticky; top:var(--headH)`、Monaco `paths: { vs: "/vendor/monaco/min/vs" }`、禁含 `LocalAI Workbench` 与 `});\n}\n}\n// ── stats ──`）→ 改造后**契约 0 缺失 / 0 违禁**（校验脚本 `workspace/e2e/apply_css.py`）；旧 CSS 变量名必须保留（DOM 内联仍引用 `var(--fg/--border/--dim/--blue)`），新增变量只做补充。**验证**：构建 0 错误（警告基线 9）；tests **875/875**；drift OK。**预览**：Dashboard 有无头守卫（`Console.IsInputRedirected` → 拒绝启动 Kestrel，保护 E2E 输出逐字节兼容）→ 无头环境改走 mock 预览服务 `workspace/e2e/dash_preview.py`（静态 + mock `/api/stats|tasks|health|deps|files` + SSE 真实事件序列），本机 `http://127.0.0.1:8777/` 验证视觉与交互。

7s. **【迭代】Dashboard v3 视觉打磨（10-15，7r 之上：治"配色太灰太闷" + "像工具不像产品"）**：用户复盘两点反馈——① 暗底 + 低饱和语义色缺视觉重心；② 整体气质太克制/冷淡、缺个性记忆点。**方向（用户指定）**：更大胆抓眼（演示优先）+ 精致高级感。**v3 仅换 CSS 设计系统，DOM/JS 契约不变**：①**拉开对比**——底色压深（16.5%→12.5%）+ 正文提亮（91%→97%）+ 语义色饱和度整体上调（绿 .15→.19 / 红 .17→.215 / 青 .11→.165）；②**双强调色**——主强调电光青（品牌/交互/焦点一贯色）+ 次强调琥珀金（呼应"锻造"命名，仅用于品牌标记与完成态辉光）；③**事件流做成"显示面板"**（下沉底 + 内阴影）成页面视觉重心；④**去终端感**——按钮/表头/空态文案改 UI 字体（此前全继承等宽），卡片标题去大写大字距改 13px/600 + 青色竖条，状态信息改胶囊标签；⑤**大胆但克制点缀**——渐变仅 4 处（品牌斜切方块标记[金青渐变+微光] / header 底部渐变分隔线 / 主按钮渐变+辉光 / 页面顶部环境光），避霓虹廉价感；⑥ 精致细节——双层阴影(接触+扩散)+inset 顶部高光、数字 tabular-nums、8px 统一圆角、focus 环、hover 位移。验证：构建 0 错误（警告 9）；契约 0 缺失/0 违禁（apply_css.py 重校验）；tests 875/875；drift OK。预览：mock 服务 `workspace/e2e/dash_preview.py`（http://127.0.0.1:8777/）。
7t. **【已完成】Web Dashboard 定为主交互入口（10-15，用户指令"先执行主入口，再看P0"）**：**事实核查**——自动启动骨架 10-10 已存在（交互终端启动即自动起 Dashboard + 自动开浏览器；LAW_NO_AUTODASH=1 关闭开关；无头守卫 [管道/重定向跳过，E2E 输出逐字节兼容]；幂等 [重复 dashboard 命令只提示不重启]；CLI 输入与 Dashboard POST /api/commands 统一汇入 CommandHub 一次一任务串行）。本次"执行"= 补齐**定位表达与文档一致性**（零行为变更，不重复造启动逻辑）：①Program.cs 启动横幅明确"**主交互入口：Web Dashboard**（已自动启动）…CLI 保留兜底：use/stats/memory/rollback/plan/orch/new/exit/help 照常可用"；②Program.Commands.cs help 文本 dashboard 项标注"**主交互入口 Web UI**"；③README.md 快速开始改为 "**The web dashboard is the primary interaction surface**: the CLI auto-launches it on startup (loopback + token) and opens your browser… The CLI itself stays as a fallback (pipeline/headless safe)"。**验证**：build 0 错误（警告基线 9）；tests **875/875**（纯展示文本零行为变更，无测试面变化；E2E 无头路径不触发自动启动分支，输出不受影响）。**P0 在主入口视角再审视**：四项（品牌 / /api/health / 通知 / 筛选重跑）全部服务 Web 主入口且无缺口；唯一结构性风险 = /api/tasks 过滤为**上限内内存过滤**（当前 50 条上限，刻意不扩散 SQLite 查询层），任务量放大后需下沉查询层。
7u. **【已完成】Dashboard 前端 JS 语法根因修复 + 错误态反馈 + no-store + 测试幂等 + JS 语法门禁（10-16）**：用户反复报告「加载中…/点提交无响应」（6 轮截图）后 Console 证据 `Uncaught SyntaxError ...:906` 锁定**最终根因——index.html L906 `emptyState("…点"加载"重建索引。")` 字符串内嵌未转义英文双引号 → 整段 `<script>` SyntaxError → 全部 JS 失效**（SSE/提交/状态行/刷新全静默；既有 bug，契约测试只查 HTML 字符串不查 JS 语法一直漏网，与 10-10 同因不同处）。修复：`点"加载"` → `点「加载」`（node --check 全量语法校验通过）。**配套工程化**：①**前端错误态反馈**（此前失败路径全静默）——fetch 全局 12s 超时（AbortController）+ `wrapFetch` 统一把网络失败转 `{ok:false,status:"连接失败/连接超时"}` 复用 `!r.ok` 错误 UI；SSE onopen/onerror 驱动顶部状态行（「在线（实时流已连接）」/「实时流连接异常（自动重连中…）」）；token 缺失时状态行提示；fetchHealth 失败显示「状态不可用」。②**GET / 加 `Cache-Control: no-store`**（页面随服务版本内嵌，杜绝浏览器缓存旧页面——"重启后仍旧界面"根因之一）。③**测试幂等修复**——`Suites_Dashboard.RunEventSchemaVersion` 固定路径 `new.db/old.db` 复跑残留 → `table task_events already exists` 崩溃（本批全量重编实测暴露）；改 `Guid.NewGuid().ToString("N")` 唯一化。④**JS 语法门禁**——`scripts/verify.ps1` 新增 3/4 步：python 提取 index.html `<script>` → `node --check`（纯 ASCII；node/文件缺失跳过）；pre-commit 提交前自动全绿。**验证**：build 0 错误（警告基线 9）；tests **875/875**；node --check 通过；commit `caf566d` → 远端 `9089ca5d`（300 blob 全量对齐 + tree 校验一致）。**教训**：大 HTML 的字符串内嵌引号是 JS 崩溃温床，字符串断言抓不住 → 语法门禁进 pre-commit（本批落地，防打地鼠）。
7v. **【已完成】项目全量改名 LocalAiWorkbench -> LocalCodeForge（10-16，用户指令"全都改完不要遗漏"）**：仓库目录 Move-Item 为 X:\.NET AI\LocalCodeForge（git remote 不变）；CLI 品牌横幅/框线、slnx git mv、README/DEVELOPER/DEVELOPER_DETAILED 清零；HANDOVER 标题+速览表+构建命令同步（7p/7v 迁移记录保留旧名）；Suites_Dashboard use 样例路径与 patch_handover_b8.py 路径同步新目录；verify.ps1 新增 5/5 rename-gate（scripts/rename-gate.py，白名单仅 HANDOVER 迁移记录，防打地鼠）；验证 build 0 错误 / tests 875/875 / JS / drift / rename-gate 全绿；本地 commit 5be85b9 -> 远端 4f05fb04（tree 全量一致校验）。

6. `git init`**&#x20;纳入版本控制**；为 TaskClassifier / RuleEngine / TryParseToolCallsFromText 补单元测试。

7. **远期**：CLI 之外的前端（AgentEvent 事件流已为 UI 预留；Routing 事件、指标统计都已齐备）。

### §12 附：回滚安全交互矩阵（已审计 · 2026-10-03 P-A/P-B/P-C 批次）

> 工程可写模式（use --write）下，备份/新文件标记按 `taskId` 隔离（`.backup/<taskId>/<stamp>/<rel>` + `.backup/<taskId>/.new/<rel>`），
> 回滚取【最早】备份（任务前干净状态），成功任务收尾清空本任务备份与标记。
> 本矩阵覆盖全部跨任务交互场景，均已在 tests 267 项中固化（场景 1/2/3 + P-A + dry-run）。

| # | 任务序列 | 备份/标记行为 | 失败回滚结果 | 测试断言 |
|---|---|---|---|---|
| 1 | A 成功新建文件 → B 失败 | A：MarkNewFile(taskA)→成功收尾清 taskA；B：MarkNewFile(taskB) | 只回滚 taskB：B 新建文件删除 | ✅ 场景1（A 文件保留、B 清理） |
| 2 | A 成功改文件 → A 再改同文件 → B 失败改同文件 | A 备份(taskA) + 成功清 taskA；A 再改新备份(taskA)；B 备份(taskB=含 A 二次改动) | 只回滚 taskB：恢复 taskB 备份=**A 第二次改动保留**（B 不碰 A 的备份） | ✅ 场景2（v2 保留） |
| 3 | A 失败建 X.cs → B 成功建同名 → C 失败 | A：标记+回滚（删 X.cs 清 taskA）；B：标记+成功清 taskB；C：失败 | 只回滚 taskC：**B 的 X.cs 保留**（C 不误删） | ✅ 场景3（class X2 保留） |
| 4 | 任务内多次写入同一文件（初写+修复重写） | 每次写入前备份（毫秒 stamp 区分） | RollbackTask 取**最早备份**=任务前干净状态（**P-A 修复**：旧实现取最近备份=任务中间坏状态） | ✅ P-A（恢复 v0 非 v1） |
| 5 | 同秒多次写入 | stamp=HHmmssfff（毫秒） | 备份不覆盖（**P-C 修复**） | 代码级（毫秒粒度） |
| 6 | rollback --dry-run | 预览恢复/删除列表不执行 | 展示影响面后人工确认 rollback all | ✅ dry-run（不执行任何改动） |

**已审计结论**：回滚数据安全闭环——任务隔离（P-B）、方向正确（P-A 最早备份）、时间粒度（P-C 毫秒）、跨任务互不误伤（H1/P-B）、预览确认（dry-run）、成功后清理（成功收尾）。
**已知边界（P-D）**：30B 模型增量修改纪律弱（偶发把多类塞进单文件全量重写）——依赖编译验证拦截 + 回滚兜底，非提示词解决（用户铁律）。
**端到端实证（10-03）**：工程模式失败任务真实触发 `[回滚] 2 个文件恢复备份、1 个新建文件已清理`，回滚后 demo 编译 0 错误（恢复到任务前干净状态）；`rollback --dry-run` 正确展示恢复/删除列表。



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