# LocalAiWorkbench 开发者维护文档

> 面向**后续维护与开发者**。读者：接手本内核做功能开发、缺陷修复、性能优化的人。
> 撰写：2026-10-04（基线：git main @ e94e2a8 + 本地待提交、tests 353/353、构建 0 错误）。
> 与 `HANDOVER.md` 的关系：HANDOVER 是**交接与变更史**（谁在何时做了什么、踩了什么坑）；本文是**开发手册**（系统怎么工作、改哪里、怎么改、怎么验证）。开发前两者都读。

---

## 0. 铁律（用户的硬约束，违反即返工）

1. **放弃提示词工程**。一切缺陷修复优先用**工程化 / 数据结构性**手段（确定性代码、词表、表结构、工具行为约束、拒绝消息），禁止靠堆 System Prompt / 修复器规则"打地鼠"。
2. **任何修改先出方案，用户确认后才动手**。方案要写明：改什么、为什么、影响面、验证方式。
3. **只用本地模型**。云端（DashScope）仅预留未来兜底，当前不使用；模型形态已固化为**单 tag `law-unified`（30B）三角色**，不再额外拉取模型。
4. **测试不落 C 盘 Temp**。统一落 `X:\.NET AI\LocalAiWorkbench\workspace\test-tmp\<PID>`（已 gitignore）。
5. **诚实失败优于假完成**。任务做不了就如实失败（走 H2/H4 防线），禁止"工具已验证"式假完成。
6. 批量改动后必须：构建 0 错误 → tests 全过 →（涉及模型行为时）E2E 复测 → 同步 HANDOVER/DEVELOPER → git 提交（本机仓库，沿用现有 Git 配置）。

---

## 1. 项目概览

| 项 | 值 |
| --- | --- |
| 性质 | 纯本地 AI 编码助手内核（控制台），用户输入自然语言 → 智能路由 → 规划/生成/写文件/运行验证/自动修复闭环 |
| 解决方案 | `LocalAiWorkbench.slnx`（新版 XML solution） |
| 目标框架 | net10.0（**勿降级**） |
| 核心依赖 | Microsoft.SemanticKernel 1.80.1 + SK OpenAI Connector（走 Ollama `/v1` 兼容端点，全本地）+ Microsoft.Data.Sqlite |
| 模型 | Ollama：classifier=`qwen2.5:1.5b`；small/large/agent_pro 三角色全部 = `law-unified`（30B Qwen3-Coder-A3B，同 tag 单驻，请求级 temperature 区分：small 0.3 / agent_pro 0.5 / large 0.7） |
| 版本控制 | 本机 git 仓库（main，91+ commits，无远端） |
| 测试 | `tests/Agent.Infrastructure.Tests`（**零依赖断言 harness**，372/372，`dotnet run --project` 一键回归） |
| 数据落盘 | `workspace/`：sessions.db、runs/、project_context.db、agent_memory.db、symbol_index.db、test-tmp/、e2e/ |

### 1.1 三层项目结构

| 项目 | 职责 | 依赖 |
| --- | --- | --- |
| `src/Agent.Core` | 抽象接口（IAgentLoop/IAgentTool/IToolRegistry/ISessionStore）+ 纯模型（AgentOptions/AgentEvent/Plan/TaskMetric/SystemPrompt 等），零依赖 | 无 |
| `src/Agent.Infrastructure` | **全部实现**：AgentLoop、Orchestration、Routing、Project、Tools、Metrics、Memory、Quality、Session、Ollama | Core + SK + Sqlite |
| `src/Agent.Cli` | 控制台入口：交互循环、命令分发（use/plan/orch/stats/memory/rollback）、事件渲染、健康检查、run 目录清理、任务归档 | Core + Infrastructure |

---

## 2. 快速上手

```powershell
Set-Location "X:\.NET AI\LocalAiWorkbench"

# 全量构建（注意：必须 slnx 全量，不能只 build CLI，见 §9 构建坑）
dotnet build LocalAiWorkbench.slnx --no-incremental

# 确定性回归（零依赖，几秒）
dotnet run --project tests\Agent.Infrastructure.Tests\Agent.Infrastructure.Tests.csproj

# 运行 CLI（Ollama 未启动会自动拉起）
dotnet run --project src\Agent.Cli
```

