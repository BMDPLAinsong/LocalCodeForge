# LocalAiWorkbench 开发文档（详细版）

> **定位**：本文是 `docs/DEVELOPER.md`（速查手册）的**详细展开版**，面向后续维护与开发：
> 以「任务从输入到交付的完整链路」为主线，逐步说明每个环节的代码位置、判定规则、数据结构与改造方法。
> 速查用 `DEVELOPER.md`，深入理解与改造用本文；两者均基于当前基线（git main @ 6adde9c，
> tests 450/450、构建 0 错误、metrics 587 任务 / 500 成功 / 85.2%）。

---

## 0. 铁律（用户的硬约束，违反即返工）

1. **放弃提示词工程**。一切缺陷修复优先用**工程化 / 数据结构性**手段（确定性代码、词表、表结构、工具行为约束、拒绝消息），禁止靠堆 System Prompt / 修复器规则「打地鼠」。
2. **任何修改先出方案，用户确认后才动手**。方案写明：改什么、为什么、影响面、验证方式。
3. **只用本地模型**。云端（DashScope）仅预留未来兜底，当前不使用；模型形态固化为**单 tag `law-unified`（30B）三角色**，不再额外拉取模型。
4. **测试不落 C 盘 Temp**。统一落 `X:\.NET AI\LocalAiWorkbench\workspace\test-tmp\<PID>`（已 gitignore）。
5. **诚实失败优于假完成**。任务做不了就如实失败（走 H2/H4 防线），禁止「工具已验证」式假完成。
6. 批量改动后必须：构建 0 错误 → tests 全过 →（涉及模型行为时）E2E 复测 → 同步 HANDOVER/DEVELOPER → git 提交（本机仓库，沿用现有 Git 配置）。

---

## 1. 项目概览

| 项 | 值 |
| ---- | ---- |
| 性质 | 纯本地 AI 编码助手内核（控制台）：自然语言输入 → 智能路由 → 规划/生成/写文件/运行验证/自动修复闭环 |
| 解决方案 | `LocalAiWorkbench.slnx`（新版 XML solution） |
| 目标框架 | net10.0（勿降级） |
| 核心依赖 | Microsoft.SemanticKernel 1.80.1 + SK OpenAI Connector（走 Ollama `/v1` 兼容端点，全本地）+ Microsoft.Data.Sqlite |
| 模型 | Ollama：classifier=`qwen2.5:1.5b`；small/large/agent_pro 三角色全部 = `law-unified`（30B Qwen3-Coder-A3B，单 tag 单驻，请求级 temperature 区分：small 0.3 / agent_pro 0.5 / large 0.7） |
| 版本控制 | 本机 git 仓库（main，无远端） |
| 测试 | `tests/Agent.Infrastructure.Tests`（零依赖断言 harness，**450/450**，`dotnet run --project` 一键回归） |
| 数据落盘 | `workspace/`：sessions.db、runs/、project_context.db、agent_memory.db、symbol_index.db、test-tmp/、e2e/ |

### 1.1 三层项目结构

| 项目 | 职责 | 依赖 |
| ---- | ---- | ---- |
| `src/Agent.Core` | 抽象接口（IAgentLoop/IAgentTool/IToolRegistry/ISessionStore）+ 纯模型（AgentOptions/AgentEvent/Plan/TaskMetric/SystemPrompt 等），零依赖 | 无 |
| `src/Agent.Infrastructure` | **全部实现**：AgentLoop、Orchestration、Routing、Project、Tools、Metrics、Memory、Quality、Session、Rendering | Core + SK + Sqlite |
| `src/Agent.Cli` | 控制台入口：交互循环、命令分发、事件渲染、健康检查、run 目录清理、任务归档 | Core + Infrastructure |

### 1.2 源码规模（改哪里先看这里）

```
Agent.Cli/Program.cs                    1104 行   CLI 主循环 + 命令分发 + 归档 + 回滚
Agent.Infrastructure/AgentLoop.cs       1129 行   ReAct 主循环 + 防线全家桶
Agent.Infrastructure/Orchestration/Orchestrator.cs    1046 行   三阶段编排
Agent.Infrastructure/Orchestration/PlannerService.cs   678 行   Plan JSON 生成
Agent.Infrastructure/Orchestration/ExecutorService.cs  571 行   逐文件生成/修复
Agent.Infrastructure/Memory/AgentMemoryStore.cs        393 行   记忆存储
Agent.Infrastructure/Project/SymbolIndexer.cs          390 行   符号索引
Agent.Infrastructure/Metrics/SqliteMetricsCollector.cs 379 行   指标三表
Agent.Infrastructure/Tools/WriteFileTool.cs            368 行   写工具（P-D/白名单/备份）
Agent.Infrastructure/Tools/RunCommandTool.cs           344 行   命令工具（白名单/敏感拒/超时）
Agent.Infrastructure/Quality/OutputVerifier.cs         302 行   输出判定单一来源
其余各文件 <260 行
```

---

## 2. 端到端数据流（任务输入 → 交付）

```
用户输入
  │
  ▼
CLI 主循环（Program.cs 194 行起）：
  ① input.TrimStart('\uFEFF').Trim()          ← BOM 清理（根治 PS 管道 BOM 乱码）
  ② 命令分发：
     - 临时测试/plan/orch/stats/memory/use/rollback/exit/new → 各自分支处理
     - 输入路径自动识别（ProjectRoot 为空 && 输入含绝对目录路径 + 读语境）→ 自动只读 use
  ③ 自动分发（483 行起）：
     a. ReadOnlyGuard 预检（只读工程 + 写任务引用工程内文件 → 直接阻断，不跑任务）
     b. 生成 taskId（8 位）→ 打印 [工作目录]
     c. 显式写目标解析（TryGetWriteTarget + 敏感区源头拒 → 本次任务临时白名单）
     d. 每任务独立 DI 容器（工具绑定 run 根 + taskId 隔离备份）
     e. TaskClassifier.ShouldOrchestrate(input)？
        ├─ true  → Orchestrator.RunAsync（三阶段编排）
        └─ false → AgentLoop.RunAsync（ReAct 单循环）
     f. ArchiveTask（任务归档记忆，source=task_<id>）
     g. 工程可写模式：失败 → AutoRollbackOnFailure；成功 → ClearNewMarkers；末了 PruneBackups(8)
  ▼
模型层（SK + Ollama /v1）：
  - 路由（ModelRouter 三层漏斗）决定用哪个角色（small/agent_pro/large）
  - 角色差异 = 请求级 Temperature（0.3/0.5/0.7），同 tag law-unified
  ▼
工具层（10 工具，§7）：文件/命令/分析操作，全部带边界守卫
  ▼
SQLite 数据层（§9）：sessions/metrics/project_context/agent_memory/symbol_index
```

