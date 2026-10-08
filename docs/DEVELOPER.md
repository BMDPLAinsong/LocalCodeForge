# LocalAiWorkbench 开发者维护文档

> **详细版见 `docs/DEVELOPER_DETAILED.md`**（以任务输入→交付完整链路为主线的逐步展开，
> 含源码级判定规则、数据表 schema、词表全文与改造指引；本文为速查版）。

> 面向
>
> **后续维护与开发者**
>
> 。读者：接手本内核做功能开发、缺陷修复、性能优化的人。
> 撰写：2026-10-04（基线：git main @ e94e2a8 + 本地待提交、tests 353/353、构建 0 错误）。
> 与 
>
> `HANDOVER.md`
>
>  的关系：HANDOVER 是
>
> **交接与变更史**
>
> （谁在何时做了什么、踩了什么坑）；本文是
>
> **开发手册**
>
> （系统怎么工作、改哪里、怎么改、怎么验证）。开发前两者都读。



***

## 0. 铁律（用户的硬约束，违反即返工）



1. **放弃提示词工程**。一切缺陷修复优先用**工程化 / 数据结构性**手段（确定性代码、词表、表结构、工具行为约束、拒绝消息），禁止靠堆 System Prompt / 修复器规则 "打地鼠"。

2. **任何修改先出方案，用户确认后才动手**。方案要写明：改什么、为什么、影响面、验证方式。

3. **只用本地模型**。云端（DashScope）仅预留未来兜底，当前不使用；模型形态已固化为**单 tag&#x20;**`law-unified`**（30B）三角色**，不再额外拉取模型。

4. **测试不落 C 盘 Temp**。统一落 `X:\.NET AI\LocalAiWorkbench\workspace\test-tmp\<PID>`（已 gitignore）。

5. **诚实失败优于假完成**。任务做不了就如实失败（走 H2/H4 防线），禁止 "工具已验证" 式假完成。

6. 批量改动后必须：构建 0 错误 → tests 全过 →（涉及模型行为时）E2E 复测 → 同步 HANDOVER/DEVELOPER → git 提交（本机仓库，沿用现有 Git 配置）。



***

## 1. 项目概览



| 项    | 值                                                                                                                                                                   |
| ---- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 性质   | 纯本地 AI 编码助手内核（控制台），用户输入自然语言 → 智能路由 → 规划 / 生成 / 写文件 / 运行验证 / 自动修复闭环                                                                                                  |
| 解决方案 | `LocalAiWorkbench.slnx`（新版 XML solution）                                                                                                                            |
| 目标框架 | net10.0（**勿降级**）                                                                                                                                                    |
| 核心依赖 | Microsoft.SemanticKernel 1.80.1 + SK OpenAI Connector（走 Ollama `/v1` 兼容端点，全本地）+ Microsoft.Data.Sqlite                                                               |
| 模型   | Ollama：classifier=`qwen2.5:1.5b`；small/large/agent\_pro 三角色全部 = `law-unified`（30B Qwen3-Coder-A3B，同 tag 单驻，请求级 temperature 区分：small 0.3 /agent\_pro 0.5 /large 0.7） |
| 版本控制 | 本机 git 仓库（main，91+ commits，无远端）                                                                                                                                     |
| 测试   | `tests/Agent.Infrastructure.Tests`（**零依赖断言 harness**，625/625，`dotnet run --project` 一键回归）                                                                           |
| 数据落盘 | `workspace/`：sessions.db、runs/、project\_context.db、agent\_memory.db、symbol\_index.db、test-tmp/、e2e/                                                                 |

### 1.1 三层项目结构



| 项目                         | 职责                                                                                                                       | 依赖                    |
| -------------------------- | ------------------------------------------------------------------------------------------------------------------------ | --------------------- |
| `src/Agent.Core`           | 抽象接口（IAgentLoop/IAgentTool/IToolRegistry/ISessionStore）+ 纯模型（AgentOptions/AgentEvent/Plan/TaskMetric/SystemPrompt 等），零依赖 | 无                     |
| `src/Agent.Infrastructure` | **全部实现**：AgentLoop、Orchestration、Routing、Project、Tools、Metrics、Memory、Quality、Session、Ollama                             | Core + SK + Sqlite    |
| `src/Agent.Cli`            | 控制台入口：交互循环、命令分发（use/plan/orch/stats/memory/rollback）、事件渲染、健康检查、run 目录清理、任务归档                                             | Core + Infrastructure |



***

## 2. 快速上手



```
Set-Location "X:\.NET AI\LocalAiWorkbench"

# 全量构建（注意：必须 slnx 全量，不能只 build CLI，见 §9 构建坑）
dotnet build LocalAiWorkbench.slnx --no-incremental

# 确定性回归（零依赖，几秒）
dotnet run --project tests\Agent.Infrastructure.Tests\Agent.Infrastructure.Tests.csproj

# 运行 CLI（Ollama 未启动会自动拉起；交互终端启动即自动起 Web Dashboard 并开浏览器，无头/管道模式自动跳过；LAW_NO_AUTODASH=1 可关）