CLI 常用命令（交互式输入）：

| 输入 | 作用 |
| --- | --- |
| 普通文本 | 作为任务交给 Agent（路由 → AgentLoop 或 Orchestrator） |
| `use <路径>` | 进入工程模式（只读感知）；`use --write <路径>` 可写模式（改动前自动备份） |
| `plan <需求>` | 只跑 Planner，看规划 JSON（不带模型执行） |
| `orch <需求>` | 强制走 Orchestrator 编排路径 |
| `stats` | 任务指标汇总（成功率/失败类别/特征归因/时代隔离/可疑成功明细/工具拒绝） |
| `memory` / `memory list` / `memory forget <id>` / `memory prune [days] [minhits]` / `memory dedup` | 模型记忆管理 |
| `rollback [rel]` / `rollback --dry-run` | 任务级回滚（无参=最近任务全量，--dry-run 预览） |
| `exit` / `new` | 退出 / 开启新会话 |

---

## 3. 架构全景

```
用户输入
  │
  ▼
CLI（Agent.Cli/Program.cs：BOM 清理 → 命令分发 → 健康检查 → run 目录清理）
  │
  ▼
任务分发（Agent.Cli）：
  ├─ TaskClassifier（Orchestration）：长度/关键词判定 → 走 B 编排 或 A ReAct
  └─ 默认 → A AgentLoop
  │
  ▼
┌─ A. AgentLoop（Agent.Infrastructure/AgentLoop.cs）—— ReAct 单循环
│    · 启动：记忆检索注入（LoadRelevantMemory）→ 工程上下文注入（签名缓存）
│    · 循环：模型 → 工具调用 → 工具结果 → 检查（验证/熔断/升级/强制写/防假完成）
│    · 防线：空响应升级链（small→agent_pro→large，直达跳层）、循环熔断、
│           模板检测、H2/H4 防假完成、工程完成门
│
└─ B. Orchestrator（Orchestration/Orchestrator.cs）—— Planner → Executor → Validator
     · PlannerService：生成结构化 Plan（JSON）+ 工程图谱块注入 + 计划校验（CheckIncrementalPlanConflicts）
     · ExecutorService：逐文件生成/修复（失败升级链 + P-D 拦截引导）
     · 验证：run_command + OutputVerifier 严格校验（工程模式默认严格）
     · 失败：自动回滚（AutoRollbackOnFailure）+ 失败教训归档
  │
  ▼
路由（ModelRouter 三层漏斗，见 §4）决定模型角色
  │
  ▼
工具层（10 工具，见 §5）执行文件/命令/分析操作
  │
  ▼
SQLite 数据层（sessions/metrics/project_context/agent_memory/symbol_index，见 §7）
```

---

## 4. 路由体系详解（Routing/）

**文件**：ModelRouter.cs、RuleEngine.cs（+RoutingRules.json）、IntentClassifier.cs、IntentOverride.cs、RoutingIntelligence.cs、TaskFeatureClassifier.cs、TaskMetricsAggregator.cs、RoutingDecisionStore.cs、ExternalTaskDetector.cs 等。

### 4.1 漏斗层级（ModelRouter 顺序判定）

| 层级 | 判定 | 动作 |
| --- | --- | --- |
| L0 | 查询词表（问/是/吗/什么…） | 直连 small（问答） |
| L0.5 | `IntentOverride.IsWriteTask`（写动词+代码/文件语境） | **写任务直连 large**（绕开 small/agent_pro 空响应链） |
| L0.5b | `IsReadTask` + RoutingIntelligence 读任务数据闭环 | 规则命中 small 且读任务 → 查历史达标则直连 agent_pro |
| L1 | RuleEngine 8 条规则（RoutingRules.json：complex_local/recovery/force_code_simple/force_file_ops/force_code_edit/search_keywords 等） | 按优先级命中 → 对应目标 |
| L2 | IntentClassifier 意图分类（SimpleQA/CodeGenerate…）+ IntentOverride 确定性覆盖 | 操作动词+文件语境强制 CodeGenerate，防问答模式不调工具 |
| L3 | 分发到最终角色（small/agent_pro/large） | — |