---

## 3. CLI 层详解（src/Agent.Cli/Program.cs）

### 3.1 启动序列

| 步骤 | 位置 | 说明 |
| ---- | ---- | ---- |
| 输入编码加固 | 33 行 | `Console.InputEncoding = UTF8`（try-catch 静默降级，根治 GBK 解码 BOM 乱码） |
| 配置区 | 40 行 | AgentOptions：三角色全 law-unified + Temperature 0.3/0.5/0.7；classifier qwen2.5:1.5b |
| 健康检查 | 60-95 行 | Ollama 未启动自动 `ollama serve` 拉起；逐模型 [OK]/[FAIL] |
| 会话库 | — | sessions.db 初始化 |
| CleanOldRuns | 106 行 | runs 保留最近 10（keepDays=7） |
| 交互循环 | 194 行起 | 读一行 → trim BOM/空白 → 分发 |

### 3.2 命令表

| 输入 | 分支位置 | 作用 |
| ---- | ---- | ---- |
| `use <路径>` / `use --write <路径>` | 382 行 | 工程模式；可写模式改动前自动备份（ApplyUse 共用） |
| `rollback [rel]` / `--dry-run` | 405 行 | 任务级回滚（无参=最近任务全量；交互态多选） |
| `stats` | 269 行 | 任务指标汇总（成功率/失败类别/特征归因/时代隔离/可疑成功明细/工具拒绝 + 交互态 BarChart） |
| `memory list/stats/forget/prune/dedup` | 316 行 | 模型记忆管理 |
| `plan <需求>` | 206 行 | 只跑 Planner 看规划 JSON |
| `orch <需求>` | 248 行 | 强制走 Orchestrator |
| `exit` / `new` | — | 退出 / 新会话 |
| `help` / `?` | — | 命令表速查（一行式，10-07 新增） |
| 其他文本 | 483 行 | 作为任务自动分发 |

### 3.3 自动分发（483-571 行，核心接线）

```
if (ReadOnlyGuard.ShouldBlock(...)) → 打印 [只读工程] 三行提示 + continue    ← 不建容器不跑任务
taskId = Guid.NewGuid().ToString("N")[..8]
TryGetWriteTarget(input) && !SensitivePathGuard.IsDenied → [显式授权] + explicitWriteRoots
taskServices.AddAgent(options, workspace, dbPath, projectRoot, writeEnabled, taskId, explicitWriteRoots)
if (TaskClassifier.ShouldOrchestrate(input)) → Orchestrator 分支
else → AgentLoop 分支（AgentRequest 携带 writeTarget/writeTargetIsFile）
两者收尾统一：ArchiveTask → 失败 AutoRollback / 成功 ClearNewMarkers → PruneBackups(8)
```

**改这里注意**：任务级 DI 容器是每次任务新建的（P-B 隔离备份的关键）；任何新「任务前预检」应插在 483 行 ReadOnlyGuard 之后、DI 容器创建之前（参考 ReadOnlyGuard 的接法）。

---

## 4. AgentLoop 详解（ReAct 单循环，src/Agent.Infrastructure/AgentLoop.cs）

### 4.1 状态字段（类顶部，理解防线的前提）

| 字段 | 用途 |
| ---- | ---- |
| `_cycleSignatures` | 循环熔断：本轮已成功执行的工具签名（工具名+首参+输出指纹） |
| `_lastRunSucceeded` / `_lastRunOutput` | 最近 run_command 结果（H2 判定输入） |
| `_hasWrittenFile` / `_everWroteFile` | 本轮是否写过文件（强制写规则输入） |
| `_writeRejected` | 写工具被拒记录（完成门 C 判定：被拒未重试 → 失败） |
| `_writeTarget` / `_writeTargetIsFile` | 显式写目标（完成门 B 判定：目标缺失 → 失败+可疑） |
| `_lastStepFailed` / `_lastErrorSig` | 升级链 / 修复无进展熔断 |

### 4.2 RunAsync 逐步流程

```
启动：
 ① 显式写目标提取（AgentRequest 携带，CLI 已授权）
 ② 记忆治理自动清理（30 天未用且 0 命中 → 删除）
 ③ 工程上下文注入（缓存签名一致 → 复用 CompactBlock；否则重扫写入）
 ④ 历史记忆检索注入（按输入特征类过滤：task 归档记忆只注入同类，model 决策降权保留）
 ⑤ 系统提示组装（角色 prompt + 工具说明 + 约束块）

ReAct 主循环（step < MaxSteps=20）：
 ├─ 模型调用（LLM HttpClient，600s 超时；空响应 → B2 升级链）
 ├─ 情况 A：结构化函数调用（SK FunctionCall）
 │    执行工具 → 写被拒记录 → 熔断检查（BuildCycleSignature 重复 → 收尾）
 │    熔断收尾顺序：完成门优先（fuseGate）→ H2（写过但验证未过→失败）→ H4（写任务未写→失败）
 │    → 否则以工具验证结果收尾
 ├─ 情况 B：文本兜底（模型输出非工具调用的自然语言）
 │    空响应检测 → 升级链（small→agent_pro→large，每层至多一次，直达跳层）
 ├─ 情况 C：真正的最终答案（FinalAnswer）
 │    模板检测（疑似模板输出 → 重试）
 │    业务合理性检查（顺序判定，任一命中即继续循环而不是收尾）：
 │      规则1 代码请求但没写文件 → 强制写
 │      规则2 必须运行但只 build 未 run → 补 run
 │      规则3 代码请求已写文件但未 run → 补 run
 │      规则4 最近 run 失败 → 补修复
 │    工程完成门：改了代码 → 自动运行测试（每任务至多一次）
 │    完成门（批次1）：写被拒未重试 → 失败；目标缺失 → 失败+可疑；目标达成 → 正常收尾

循环结束（达到 MaxSteps）：
 最大步数也过完成门（目标达成 → 放行收尾；否则失败）
```

### 4.3 升级链（B2 空响应）

- 同模型已空一次，再同模型重试收益有限 → **强制升级服务**：small → agent_pro → large。
- 升级是主动决策 → 重置 `_lastStepFailed`（防 recovery 规则把升级白升回低层）。
- 每层至多尝试一次；small/agent_pro 连续空响应后直接终止（给大模型一次机会）。