### 4.2 数据闭环（RoutingIntelligence）

- **L0.5 写任务降级**：`TrySuggestDowngrade`——候选（small）样本≥3、成功率≥0.6、平均耗时<当前（large）才降级；不满足一律保持 large（防打地鼠）。
- **L0.5b 读任务升级**：`TrySuggestReadTier`——保守四条件全满足才 small→agent_pro。
- **数据源**：task_metrics（service_id/routing_tier/success/total_ms）+ routing_decisions（每次路由决策落库）。
- **失败归因**：TaskFeatureClassifier（工程增量/外部凭据/多文件/单文件/读查/其他）+ TaskMetricsAggregator（启动聚合，30s 缓存）；Orchestrator 按特征类历史成功率校准重试上限（工程增量<50% → 3→1）。

### 4.3 词表漂移（重点维护项）

同一功能逻辑在**多处有硬编码词表**，历史上多次因"一处改了另一处没改"导致双路径不一致：

- `IntentOverride.OperationVerbs`（写动词：写/更改/改/改为/改变/变更/换成/重写/生成/追加/覆盖/清空/格式化/打印/输出/加/注释掉…）+ `ReadVerbs`
- `AgentLoop.looksLikeCodeRequest` —— **已对齐复用 `IntentOverride.IsWriteTask`**（不再独立维护）
- `TaskClassifier` 查询/项目词表、`ExternalTaskDetector` 外部任务词表、`RoutingRules.json` 正则

**改词表时**：搜索全部词表位置一次改齐，并补 tests 断言（防回归）。

---

## 5. 工具层清单（Tools/，10 工具）

| 工具 | 文件 | 职责与关键行为 |
| --- | --- | --- |
| `list_dir` | ListDirTool.cs | 列目录；双根越界检查（任务根/工程根） |
| `read_file` | ReadFileTool.cs | **行级分页**（offset/limit，默认 500）；大文件（>100KB）无 offset 拒绝 + 符号定位引导；分页头附 find_symbol/analyze_code 提示；**hasOffset 收紧**（仅 limit 不算分页意图，防绕过） |
| `write_file` | WriteFileTool.cs | 写文件；**P-D 类型冲突拦截**（工程已有类型名重复定义拒绝 + 类型清单 + 动作指引）；P-D 熔断（同路径拒≥2 次 → 强制 edit_file 指令）；工程可写自动备份 + MarkNewFile；.py 强制 HTTP timeout=8；项目名幻觉校验；tool_rejections 落库 |
| `edit_file` | EditFileTool.cs | 精确替换；old_string 未命中查备份漂移诊断；冲突检测；P-D 拦截；备份 |
| `create_dir` | CreateDirTool.cs | 建目录；双根检查 |
| `run_command` | RunCommandTool.cs | 命令执行；**白名单**（dotnet/python/type 等）；工程模式路径边界（..\ / 盘符绝对路径越界拒绝）；cwd=工程根（工程模式）；超时双轨（沙箱 30s/工程 90s）；交互拦截（PyCodeGuard）；项目名幻觉校验 |
| `analyze_code` | CodeAnalyzerTool.cs | 代码符号地图（class/方法/属性+行号，正则确定性）；支持大文件（200KB→5MB）；非代码文件降级提示 |
| `find_symbol` | FindSymbolTool.cs | 符号定位（symbol_index.db，惰性索引 + **Find 前增量刷新**）；命中返回 [文件:行号] + 操作指引（read_file offset 建议） |
| `analyze_deps` | DependencyAnalyzerTool.cs | 文件依赖/反向依赖（改动影响面）；复用 symbol 索引 |
| `remember` | RememberTool.cs | 模型主动存发现/决策到 agent_memory（kind=model） |

守卫：`PyCodeGuard`（.py input / .cs Console.ReadLine 写入即拒绝，防 30s 挂死）+ `AgentToolBase`（基类）。

---

## 5.1 渲染层（Rendering/，Spectre P0+P1+P2 最终态）

**职责**：CLI 壳与未来 UI 宿主共用的渲染契约；事件格式化**单一来源**（防双路径漂移）。

| 文件 | 职责 |
| --- | --- |
| `IRenderer.cs` | 渲染契约：Banner/ConfigBlock/Info(DarkGray)/Notice(Magenta)/Warn(Yellow)/Error(Red)/System(无色)/Prompt/ReadLine/**RenderEventAsync**（异步签名） |
| `RendererBase.cs` | Console 直出实现（无头/默认，与旧 CLI 逐字节兼容）+ **纯函数 `FormatEvent`（七事件→文本，唯一事件格式化来源）/Truncate/FormatToolArgs**（tests 直接断言） |
| `PlainTextRenderer.cs` | 无头路径（管道/重定向/E2E），逐事件 Console 输出 |
| `SpectreRenderer.cs` | 交互路径：Panel 横幅/配置块 + **追加输出事件流**（关键节点 Panel + 高频事件彩色行）+ P2 命令块面板（StatsPanel BarChart / MemoryTable / BackupTable / RollbackPrompt 多选） |
| `RendererFactory.cs` | `Create(interactive)`：false→PlainText / true→Spectre；**交互分支写入 `AnsiConsole.Profile.Width = Math.Max(80, Console.WindowWidth)`**（宽度根因修复） |

**无头判定**（Program.cs 装配）：`!Console.IsInputRedirected && !Console.IsOutputRedirected` → 交互；管道/重定向 → PlainText（E2E 逐字节兼容）。

**改渲染格式时**：只改 `RendererBase.FormatEvent`（纯函数），PlainText/Spectre 自动同步；必须补 tests 断言（七事件映射已固化，防回归）。

**交互态事件形态（10-04 最终收敛）**：**仅路由决策 / 任务完成 / 任务失败用 Panel**（黄/绿/红，`.Expand()` 撑满宽度）；规划/写入/工具调用/工具结果用彩色 MarkupLine（防一次任务 20 框视觉碎片）。文本一律 `Markup.Escape(FormatEvent(...))`——事件文本含 `[路由]` 等方括号，不转义会被 Spectre 解析成颜色标签。

**教训固化（真终端复验驱动）**：①任务事件是**单向追加流**，不是原地刷新进度条——**禁用 LiveDisplay 类重绘 API**（初版 Channel+LiveDisplay 落地后实测残留：漂浮竖线/绿框缺底边/"任务完成"分隔线有起点无终点，前后帧叠加所致）；②终端 UI 边框类缺陷**先怀疑宽度计算**再归因渲染 API——真根因是 Spectre 误算终端宽度（认为 80 列，120+ 列内容边框错位），修复 = 初始化处显式写 `Console.WindowWidth` + 关键 Panel `.Expand()`；③一个事件一个渲染入口（最终视图灰显区取 final 之前的事件，无重复输出）。

---

## 6. 工程感知与安全机制（Project/）

| 机制 | 文件 | 说明 |
| --- | --- | --- |
| 只读扫描 | ProjectScanner.cs | 文件树/入口/依赖识别，产出工程上下文数据块（CompactBlock 注入瘦身版） |
| 工程缓存 | ProjectContextStore.cs | project_context.db + 4 元签名（FileCount/DirCount/TotalBytes/MaxMtime）——签名一致命中缓存，变更自动重扫 |
| 可写回滚 | BackupService.cs | **taskId 隔离**：备份 `.backup/<taskId>/<stamp>/<rel>` + 新建标记 `.new/<rel>`；RollbackTask 取**最早**备份；P-A（中间坏状态）/P-B（跨任务误删）/P-C（同秒覆盖）均已修；PruneBackups keepLastN=8；DryRunRollbackTask |
| 符号索引 | SymbolIndexer.cs + SymbolIndexStore.cs | 正则提取类型/方法/属性；SQLite 持久化；**增量刷新**（mtime 对比，只重扫变更文件） |
| 依赖分析 | DependencyAnalyzer.cs | 类型级符号 + 引用扫描 → 文件依赖边；analyze_deps 工具入口 |
| 类型冲突 P-D | TypeConflictDetector.cs | 写前检测新内容含工程已有类型 → 拒绝（class/interface/enum/struct/record，剥离注释/字符串防误报） |
| 测试门 | TestProjectDetector.cs + TestRunnerService.cs | 检测测试入口（*.Tests.csproj/pytest/package.json test）；工程模式改代码后自动跑测试 |
| 计划校验 | Orchestrator.CheckIncrementalPlanConflicts | 工程增量任务 Plan 中"已有文件≥2" → Planner 阶段拒绝（防整体重写） |

### 防假完成防线（AgentLoop）

- **H1**：回滚跨任务误删修复（.new 标记 taskId 隔离 + 成功清理）。
- **H2**：熔断收尾加完成门槛——写任务未写过文件 → 判失败。
- **H3**：工程模式默认严格校验（StrictOutputVerification || ProjectRoot 非空）。
- **H4**：熔断收尾——写任务"写过文件但最近 run_command 未成功" → 判失败（防假成功）。
- 循环熔断：`BuildCycleSignature`（工具名+首参+输出指纹）同一签名二次成功 = 重复无进展 → 以工具验证结果收尾或诚实失败。

---

## 7. 数据落盘（workspace/）

| 库 | 表 | 内容 |
| --- | --- | --- |
| sessions.db | sessions / turns | 会话与对话历史 |
| sessions.db | task_metrics | 任务指标 19 字段：service_id/routing_tier/mode/success/total_ms/error_summary/**failure_category**/is_suspicious + 时代隔离（started_at） |
| sessions.db | routing_decisions | 每次路由决策（L0/L0.5/L1… 命中规则/目标/结果） |
| sessions.db | tool_rejections | 工具拒绝事件（tool/category/path/task_id/message 截断 200） |
| project_context.db | project_context | 工程概览/上下文块/紧凑块 + 文件签名 |
| agent_memory.db | agent_memory | 记忆：kind（model=模型发现/task=任务归档）、topic/content/feature/hit_count/last_hit_at；**同 kind+topic 合并去重** |
| symbol_index.db | symbols | 符号（project_root+symbol 复合主键 + file_mtime 增量基准） |
| runs/<run>/ | — | 每会话一次运行的工作目录（工具根 = run 根，任务产物跨任务共享）；`.backup/` 备份 |