### 4.4 强制写规则（业务合理性检查）

四个规则全部是**确定性判定**（非提示词），命中即注入下一步指令让模型继续循环：

| 规则 | 条件 | 动作 |
| ---- | ---- | ---- |
| 1 | 代码请求 && !hasWrittenFile && step<MaxSteps-1 | 强制写：注入「必须写文件」指令 |
| 2 | mustRun && onlyBuiltNotRun && step<MaxSteps-1 | 补 run |
| 3 | 代码请求 && hasWrittenFile && !hasRunCommand | 补 run 验证 |
| 4 | 代码请求 && lastRunFailed | 补修复 |

### 4.5 完成门（批次1，三处接线：正常收尾/熔断区/最大步数）

```
CompletionGate.Evaluate(isWriteTask, wroteAny, writeRejected, target, targetIsFile)
→ CompletionVerdict?
   - 写被拒且无写成功             → 判失败（非可疑）
   - 提取到目标但目标不存在        → 判失败 + isSuspicious=true（"写了别处"= 假完成嫌疑）
   - 目标存在且已写入             → 放行收尾
```

---

## 5. Orchestrator 详解（三阶段编排，src/Agent.Infrastructure/Orchestration/）

### 5.1 阶段总览

```
阶段1 规划（PlannerService.GeneratePlanAsync）
  → 工程图谱块注入（BuildEngineeringGraphBlock：已有文件清单+类型分布+影响面）
  → 计划校验（CheckIncrementalPlanConflicts：工程增量任务 Plan 中"已有文件≥2"→ 拒绝走升级链）
  → 失败升级链：Planner 空输出/解析失败 → 追加提示重试 → Large 兜底（至多 3 次）
  → 子指标记录（routingTier=OrchestratorPlanner，样本自动积累）

阶段2 逐文件生成（ExecutorService.GenerateAsync，routingTier=OrchestratorExecutor）
  → 每个文件按 Plan.spec 生成
  → 后写文件可看到前面文件的真实接口（fileContents 累积注入）

阶段3 验证 + 修复
  3.1 Lint 检查（PythonLintService：pyflakes 风格）→ 按文件分组逐个修复
  3.2 Lint 通过 → run_command（防 Planner 幻觉 csproj：工程模式用工程根实际 .csproj，
      命令含 --project 幻觉名 → 正则替换为实际名）
      → OutputVerifier 判定（凭据/结构/stderr/通用失败/占位/模板）
      → 严格校验（StrictOutputVerification || 工程模式默认严格）：expected 每非空行须精确出现
  3.3 失败 → 修复循环（ExecutorService.RepairAsync）：
      · 错误签名熔断（ExtractErrorSignature：CS\d+ / [A-Za-z]+Error / 文件(行号 / line N）
        新签名 == 上次签名 && retries>0 → 提前终止（防同错误反复修复）
      · 类型缺失定义注入（ExtractMissingType 命中 → 找 class X 定义文件 → 完整定义内容注入
        修复上下文；只在修复阶段、每任务一次、只注入第一个命中的类型）
      · 修复输出与原文相同 → 升级链（small→agent_pro→large）
      · 重试上限由特征类历史校准（工程增量 <50% 成功率 → 3→1）
  3.4 全部失败 → 失败教训归档 + （工程可写模式）CLI 侧 AutoRollbackOnFailure
```

### 5.2 Plan JSON schema（PlannerService 要求模型输出的格式）

```json
{
  "summary": "一句话总结",
  "language": "python | csharp | javascript",
  "files": [
    {
      "path": "相对路径",
      "purpose": "文件职责（一句话）",
      "requirements": "分号分隔的具体要求"
    }
  ],
  "run_command": "验证命令",
  "expected_output": "期望输出摘要"
}
```

- Plan 里必须使用与用户需求完全相同的文件名（不得改名）。
- 解析：TryParsePlan 提取 JSON 块 → JsonSerializer.Deserialize<Plan>（宽容配置）。
- 后处理：SimplifyRunCommand 等（防幻觉文件名）。

### 5.3 Orchestrator 关键检查点

| 位置 | 机制 |
| ---- | ---- |
| 112 行 | CheckIncrementalPlanConflicts 早拦截（计划阶段防整体重写，避免 Executor 白跑） |
| 425 行 | 防 Planner 幻觉 csproj 名（工程模式 run_command 用实际文件） |
| 833 行 | 严格校验：expected 每一非空行须在 stdout 精确出现 |
| 529 行 | 类型缺失定义注入（修复阶段） |
| 1013 行 | BuildEngineeringGraphBlock（工程图谱块：多文件修改时 Planner 看到工程全貌） |
| 1122 行 | RecordSubMetricAsync（子调用指标：Planner=agent_pro / Executor=small） |

---

## 6. 路由体系详解（src/Agent.Infrastructure/Routing/）

### 6.1 三层漏斗（ModelRouter.RouteAsync 顺序判定）

| 层级 | 判定 | 动作 |
| ---- | ---- | ---- |
| **L0** | 查询词表：看看/查看/看一下/列出/显示/有哪些文件/什么文件 | 直连 small（跳过规则与分类器） |
| **L0.5** | `IntentOverride.IsWriteTask`（写动词 + 文件/代码语境） | 写任务直连 large；先查数据闭环可否降级 small |
| **L1** | RuleEngine 8 条规则（RoutingRules.json，按 priority 命中） | 命中 → 目标角色 |
| **L0.5b** | L1 命中 small 且 `IsReadTask` | 查数据闭环可否升级 agent_pro |
| **L2** | IntentClassifier 意图分类 + `IntentOverride.Apply` 确定性覆盖 | SimpleQA+写动词+文件语境 → 强制 CodeGenerate |
| **L3** | 意图 → 服务映射（见下表） | 分发最终角色 |

### 6.2 L3 意图映射表

| 意图 | 目标 |
| ---- | ---- |
| SimpleQA / CodeGenerate / FileProcess | small（本地省钱） |
| Search / Unknown | large |
| ComplexReason | ComplexToPro=true → agent_pro；false → large |

### 6.3 RoutingRules.json 全文（8 条规则，按 priority 从高到低）