**生命周期**：runs 保留最近 10（启动 CleanOldRuns）；test-tmp >7 天清理；备份 keepLastN=8；记忆 prune（默认 90 天/2 命中）。

---

## 8. 配置（AgentOptions + Program.cs + Modelfile）

- **AgentOptions.cs**（Agent.Core/Models）：全部可调参数——ModelEndpoint 列表、ProjectRoot/ProjectWriteEnabled、StrictOutputVerification、LawUnifiedEpoch（时代隔离 2026-10-02 23:00）、MemoryPruneDays/MemoryPruneMinHits、TimeoutSecondsSandbox/Project（30/90）、MaxSteps、ExpectedOutput 等。
- **Program.cs**（Agent.Cli）：三角色 Model 均 = `law-unified` + Temperature（0.3/0.5/0.7）；classifier = qwen2.5:1.5b；dashscopeKey 仅预留。
- **Modelfile**（Ollama，源 %TEMP%\lawcheck\）：`FROM anarko/qwen3-coder-flash:30b` + num_gpu（当前 8，实验 4/8/12 后定）+ num_ctx 32768 + q8_0 KV + temperature 0.5 兜底 + stop 标记。环境：OLLAMA_KEEP_ALIVE=24h（注册表 User 已持久化）、OLLAMA_KV_CACHE_TYPE=q8_0。