| 规则 | type | 触发 | target | priority |
| ---- | ---- | ---- | ---- | ---- |
| recovery | after_failure | 上一步失败 | agent_pro | 200 |
| complex_local | keyword_any | 爬虫/应用/项目/系统/网站/服务/框架/微服务/架构/分布式 | agent_pro | 150 |
| force_code_simple | regex | 写一个/写个/生成一个/创建一个/实现一个/用.*写/… | small | 100 |
| force_file_ops | regex | ^(列出/查看/显示/打开/读取/删除/重命名/复制/移动/创建/新建/写入) | small | 98 |
| force_code_edit | regex | (修改/改一下/重构/优化/修复/fix/refactor/添加/新增/…) *(代码/函数/方法/类/文件/…) | small | 95 |
| search_keywords | keyword_any | 今天/最新/天气/新闻/股价/实时 | large | 82 |
| long_input | length_min | 输入 ≥200 字符 | large | 60 |
| short_input | length_max | 输入 ≤8 字符 | agent_pro | 55 |

### 6.4 数据闭环调权（RoutingIntelligence，纯静态可测）

**TrySuggestDowngrade（L0.5 写任务 large → small）**：保守三条件全满足才降级——
1. small 样本 ≥3；
2. small 成功率 ≥0.6；
3. small 平均耗时 < large（或 large 无历史）。
不满足一律保持 large（防打地鼠）。

**TrySuggestReadTier（L0.5b 读任务 small → agent_pro）**：四条件全满足才升级——
1. small 样本 ≥3 且成功率 <0.6（读任务表现差）；
2. agent_pro 样本 ≥3 且成功率 ≥0.5；
3. agent_pro 平均耗时 < small。

**数据源**：task_metrics（service_id/routing_tier/success/total_ms）+ routing_decisions（每次决策落库）。

### 6.5 词表漂移（重点维护项，历史多次踩坑）

同一功能逻辑在**多处有硬编码词表**，改词表必须**一次改齐**并补 tests：

| 词表 | 位置 | 内容 |
| ---- | ---- | ---- |
| `OperationVerbs` | IntentOverride.cs | 写/更改/改/改为/改变/变更/换成/重写/生成/追加/覆盖/清空/格式化/打印/输出/加/注释掉/添加/新增/修改/写入/创建/删除/编辑/改一下/加上/加入/增加/重构/修复/插入/替换/移动/复制/改名/更新 |
| `ReadVerbs` | IntentOverride.cs | 读取/读一下/查看/看看/看一下/列出/显示/检查/分析/理解/找到/查找/定位/内容/结构 |
| `CodeContext` | IntentOverride.cs | .cs/.py/.js/.ts/.json/.csproj/.sln/.slnx/.md/.txt/.xml/.yaml/.yml/代码/文件/注释/属性/函数/方法/类/脚本/模块/配置/接口/实现 |
| `FileReferencePattern` | TaskClassifier.cs | `([\w\-\.]+\.[a-z]{1,5})(?=\s|$|/|\\)`（捕获组+lookahead，ReadOnlyGuard 共用） |
| 特征正则 | TaskFeatureClassifier.cs | 工程增量/外部凭据/多文件/读查/单文件（§11.4 全文） |
| L1 规则 | RoutingRules.json | 见 §6.3 |

**AgentLoop.looksLikeCodeRequest 已对齐复用 `IntentOverride.IsWriteTask`**（不再独立维护）。

---

## 7. 工具层详解（src/Agent.Infrastructure/Tools/，10 工具）

### 7.1 工具总表（schema 参数 + 关键行为）

| 工具 | schema 参数 | 关键行为与边界 |
| ---- | ---- | ---- |
| `list_dir` | path | 列目录；读策略分档（沙箱限工作区根/工程模式放开）；敏感区源头拒；500 条目上限（超出提示缩小范围） |
| `read_file` | path, offset(可选), limit(默认500上限1000) | **行级分页**：大文件（>200KB）默认返回前 500 行 + 分页头（共 N 行/已返回 X-Y/续读 offset 提示）；小文件默认全文无头（向后兼容）。**大文件首读重定向**：工程模式 + >100KB + 未带 offset → 拒绝 + 引导 find_symbol/analyze_code（仅 limit 不算分页意图，防绕过）。**敏感区源头拒**（内容物理不进上下文）：返回「路径涉及敏感区」。模式分档：工程模式读放开 / 沙箱限工作区根 |
| `write_file` | path, content | **写白名单**（WriteRoots=workspaceRoot ∪ projectRoot(工程可写)）；**P-D 类型冲突拦截**（新内容含工程已有类型名 → 拒绝 + 类型清单 + 动作指引）；P-D 熔断（同路径被拒≥2 次 → 强制 edit_file 指令）；工程可写自动备份 + MarkNewFile；.py 强制 HTTP timeout=8；项目名幻觉校验；tool_rejections 落库；explicitWriteRoots 临时授权并集 |
| `edit_file` | path, old_string, new_string | 精确替换；old_string 未命中 → 查备份漂移诊断；P-D 拦截；备份；写白名单同 write_file |
| `create_dir` | path | 建目录；写白名单同 write_file |
| `run_command` | command | **白名单**：python/pip/dotnet/node/npm/git/dir/ls/type/cat/find/findstr/where/grep/Select-String（首 token 匹配，含正则检索；10-07 补 grep）；**敏感路径源头拒**（命令串先展开环境变量 → 正则提取路径 token → IsDenied）；工程模式路径边界（..\ / 盘符绝对路径越界拒绝）；cwd=工程根（工程模式）；**超时双轨**（沙箱 30s / 工程 90s，工程模式自动放宽）；交互拦截（PyCodeGuard）；项目名幻觉校验（.csproj 名必须存在于工程根） |
| `analyze_code` | path | 代码符号地图（class/方法/属性+行号，行级正则不进上下文）；支持大文件（200KB→5MB）；非代码文件降级提示 |
| `find_symbol` | symbol | 符号定位（symbol_index.db）；**Find 前增量刷新**（mtime 对比只重扫变更文件）；命中返回 [文件:行号] + 操作指引（read_file offset 建议） |
| `analyze_deps` | path | 文件依赖/反向依赖（改动影响面）；复用 symbol 索引 |
| `remember` | topic, content | 模型主动存发现/决策到 agent_memory（kind=model）；同 kind+topic 合并去重 |

### 7.2 守卫

- **PyCodeGuard**：.py `input(` / .cs `Console.ReadLine` 写入即拒绝（防 30s 挂死）。
- **AgentToolBase**：工具基类（描述/schema/名称约定）。
- **写白名单统一实现**：WriteFileTool/EditFileTool/CreateDirTool 共用 `PathBoundary.IsInside(path, WriteRoots)` 判定。

---

## 8. 工程感知与安全机制（src/Agent.Infrastructure/Project/）

### 8.1 机制清单（实现细节）

| 机制 | 文件 | 实现细节 |
| ---- | ---- | ---- |
| 只读扫描 | ProjectScanner.cs | 文件树/入口/依赖识别；产出工程上下文数据块（CompactBlock 注入瘦身版） |
| 工程缓存 | ProjectContextStore.cs | project_context.db + 4 元签名（FileCount/DirCount/TotalBytes/MaxMtimeTicks）——签名一致命中缓存复用，变更自动重扫 |
| 可写回滚 | BackupService.cs | **taskId 隔离**：备份 `.backup/<taskId>/<stamp>/<rel>` + 新建标记 `.new/<rel>`；RollbackTask 取**最早**备份；PruneBackups keepLastN=8；DryRunRollbackTask |
| 符号索引 | SymbolIndexer.cs | 正则提取类型/方法/属性；SQLite 持久化；增量刷新（mtime 对比） |
| 依赖分析 | DependencyAnalyzer.cs | 类型级符号 + 引用扫描 → 文件依赖边 |
| 类型冲突 P-D | TypeConflictDetector.cs | 写前检测新内容含工程已有类型 → 拒绝（class/interface/enum/struct/record，剥离注释/字符串防误报）；目标文件自身已有类型=partial 语义放行 |
| 测试门 | TestProjectDetector.cs + TestRunnerService.cs | 检测测试入口（*.Tests.csproj/pytest/package.json test）；工程模式改代码后自动跑测试 |
| 计划校验 | Orchestrator.CheckIncrementalPlanConflicts | 工程增量任务 Plan「已有文件≥2」→ Planner 阶段拒绝（防整体重写）；合法形态（新增1+接入1）放行 |
| **路径边界** | PathBoundary.cs | **逐路径分段比较**（OrdinalIgnoreCase + 分隔符归一）：root 自身/子目录=true；`X:\proj2` 对 `X:\proj`=false（StartsWith 前缀假阳性根治）；`..` 跳脱经 GetFullPath 归一必越界 |
| **敏感源头拒** | SensitivePathGuard.cs | **读=黑名单排除**：系统区 4（C:\Windows/Program Files/(x86)/ProgramData）+ 凭据区 9（.ssh/.aws/.kube/.docker/.config\gcloud/.git-credentials/.npmrc/两处 Credentials）+ 用户 denylist（workspace\sensitive_denylist.txt，# 注释/空行跳过/幂等）。四条判定线：①长名逐段匹配 ②8.3 短名双向展开（GetLongPathNameW/GetShortPathNameW P/Invoke）③符号链接/junction 逐段 ResolveLinkTarget ④UNC `\\?\` 前缀剥除。模式分档：工程模式读放开+黑名单硬拒；沙箱模式读仍限 workspaceRoot。FindWriteRootOverlaps 启动自检。已知边界：脚本内敏感读不做 |
| **输入路径自动识别** | ProjectPathDetector.cs | TryExtractProjectPath（分片正则不含 ':'——盘符冒号是路径组成部分；Directory.Exists 校验；尾部 CJK 语境词剥离 ≤3 次）；HasReadContext/HasWriteContext；TryGetAutoUsePath（非写 + 读语境 + 非敏感） |
| **写目标提取** | ProjectPathDetector.cs | TryGetWriteTarget：通道1 完整绝对路径（盘符起点分片 + Exists + LooksLikeFile）；通道2 自然语言盘符（"X盘根目录"+文件名 → 文件级；无文件名 → 目录级）；文件名正则用 **ASCII 类**（.NET \w 默认含 Unicode 中文会吞掉"新建一下test.txt"） |
| **完成门** | CompletionGate.cs | Evaluate(isWriteTask, wroteAny, writeRejected, target, targetIsFile) → CompletionVerdict?（§4.5） |
| **只读工程预检** | ReadOnlyGuard.cs | ShouldBlock(input, projectRoot, projectWriteEnabled, out hitFile)：4 条件全满足才拦（工程模式 && !可写 && IsWriteTask && FindExistingProjectFileRef 命中）；豁免=显式写目标在工程外；FindExistingProjectFileRef 用 TaskClassifier.FileReferencePattern（公共正则）+ 工程根文件名 HashSet（OrdinalIgnoreCase） |
| **显式写授权** | AgentRequest.cs + 写三工具 + Program.cs | CLI 解析写目标 → 非敏感注入 ExplicitWriteTarget + explicitWriteRoots；敏感区兜底（白名单通过也拒） |

### 8.2 防假完成防线（AgentLoop + Orchestrator）

| 防线 | 说明 |
| ---- | ---- |
| H1 | 回滚跨任务误删修复（.new 标记 taskId 隔离 + 成功清理） |
| H2 | 熔断收尾加完成门槛——写任务未写过文件 → 判失败 |
| H3 | 工程模式默认严格校验（StrictOutputVerification \|\| ProjectRoot 非空） |
| H4 | 熔断收尾——写任务「写过文件但最近 run_command 未成功」→ 判失败 |
| 完成门 | 收尾区 + 熔断区 + 最大步数三处接线（§4.5） |
| 循环熔断 | BuildCycleSignature（工具名+首参+输出指纹）同签名二次成功 = 重复无进展 → 收尾 |
| 修复无进展熔断 | ExtractErrorSignature + 循环内新签名==上次 && retries>0 → 提前终止 |
| 类型缺失注入 | ExtractMissingType 命中 → 完整定义内容注入修复上下文（非位置指引；30B 边界已承认） |
| 防 Planner 幻觉 csproj | 工程模式 run_command 用工程根实际 .csproj；--project 幻觉名替换 |

---

## 9. 数据层（workspace/，SQLite）

### 9.1 库与表完整 schema