**改模型配置注意**：Ollama 侧 `ollama create law-unified -f Modelfile` 重建（blob 复用秒级）；工作台配置在 AgentOptions 默认值 + Program.cs 两处，同步改。

---

## 9. 构建与测试（坑）

### 9.1 构建坑（字节级定性，反复踩）

**`dotnet build src\Agent.Cli --no-incremental` 只重建 CLI 自身，依赖项目 Agent.Infrastructure.dll 不会被复制更新** → 改动 Infrastructure 后 CLI 仍跑旧逻辑，误判"改动没生效"。

**必须**：`dotnet build LocalAiWorkbench.slnx --no-incremental`（全量）。

验证"改动是否生效"：python 字节级搜索 UTF-16LE 字面量（中文串 UTF-8 字节搜不到；UTF-16 解码搜索曾误报，字节搜索为准）。

### 9.2 测试（tests/Agent.Infrastructure.Tests/Program.cs）

- **零依赖断言 harness**（`Check(name, cond)`），单文件 2000+ 行，`dotnet run --project` 回归，**372/372**。
- 断言分组：OutputVerifier 判定 / 路由词表与意图 / 工具行为（P-D/分页/越界/备份/回滚）/ 记忆（去重/治理/过滤）/ 指标（归类/聚合/时代隔离）/ 符号索引（增量/刷新）/ **渲染层（Rendering：工厂选择 + FormatEvent 七事件映射固化 + Truncate/FormatToolArgs 边界 + 兼容抽样）**。
- **新增功能必须带 tests**（用户流程惯例：功能 → tests +N → E2E 复测 → 文档 → 提交）。

### 9.3 E2E 复测

- `scripts/e2e/runner.py <exe> <cmd_b64> <out_log>`：cmd_b64 = PowerShell `[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("…`n"))`；**绕开 PS5.1 -File 注入 UTF-8 BOM 的 bug**。
- `scripts/e2e/e2e.ps1`：4 用例（工程增量/单文件/多文件/读查）逐用例工程副本隔离 + 三态断言 + 汇总，落 `workspace\e2e\e2e_<ts>.log`。**PS5.1 读无 BOM 文件按 GBK 乱码——脚本必须 UTF-8 BOM**。
- 大模型任务 E2E 慢（large CPU 推理 5-15 分钟），用后台跑 + TaskOutput 轮询。

---

## 10. 已知边界与坑（开发必读）

1. **大文件定位→修改链路（30B 能力边界，已承认）**：符号定位（find_symbol/analyze_code 命中）→ offset 精确读取 → edit_file 修改，全链路模型不采纳引导（批 6 三叉验收第三种）。系统已铺三通道（拒绝消息/命中行号/操作指引），模型不采纳即诚实失败，**不加强制写**（用户明确否决）。
2. **30B Executor 增量修改纪律弱**：P-D 拦截消息附类型清单+动作指引后，模型仍可能整体重写 → P-D 恒定兜底，任务失败即回滚。ShippingService 类任务标注能力边界（E2E 3/4 基线）。
3. **LLM HttpClient 600s 超时**：large 多轮累积可能超时（批 5 记录，待方案调大）。
4. **情况 A 工具循环无强制写/目标聚焦检查**：模型持续工具调用绕过强制写（批 5 记录，用户确认"有依据才加"）。
5. **run_command 白名单无 grep**：模型想用 grep 被拒（已知，需时加）。
6. **PS5.1 编码**：读无 BOM 文件按 GBK 乱码；写文件默认 UTF-16LE/ANSI——一律显式 `-Encoding utf8`（BOM 场景）或 `[IO.File]::WriteAllText`（无 BOM 场景）。
7. **构建 DLL 复制坑**（见 §9.1）——改 Infrastructure 后必须全量 build slnx。
8. **交互程序挂死**：.py input / .cs Console.ReadLine 写入即被 PyCodeGuard 拒绝——不要放宽，这是防 30s 挂死防线。
9. **项目名幻觉**：工程模式命令中 `.csproj` 名不在工程根 → 拒绝（模型曾乱写 OrderSystem.csproj 导致失败）。
10. **模板误判历史**：hello 类输出曾触发模板检测死循环——`IsTaskExpectedOutput` 豁免已修（输入含输出行 → 豁免）；改 OutputVerifier 时保持该豁免语义。