| 库 | 表 | 完整字段 |
| ---- | ---- | ---- |
| sessions.db | task_metrics | task_id(PK)/session_id/user_input/mode/routing_tier/service_id/routing_reason/success/is_suspicious/retry_count/total_ms/planner_ms/executor_ms/validator_ms/lint_blocking_errors/lint_warning_errors/error_summary/failure_category/started_at/finished_at + 索引 (started_at DESC, success, failure_category)。旧库迁移：is_suspicious/failure_category 缺列 ALTER 补（**索引必须在 ALTER 之后建**，否则旧库 CREATE INDEX 引用不存在列报错） |
| sessions.db | routing_decisions | id(AI PK)/session_id/user_input/feature/tier/service_id/reason/intent/input_len/created_at + 索引 (created_at DESC, service_id, feature)。**生命周期**：启动时 PruneOlderThan(90) 删除 90 天前明细（10-07 新增；调权仅用近 10 条，历史无统计价值） |
| sessions.db | tool_rejections | tool/category/path/task_id/message(截断200)/created_at + 索引 (created_at, category)；类别 10 种：empty_content/interactive/out_of_bounds/project_file/pd_conflict/read_only/bad_args/not_found/drift/old_not_found/not_unique |
| sessions.db | sessions/turns | 会话与对话历史 |
| project_context.db | project_context | project_root(PK)/scan_version/overview/context_block/compact_block/file_count/dir_count/total_bytes/max_mtime_ticks/scanned_at（旧库补 compact_block 列） |
| agent_memory.db | agent_memory | id(AI PK)/kind/topic/content/source_task/created_at/hit_count/last_hit_at/count/feature + 索引 (topic)；同 kind+topic 合并去重（content 取最新、count+1）；旧库补 last_hit_at/count/feature 列 |
| symbol_index.db | symbol_index | project_root/symbol/file/line/kind/file_mtime，复合主键 (project_root, symbol, file, line) + 索引 (project_root, symbol)；file_mtime 增量基准 |

### 9.2 生命周期策略

| 对象 | 策略 | 位置 |
| ---- | ---- | ---- |
| runs/ | 保留最近 10（启动 CleanOldRuns，keepDays=7） | Program.cs 106 行 |
| .backup/ | 保留最近 8 个任务备份（PruneBackups） | Program.cs 570 行 |
| test-tmp/ | >7 天清理（按创建时间） | tests Program.cs 启动段 |
| 记忆 | 自动清理 30 天未用且 0 命中（MemoryPruneDays/MinHits） | AgentLoop 152 行 |

---

## 10. 模型与配置

### 10.1 AgentOptions 全字段（Agent.Core/Models/AgentOptions.cs）

| 字段 | 默认 | 说明 |
| ---- | ---- | ---- |
| Classifier | qwen2.5:1.5b | 意图分类器（只分类不生成） |
| Small / Large / AgentPro | law-unified + Temp 0.3/0.7/0.5 | 单模型三角色 |
| EnableRouting | true | 三层漏斗开关（false=全走 Large） |
| ComplexToPro | true | ComplexReason 走本地 agent_pro |
| StrictOutputVerification | false | expected_output 严格校验（工程模式默认严格，H3） |
| MaxSteps | 20 | 单任务最大推理步数 |
| MaxHistoryTurns | 10 | 历史对话携带轮数 |
| Temperature | 0.2 | 全局兜底温度 |
| ExecutorMaxRetries | 3 | 单文件修复最大重试（D2 可按特征类校准 3→1） |
| MemoryPruneDays / MinHits | 30 / 1 | 记忆自动治理 |
| LawUnifiedEpoch | — | 时代隔离点（stats 按此拆分 agent_pro 成功率） |
| ProjectRoot | null | 工程根（null=沙箱模式） |
| ProjectWriteEnabled | false | 工程可写授权 |

### 10.2 单 tag 三角色（关键机制）

- small/large/agent_pro 三个 ModelEndpoint **全部指向同一 Ollama tag `law-unified`**（30B Qwen3-Coder-A3B）。
- 角色差异 = **请求级 Temperature**（SK OpenAIPromptExecutionSettings.Temperature 注入）：
  small 0.3（确定性）/ agent_pro 0.5（居中）/ large 0.7（探索）。
- 好处：不占额外磁盘/内存（一个模型常驻）；坏处：三个角色行为差异只有温度，能力同源。

### 10.3 law-unified Modelfile（当前生效：num_gpu 14）

```dockerfile
FROM anarko/qwen3-coder-flash:30b
PARAMETER num_gpu 14        # 14/48 层 GPU（71% CPU / 29% GPU，显存 ~5653MiB）；15 层 OOM 已回退
PARAMETER num_ctx 32768     # 32K 上下文（规划 JSON + 工具说明 + 多轮历史 + 长代码）；16K 会截断输入
PARAMETER temperature 0.7   # 兜底（角色温度由请求级注入覆盖）
PARAMETER top_k 20
PARAMETER top_p 0.8
PARAMETER repeat_penalty 1.05
PARAMETER stop "<|im_start|>"
PARAMETER stop "<|im_end|>"
```

环境变量（用户级，重启 ollama serve 生效）：`OLLAMA_KEEP_ALIVE=24h`、`OLLAMA_KV_CACHE_TYPE=q8_0`、`OLLAMA_FLASH_ATTENTION=1`。

**重建 tag**：`ollama create law-unified -f Modelfile`（blob 复用秒级）；改 num_gpu 必须重建（SK /v1 无法请求级注入 num_gpu，仅 Temperature 标准字段可注入）。

---

## 11. 验证体系

### 11.1 OutputVerifier 判定信号集（Quality/OutputVerifier.cs，双路径共用）

| 检查 | 信号 | 说明 |
| ---- | ---- | ---- |
| 0.5 凭据失败 | \b401\b/\b403\b/unauthorized/invalid api key/appid/无效的密钥/认证失败… | 硬失败，无容错 |
| 2 stderr 剥离 | RequestsDependencyWarning 整块 + warning/警告 行过滤 + NETSDK 词替换 | **先整行过滤再做词替换**（顺序缺陷历史踩过） |
| 3 结构性错误 | Traceback/NameError/ImportError/…/error CS/error MSB/error NETSDK/Unhandled exception/找不到/不存在… | stdout 含任一 → 失败 |
| 4 通用失败 | 双容错（见源码 121-228 行） | — |
| 占位/模板 | IsPlaceholderOutput / IsTemplateOutput | 防「AI 生成占位文本」假交付 |

### 11.2 FailureCategorizer 词表（Metrics/FailureCategorizer.cs，按优先级命中）

```
external_credential → compile_error → runtime_error → timeout → validator_mismatch
→ repair_unimproved → write_unverified → write_not_attempted → target_mismatch
→ validator_failed → tool_rejected → not_found → empty_response → max_steps → template_flag → other
```

（完整信号词见源码 15-89 行；改词表时同步 OutputVerifier 信号集，两者对齐。）

### 11.3 测试 harness（tests/Agent.Infrastructure.Tests/Program.cs，450 断言）

- **零依赖**：`Check(name, cond)` 累加 pass/fail，末尾 `结果: N 通过 / M 失败` + 退出码。
- **当前基线 459 断言**（10-07：+9 配置结构自检——RoutingRules.json 可解析/target 合法/无重名/priority>0/type 合法/regex 可编译/keywords 非空；防"重名死规则"回归，改规则表时坏配置在 tests 阶段即暴露）。
- **反射调用**：`Assembly.Load("Agent.Infrastructure")` + MethodInfo 句柄（OutputVerifier 等静态方法）。
- **直接引用**：Agent.Infrastructure.Project.PathBoundary 等类型直接调用（测试工程引用 Infrastructure）。
- **测试临时根**：`workspace\test-tmp\<PID>`（启动时清理 >7 天旧目录，X 盘约定）。
- **断言分组**：OutputVerifier 判定 / 路由词表与意图 / 数据闭环调权 / 工具行为（P-D/分页/越界/备份/回滚/敏感区）/ 记忆（去重/治理/过滤）/ 指标（归类/聚合/时代隔离/回填）/ 符号索引（增量/刷新）/ 渲染层（FormatEvent 七事件映射）/ ReadOnlyGuard / 完成门 / 显式授权。

**新增功能必须带 tests**（用户流程惯例：功能 → tests +N → E2E 复测 → 文档 → 提交）。

### 11.4 TaskFeatureClassifier 特征正则（路由数据校准地基）