---

## 11. 开发工作流（标准流程）

```
1. 探查：读 HANDOVER 相关批次 + 目标源码 + tests 现状（连续两次读取不再改变计划即开工）
2. 方案：给用户出方案（改什么/为什么/影响面/验证方式）→ 用户确认后才动手
3. 实施：只改点名范围；同功能多词表一次改齐（防漂移）
4. 验证：dotnet build LocalAiWorkbench.slnx --no-incremental → 0 错误
          dotnet run --project tests\... → 全过（新增断言）
          （模型行为改动）E2E 复测 → 如实汇报（诚实失败优于假完成）
5. 文档：HANDOVER 顶部加批记录（倒序）+ DEVELOPER 相关章节同步
6. 提交：git add -A && git commit（本机仓库，沿用现有配置；每完成一个批次一个 commit）
```

---

## 12. 模块文件索引（速查）

| 路径 | 一句话 |
| --- | --- |
| `src/Agent.Infrastructure/AgentLoop.cs` | ReAct 主循环 + 防线全家桶（升级/熔断/防假完成/目标注入/工程上下文/记忆） |
| `src/Agent.Infrastructure/Routing/ModelRouter.cs` | 三层漏斗路由主体 |
| `src/Agent.Infrastructure/Routing/RuleEngine.cs` + `RoutingRules.json` | 8 条 L1 规则 |
| `src/Agent.Infrastructure/Routing/IntentOverride.cs` | 写/读动词词表（唯一权威，AgentLoop 复用） |
| `src/Agent.Infrastructure/Routing/RoutingIntelligence.cs` | 数据闭环调权（降级/升级判定） |
| `src/Agent.Infrastructure/Routing/TaskFeatureClassifier.cs` / `TaskMetricsAggregator.cs` | D1/D2 特征归因 |
| `src/Agent.Infrastructure/Orchestration/Orchestrator.cs` | Planner→Executor→Validator + 严格校验 + 自动回滚 |
| `src/Agent.Infrastructure/Orchestration/PlannerService.cs` | Plan JSON 生成 + 工程图谱块 + 计划校验 |
| `src/Agent.Infrastructure/Orchestration/ExecutorService.cs` | 逐文件生成/修复 + 升级链 |
| `src/Agent.Infrastructure/Project/BackupService.cs` | taskId 隔离回滚全家 |
| `src/Agent.Infrastructure/Project/TypeConflictDetector.cs` | P-D 写前类型冲突 |
| `src/Agent.Infrastructure/Project/SymbolIndexer.cs` | 符号索引（增量） |
| `src/Agent.Infrastructure/Project/DependencyAnalyzer.cs` | 依赖边 |
| `src/Agent.Infrastructure/Project/TestRunnerService.cs` | 工程测试自动执行 |
| `src/Agent.Infrastructure/Quality/OutputVerifier.cs` | 输出判定单一来源（双路径共用） |
| `src/Agent.Infrastructure/Rendering/` | 渲染层（IRenderer/RendererBase/PlainText/Spectre/Factory，§5.1） |
| `src/Agent.Infrastructure/Metrics/SqliteMetricsCollector.cs` | 三表指标 + 失败归类 + 时代隔离 |
| `src/Agent.Infrastructure/Metrics/FailureCategorizer.cs` | 失败类别词表 |
| `src/Agent.Infrastructure/Memory/AgentMemoryStore.cs` | 记忆存储（去重/治理） |
| `src/Agent.Infrastructure/Memory/MemoryPromptBuilder.cs` | 失败教训注入格式 |
| `src/Agent.Cli/Program.cs` | CLI 入口：命令分发/BOM 清理/健康检查/归档 |
| `tests/Agent.Infrastructure.Tests/Program.cs` | 353 断言 harness |