| 特征 | 正则要点 |
| ---- | ---- |
| 工程增量 | 在工程中/工程里新增/新增.*(类\|服务\|模块)/给.*(类\|文件).*新增/向.*添加.*(类\|方法) |
| 外部凭据 | 天气/weather/ip地址/汇率/实时/股价/新闻/api/api_key/key |
| 多文件 | 多文件/分.*(模块\|文件)/系统(，\|,)/项目.*(模型\|服务\|入口) |
| 读/查 | ^读取/^查看/^检查/^列出/^显示/^分析/^解释/^介绍/^总结/是什么/有哪些/怎么用/如何 |
| 单文件 | 写一个/写个/创建一个/生成一个/实现一个/用 (c#\|python\|…) |

匹配顺序：工程增量 → 外部凭据 → 读/查 → 多文件 → 单文件 → 其他。

---

## 12. 已知边界与坑（开发必读）

1. **大文件定位→修改链路（30B 能力边界，已承认）**：符号定位（find_symbol/analyze_code 命中）→ offset 精确读取 → edit_file 修改，全链路模型不采纳引导（批 6 三叉验收第三种）。系统已铺三通道（拒绝消息/命中行号/操作指引），模型不采纳即诚实失败，**不加强制写**（用户明确否决）。
2. **30B Executor 增量修改纪律弱**：P-D 拦截消息附类型清单 + 动作指引后，模型仍可能整体重写 → P-D 恒定兜底，任务失败即回滚。ShippingService 类任务标注能力边界（E2E 3/4 基线）。
3. **LLM HttpClient 600s 超时**：large 多轮累积可能超时（待方案调大）。
4. **情况 A 工具循环无强制写/目标聚焦检查**：模型持续工具调用绕过强制写（用户确认「有依据才加」）。
5. **run_command 白名单已含 grep/Select-String（10-07 解决）**：模型可用正则检索工程内容，不再被迫全量 read_file。剩余白名单外命令仍拒（需要时再加 token + 测试）。
6. **PS5.1 编码**：读无 BOM 文件按 GBK 乱码；写文件默认 UTF-16LE/ANSI——一律显式 `-Encoding utf8`（BOM 场景）或 `[IO.File]::WriteAllText`（无 BOM 场景）。
7. **构建 DLL 复制坑**：`dotnet build src\Agent.Cli` 只重建 CLI 自身，Infrastructure.dll 不会被复制更新 → **必须 `dotnet build LocalAiWorkbench.slnx --no-incremental`** 全量重建。
8. **交互程序挂死**：.py input /.cs Console.ReadLine 写入即被 PyCodeGuard 拒绝——不要放宽（防 30s 挂死防线）。
9. **项目名幻觉**：工程模式命令中 `.csproj` 名不在工程根 → 拒绝（模型曾乱写 OrderSystem.csproj 导致失败）。
10. **模板误判历史**：hello 类输出曾触发模板检测死循环——`IsTaskExpectedOutput` 豁免已修（输入含输出行 → 豁免）；改 OutputVerifier 时保持该豁免语义。
11. **CLI 冒烟管道编码**：PS 管道喂 CLI（runner.py b64 / `$input | & cli`）会因编码混流卡死（use 行乱码被当任务跑模型）——**cmd 文件重定向 `<` 为字节流无 BOM 最稳**。
12. **OOM 僵尸进程**：num_gpu 15 层 OOM 后 llama-server 僵尸残留占 CUDA 上下文 → 后续所有加载请求全 OOM——`Stop-Process` 杀残留 llama-server 即恢复。

---

## 13. 开发工作流与调试指南

### 13.1 标准流程

```
1. 探查：读 HANDOVER 相关批次 + 目标源码 + tests 现状（连续两次读取不再改变计划即开工）
2. 方案：给用户出方案（改什么/为什么/影响面/验证方式）→ 用户确认后才动手
3. 实施：只改点名范围；同功能多词表一次改齐（防漂移）
4. 验证：dotnet build LocalAiWorkbench.slnx --no-incremental → 0 错误
          dotnet run --project tests\... → 全过（新增断言）
          （模型行为改动）E2E 复测 → 如实汇报（诚实失败优于假完成）
5. 文档：HANDOVER 顶部加批记录（倒序）+ DEVELOPER/本文同步
6. 提交：git add -A && git commit（本机仓库；每完成一个批次一个 commit）
```

### 13.2 调试速查

| 需求 | 方法 |
| ---- | ---- |
| 看路由决策 | `sqlite3 workspace/sessions.db "SELECT * FROM routing_decisions ORDER BY created_at DESC LIMIT 20"` |
| 看任务失败归因 | `sqlite3 workspace/sessions.db "SELECT user_input, failure_category, error_summary FROM task_metrics WHERE success=0 ORDER BY started_at DESC LIMIT 20"` |
| 看工具拒绝 | `sqlite3 workspace/sessions.db "SELECT * FROM tool_rejections ORDER BY created_at DESC LIMIT 20"` |
| 看模型记忆 | `memory list`（CLI）或 agent_memory.db 查询 |
| 验证改动生效 | python 字节级搜索 UTF-16LE 字面量（中文串 UTF-8 字节搜不到，字节搜索为准） |
| 看最近 run 日志 | `workspace\runs\<ts>\` 下任务产物 + CLI stdout |
| E2E | `scripts/e2e/e2e.ps1`（4 用例，落 workspace\e2e\e2e_<ts>.log；脚本必须 UTF-8 BOM） |

### 13.3 E2E 脚本

- `scripts/e2e/runner.py <exe> <cmd_b64> <out_log>`：cmd_b64 = `[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("…`n"))`，绕开 PS5.1 -File 注入 UTF-8 BOM 的 bug。
- `scripts/e2e/e2e.ps1`：4 用例（工程增量/单文件/多文件/读查）逐用例工程副本隔离 + 三态断言（`[安全拦截生效-模型未接入]`/`[安全拦截]`/`[工程未接入]`）+ 汇总。
- 大模型任务 E2E 慢（large CPU 推理 5-15 分钟），用后台跑 + TaskOutput 轮询。

---

## 14. 模块文件索引（速查，含关键方法）

| 路径 | 一句话 | 关键方法 |
| ---- | ---- | ---- |
| `src/Agent.Cli/Program.cs` | CLI 主循环 + 命令分发 + 归档 + 回滚 | Main / ApplyUse / ArchiveTask / AutoRollbackOnFailure / CleanOldRuns |
| `src/Agent.Infrastructure/AgentLoop.cs` | ReAct 主循环 + 防线全家桶 | RunAsync / BuildCycleSignature / 完成门三处接线 |
| `src/Agent.Infrastructure/Orchestration/Orchestrator.cs` | 三阶段编排 | RunAsync / CheckIncrementalPlanConflicts / BuildEngineeringGraphBlock / RecordSubMetricAsync |
| `src/Agent.Infrastructure/Orchestration/PlannerService.cs` | Plan JSON 生成 | GeneratePlanAsync / TryParsePlan / SimplifyRunCommand |
| `src/Agent.Infrastructure/Orchestration/ExecutorService.cs` | 逐文件生成/修复 | GenerateAsync / RepairAsync / IsSameOutput / 升级链 |
| `src/Agent.Infrastructure/Routing/ModelRouter.cs` | 三层漏斗路由 | RouteAsync |
| `src/Agent.Infrastructure/Routing/RuleEngine.cs` | L1 规则引擎 | Evaluate |
| `src/Agent.Infrastructure/Routing/IntentOverride.cs` | 写/读动词词表（唯一权威） | IsWriteTask / IsReadTask / Apply |
| `src/Agent.Infrastructure/Routing/RoutingIntelligence.cs` | 数据闭环调权 | TrySuggestDowngrade / TrySuggestReadTier |
| `src/Agent.Infrastructure/Routing/TaskFeatureClassifier.cs` | D1/D2 特征归因 | Classify / FeatureName |
| `src/Agent.Infrastructure/Routing/TaskMetricsAggregator.cs` | 特征类聚合（30s 缓存） | GetFeatureStats / 重试上限校准 |
| `src/Agent.Infrastructure/Routing/RoutingDecisionStore.cs` | 路由决策落库 | RecordAsync |
| `src/Agent.Infrastructure/Project/PathBoundary.cs` | 路径边界逐段比较 | IsInside |
| `src/Agent.Infrastructure/Project/SensitivePathGuard.cs` | 敏感源头拒（读黑名单） | IsDenied / LoadUserDenylist / FindWriteRootOverlaps |
| `src/Agent.Infrastructure/Project/ReadOnlyGuard.cs` | 只读工程预检 | ShouldBlock / FindExistingProjectFileRef |
| `src/Agent.Infrastructure/Project/ProjectPathDetector.cs` | 路径自动识别 + 写目标提取 | TryExtractProjectPath / TryGetAutoUsePath / TryGetWriteTarget / TryExtractDiskRoot |
| `src/Agent.Infrastructure/Project/CompletionGate.cs` | 写任务完成门 | Evaluate |
| `src/Agent.Infrastructure/Project/BackupService.cs` | taskId 隔离回滚 | BackupFile / RollbackTask / DryRunRollbackTask / PruneBackups / ClearNewMarkers |
| `src/Agent.Infrastructure/Project/TypeConflictDetector.cs` | P-D 写前类型冲突 | DetectConflicts / BuildExistingTypesHint |
| `src/Agent.Infrastructure/Project/SymbolIndexer.cs` | 符号索引（增量） | BuildAndSaveIncremental / Find |
| `src/Agent.Infrastructure/Project/ProjectScanner.cs` | 工程扫描 | Scan |
| `src/Agent.Infrastructure/Project/ProjectContextStore.cs` | 工程上下文缓存 | TryGet / Save |
| `src/Agent.Infrastructure/Project/DependencyAnalyzer.cs` | 依赖边 | Analyze |
| `src/Agent.Infrastructure/Quality/OutputVerifier.cs` | 输出判定单一来源 | HasCredentialFailure / StripHarmlessWarnings / HasStructuralError / HasGenericFailure / IsPlaceholderOutput / IsTemplateOutput |
| `src/Agent.Infrastructure/Metrics/SqliteMetricsCollector.cs` | 指标三表 | RecordAsync / GetServiceStatsAsync / GetFailureBreakdownAsync / BackfillFailedSubMetricsAsync |
| `src/Agent.Infrastructure/Metrics/FailureCategorizer.cs` | 失败类别词表 | Categorize |
| `src/Agent.Infrastructure/Metrics/ToolRejectionStore.cs` | 工具拒绝落库 | RecordAsync / GetSummaryAsync |
| `src/Agent.Infrastructure/Memory/AgentMemoryStore.cs` | 记忆存储（去重/治理） | Add / Search / Forget / Prune / Deduplicate |
| `src/Agent.Infrastructure/Memory/MemoryPromptBuilder.cs` | 失败教训注入格式 | BuildMemoryBlock |
| `src/Agent.Infrastructure/Memory/TaskMemoryExtractor.cs` | 任务归档提炼 | Extract |
| `src/Agent.Infrastructure/Rendering/` | 渲染层 | IRenderer / RendererBase.FormatEvent / SpectreRenderer / PlainTextRenderer / RendererFactory.Create |
| `src/Agent.Infrastructure/Tools/` | 10 工具 | 见 §7.1 |
| `tests/Agent.Infrastructure.Tests/Program.cs` | 450 断言 harness | Check / 各分组 |
| `scripts/e2e/e2e.ps1` + `runner.py` | E2E | 4 用例三态断言 |
| `docs/HANDOVER.md` | 交接与变更史 | 顶部 date-line 倒序 |
| `docs/DEVELOPER.md` | 速查手册 | — |
| `docs/DEVELOPER_DETAILED.md` | 本文（详细版） | — |
