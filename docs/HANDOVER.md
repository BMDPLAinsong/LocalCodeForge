# LocalCodeForge 项目交接文档

> 最后更新：2026-10-17（批次8b：② 记忆倒排 + ③ /api/tasks 下沉后）
>
> **文档地图**：
> - 本文 = **当前状态 + 近两周变更史**（10-15 Dashboard 时代起）。日常接手先读本文 + §速览。
> - `docs/history/HANDOVER-archive-2026-09-22_10-15.md` = **全量批次史**（2026-09-22 初撰的架构盘点、10-03~10-15 全部批次记录、2026-09-22 事实清单），备查不日常读。
> - `docs/DEVELOPER.md` / `docs/DEVELOPER_DETAILED.md` = 开发手册（铁律 / 架构 / 改哪里 / 怎么验证）。
>
> 与 `DEVELOPER.md` 的关系：HANDOVER 是**交接与变更史**（谁在何时做了什么、踩了什么坑）；DEVELOPER 是**开发手册**（系统怎么工作、改哪里、怎么改、怎么验证）。开发前两者都读。

---

## 当前状态速览（2026-10-16）

| 项 | 状态 |
|---|---|
| 构建 | 0 错误（9 个既有 nullable 警告基线，见 AgentLoop/Orchestrator 等） |
| 测试 | **885/885**（`dotnet run --project tests/Agent.Infrastructure.Tests`，零依赖 harness + 91 区段顺序门禁） |
| 一键门禁 | `powershell -NoProfile -File scripts/verify.ps1` —— **六门全绿**：① build ② tests ③ js-syntax（node --check / 无 node 走 `jscheck.py` + selftest）④ drift-check（基线 51 处/19 文件，阈值 60）⑤ rename-gate ⑥ dashboard-inline（app.css/app.js ↔ index.html 同步） |
| 提交门禁 | pre-commit 钩子（`scripts/git-hooks/pre-commit`）自动跑 verify.ps1，失败阻断（`--no-verify` 可绕） |
| 主交互入口 | Web Dashboard（CLI 交互启动即自动起 + 开浏览器；无头/管道自动跳过；`LAW_NO_AUTODASH=1` 可关）；CLI 保留兜底 |
| Dashboard 前端 | 源码三拆分：`DashboardPages/app.css` + `app.js` 为编辑源，`index.html` 为生成产物（运行时仍是单文件嵌入页，csproj 嵌 index.html，tests 契约 40+ 项逐字节锁定）；品牌强调 = **熔金炉火**（`--accent/--accent-2/--accent-ink` 三值换肤 + color-mix 派生） |
| CLI 命令集 | use [--write] / stats / memory / rollback / plan（只读预演 Planner）/ diff / dashboard / new / exit / help（`orch` 调试旁路已于批次①移除） |
| 持久层 | sessions.db / metrics.db / agent_memory.db / symbol_index.db / project_context.db；store 全部 DI 单例（请求路径零建表）；turns 90 天 / events 30 天 / routing 90 天保留期清理 |
| 事件 schema | task_events schema_version=1（老库 PRAGMA 检测 ALTER 迁移）；SSE 建连首帧 `joined` 标记（中途接入走回放） |

**已知边界 / P2 清单**（均已登记，按需排期，详见 history 各批次）：
1. P-D：30B 模型增量修改纪律弱（偶发整文件重写带重复类型）→ 编译验证 + 回滚兜底，非提示词解决。
2. 工程增量 expected_output 为 advisory（Planner 自产期望无工程基准）；"任务输入自带精确输出要求"的漏判面未验证。
3. `/api/files` 仅限 run 工作区；工程模式产物在工程根，web 端看不到。
4. 记忆检索已升级 **token 化倒排**（10-17 ②：精确匹配 + term 索引命中，取代 LIKE 子串扫描）；**阶段 4 向量（语义）检索未做**。
5. webUI P1/P2 未做：seq 联动 SSE 归属、管理命令插队、CLI 守护进程化。
6. ~~`/api/tasks` 内存过滤~~ **已下沉 SQLite 查询层**（10-17 ③：q/status/since 全库内 WHERE+LIMIT）。
7. FailureCategorizer 读 `ToolResult.Code`（与存储格式对齐）为批次遗留后续轮。
8. README demo.gif 占位待录屏；awesome 列表 PR、Show HN 发帖待办（7p）。

**近三次提交**：`8b` 批次②③双下沉（见下）→ `7e3c1f0` 批次④文档归档 → `d480a9d` 批次③源码拆分（再往前 `09d8119`/`bb9751e`/`cbfb822` v4 换肤）。

---

## 近期批次（10-15 ~ 10-16）

7r. **【已完成】Dashboard v2 视觉重构（10-15，7q 之上：视觉系统 + 交互细节；单文件架构与全部测试契约不变）**：**设计上下文（用户确认）**——受众 = 本人长时间盯屏 + 对外演示；方向 = 现代精致暗色（"精密仪器"感：暗但不黑、层次靠明度阶梯与语义色条，拒绝"万物卡片 + 通用阴影 + 霓虹点缀"的泛 AI 审美）。实施：①**CSS 设计系统全量重写**（165→357 行）——oklch 中性阶梯（`--bg/--bg-sunk/--panel~--panel-3/--border~--border-2/--fg~--faint`）+ 低饱和语义色（绿/红/黄/蓝/青）+ 双字体栈（UI 用 Segoe UI Variable/system-ui，数据与代码用 Cascadia Code/JetBrains Mono/Consolas）+ 4px 基准间距与 clamp 流式尺寸 + 统一缓动 `--ease`(ease-out-quart)；卡片改"极细边框 + inset 顶部高光"替代通用投影；事件行改**左侧 2px 语义色条**（扫描效率 > 全行染色，久看不疲劳）；卡片 h2 改"小字号 + 大字距 + 大写"仪表标签；品牌 h1 加斜切方块标记（几何记忆点，非图标字体）。②**DOM 内联硬编码色收敛**：cmdInput / evFilter / taskQ / depFile / taskStatus / filesPreview 共 7 处内联 `#0d1117/#1a1f2e/#2f3a52` → 统一 `.inp`/`.sel` class（顺带补齐此前**未定义却被引用**的 `.dim`）。③**空状态 / 错误态结构化**：新增 `emptyState(title, desc, {err, action})` 四段式（标记 + 标题 + 说明 + 行动），替换 events / dep / stats / tasks / deps 共 6 处"一行灰字"——只说"没有"改为说清"现在什么状态 + 下一步能做什么"；错误态走 `is-err` 红通道并给"重试 / 清空筛选 / 显示全部"按钮。④**键盘可达**：此前全站 **0 处 aria、0 条 :focus 样式**，可点击 div/span（`.tc-head`/`.tp-call`）只有 onclick → 补 `role=button` + `tabindex=0` + Enter/Space 全局 keydown 委托 + `:focus-visible` 描边。⑤**微交互**：按钮 hover/active 位移、过滤按钮 active 改下标线（非实心块）、卡片展开 reveal、提交按钮 `is-busy` 细进度条、在线状态点呼吸动画，并加 **`prefers-reduced-motion` 兜底**。⑥**响应式**：新增 720px 断点（命令条换行、窄屏隐藏次要副标题），保留 1100px 单栏。**红线与验证**：单页被 `Suites_Dashboard.cs` L251–294 以**逐字节**契约锁定 40 项（含 `<div class="colL">\n  <div id="cmdBar"`、`#cmdBar { position:sticky; top:var(--headH)`、Monaco `paths: { vs: "/vendor/monaco/min/vs" }`、禁含 `LocalAI Workbench` 与 `});\n}\n}\n// ── stats ──`）→ 改造后**契约 0 缺失 / 0 违禁**（校验脚本 `workspace/e2e/apply_css.py`）；旧 CSS 变量名必须保留（DOM 内联仍引用 `var(--fg/--border/--dim/--blue)`），新增变量只做补充。**验证**：构建 0 错误（警告基线 9）；tests **875/875**；drift OK。**预览**：Dashboard 有无头守卫（`Console.IsInputRedirected` → 拒绝启动 Kestrel，保护 E2E 输出逐字节兼容）→ 无头环境改走 mock 预览服务 `workspace/e2e/dash_preview.py`（静态 + mock `/api/stats|tasks|health|deps|files` + SSE 真实事件序列），本机 `http://127.0.0.1:8777/` 验证视觉与交互。

7s. **【迭代】Dashboard v3 视觉打磨（10-15，7r 之上：治"配色太灰太闷" + "像工具不像产品"）**：用户复盘两点反馈——① 暗底 + 低饱和语义色缺视觉重心；② 整体气质太克制/冷淡、缺个性记忆点。**方向（用户指定）**：更大胆抓眼（演示优先）+ 精致高级感。**v3 仅换 CSS 设计系统，DOM/JS 契约不变**：①**拉开对比**——底色压深（16.5%→12.5%）+ 正文提亮（91%→97%）+ 语义色饱和度整体上调（绿 .15→.19 / 红 .17→.215 / 青 .11→.165）；②**双强调色**——主强调电光青（品牌/交互/焦点一贯色）+ 次强调琥珀金（呼应"锻造"命名，仅用于品牌标记与完成态辉光）；③**事件流做成"显示面板"**（下沉底 + 内阴影）成页面视觉重心；④**去终端感**——按钮/表头/空态文案改 UI 字体（此前全继承等宽），卡片标题去大写大字距改 13px/600 + 青色竖条，状态信息改胶囊标签；⑤**大胆但克制点缀**——渐变仅 4 处（品牌斜切方块标记[金青渐变+微光] / header 底部渐变分隔线 / 主按钮渐变+辉光 / 页面顶部环境光），避霓虹廉价感；⑥ 精致细节——双层阴影(接触+扩散)+inset 顶部高光、数字 tabular-nums、8px 统一圆角、focus 环、hover 位移。验证：构建 0 错误（警告 9）；契约 0 缺失/0 违禁（apply_css.py 重校验）；tests 875/875；drift OK。预览：mock 服务 `workspace/e2e/dash_preview.py`（http://127.0.0.1:8777/）。
7t. **【已完成】Web Dashboard 定为主交互入口（10-15，用户指令"先执行主入口，再看P0"）**：**事实核查**——自动启动骨架 10-10 已存在（交互终端启动即自动起 Dashboard + 自动开浏览器；LAW_NO_AUTODASH=1 关闭开关；无头守卫 [管道/重定向跳过，E2E 输出逐字节兼容]；幂等 [重复 dashboard 命令只提示不重启]；CLI 输入与 Dashboard POST /api/commands 统一汇入 CommandHub 一次一任务串行）。本次"执行"= 补齐**定位表达与文档一致性**（零行为变更，不重复造启动逻辑）：①Program.cs 启动横幅明确"**主交互入口：Web Dashboard**（已自动启动）…CLI 保留兜底：use/stats/memory/rollback/plan/orch/new/exit/help 照常可用"；②Program.Commands.cs help 文本 dashboard 项标注"**主交互入口 Web UI**"；③README.md 快速开始改为 "**The web dashboard is the primary interaction surface**: the CLI auto-launches it on startup (loopback + token) and opens your browser… The CLI itself stays as a fallback (pipeline/headless safe)"。**验证**：build 0 错误（警告基线 9）；tests **875/875**（纯展示文本零行为变更，无测试面变化；E2E 无头路径不触发自动启动分支，输出不受影响）。**P0 在主入口视角再审视**：四项（品牌 / /api/health / 通知 / 筛选重跑）全部服务 Web 主入口且无缺口；唯一结构性风险 = /api/tasks 过滤为**上限内内存过滤**（当前 50 条上限，刻意不扩散 SQLite 查询层），任务量放大后需下沉查询层。
7u. **【已完成】Dashboard 前端 JS 语法根因修复 + 错误态反馈 + no-store + 测试幂等 + JS 语法门禁（10-16）**：用户反复报告「加载中…/点提交无响应」（6 轮截图）后 Console 证据 `Uncaught SyntaxError ...:906` 锁定**最终根因——index.html L906 `emptyState("…点"加载"重建索引。")` 字符串内嵌未转义英文双引号 → 整段 `<script>` SyntaxError → 全部 JS 失效**（SSE/提交/状态行/刷新全静默；既有 bug，契约测试只查 HTML 字符串不查 JS 语法一直漏网，与 10-10 同因不同处）。修复：`点"加载"` → `点「加载」`（node --check 全量语法校验通过）。**配套工程化**：①**前端错误态反馈**（此前失败路径全静默）——fetch 全局 12s 超时（AbortController）+ `wrapFetch` 统一把网络失败转 `{ok:false,status:"连接失败/连接超时"}` 复用 `!r.ok` 错误 UI；SSE onopen/onerror 驱动顶部状态行（「在线（实时流已连接）」/「实时流连接异常（自动重连中…）」）；token 缺失时状态行提示；fetchHealth 失败显示「状态不可用」。②**GET / 加 `Cache-Control: no-store`**（页面随服务版本内嵌，杜绝浏览器缓存旧页面——"重启后仍旧界面"根因之一）。③**测试幂等修复**——`Suites_Dashboard.RunEventSchemaVersion` 固定路径 `new.db/old.db` 复跑残留 → `table task_events already exists` 崩溃（本批全量重编实测暴露）；改 `Guid.NewGuid().ToString("N")` 唯一化。④**JS 语法门禁**——`scripts/verify.ps1` 新增 3/4 步：python 提取 index.html `<script>` → `node --check`（纯 ASCII；node/文件缺失跳过）；pre-commit 提交前自动全绿。**验证**：build 0 错误（警告基线 9）；tests **875/875**；node --check 通过；commit `caf566d` → 远端 `9089ca5d`（300 blob 全量对齐 + tree 校验一致）。**教训**：大 HTML 的字符串内嵌引号是 JS 崩溃温床，字符串断言抓不住 → 语法门禁进 pre-commit（本批落地，防打地鼠）。
7v. **【已完成】项目全量改名 LocalAiWorkbench -> LocalCodeForge（10-16，用户指令"全都改完不要遗漏"）**：仓库目录 Move-Item 为 X:\.NET AI\LocalCodeForge（git remote 不变）；CLI 品牌横幅/框线、slnx git mv、README/DEVELOPER/DEVELOPER_DETAILED 清零；HANDOVER 标题+速览表+构建命令同步（7p/7v 迁移记录保留旧名）；Suites_Dashboard use 样例路径与 patch_handover_b8.py 路径同步新目录；verify.ps1 新增 5/5 rename-gate（scripts/rename-gate.py，白名单仅 HANDOVER 迁移记录，防打地鼠）；验证 build 0 错误 / tests 875/875 / JS / drift / rename-gate 全绿；本地 commit 5be85b9 -> 远端 4f05fb04（tree 全量一致校验）。

7w. **【已完成】Dashboard v4 品牌换肤 + A/C 结构迭代（10-16，commit cbfb822，本文补登）**：用户否掉 v3 的电光青主强调（"这个我不喜欢"）后做两轮——**① A 信息架构重组**：一长列卡片改三区分区（左「任务中心」= 实时流+历史任务(默认展开)+详情回放；右「工程」= 产物+依赖图谱、「运行面板」= 统计(默认展开)+调权+记忆+备份），点击历史行回放卡片自动展开，分区头金→青渐变刻度线；**② C 零依赖微可视化**：统计改分段条（成功率构成/阶段耗时堆叠/时代隔离，图例带计数去重复文本）、调权改阈值仪表（fill=当前值、tick=阈值、pill=触发/保持）、记忆改构成分段条+命中热度条——纯 CSS + segBar 助手，零新增依赖。**③ 品牌换肤（v4）**：抽令牌层 `--accent/--accent-2/--accent-ink` + color-mix 派生 14 个透明度梯度，15 处硬编码青/金色值清零，旧 `--cyan/--gold` 退为别名自动跟随；默认改**熔金炉火**（与"锻造台"叙事自洽），警告黄 hue 92→100 与琥珀保持可辨；预览版带四套配色 A/B 切换条（熔金/铜橙/靛紫/电光青·原，mock 数据本地服务）。验证：build 0 错误 / 契约 11/11 本地复算通过（cmdBar 吸顶、函数名、stats 闭合防漂移全保）/ JS 括号配平通过。

7x. **【已完成】复盘批次① CLI 调试命令治理（10-16）**：项目复盘（tests 875/875、drift/rename-gate 全绿）定级 P0 一项——`orch ` 调试旁路常驻生产 CLI（Program.cs 原 486-505）。代码确认绕过面：直接 `orchestrator.RunAsync(..., CancellationToken.None)`，跳过 taskId 备份隔离 / 任务超时 / q·web 取消 / 事件持久化 / SSE 多播 / 记忆归档 / 失败自动回滚 / 备份清理共八道机制——工程可写模式下失败即留**不可回滚残写**（备份无 taskId 归属，rollback 命令无法靶向）。**处置**：`orch` 整块移除（README/E2E 零使用，help 与 DEVELOPER_DETAILED 命令表同步）；`plan` 只读零副作用（仅调 Planner 打印），转正为受控检视命令并登记 help（"调试：只读预演 Planner 规划"），去除"【临时测试】"注释。顺带修正 DEVELOPER_DETAILED 过时段落（Tab 补全/↑↓ 历史已于 10-12 因 IME 丢字降级为 `Console.ReadLine()`，cliCommands 列表已不存在于代码）。**验证**：build 0 错误 / tests 875/875 / 无头冒烟 help 输出逐字确认（无 orch、plan 带只读标注）。若后续需要"强制编排"测试能力，正确形态是普通入口加 `--force-orch` 开关（一条入口全套机制），不再开旁路。

7y. **【已完成】复盘批次② JS 语法门禁 python 通道（10-16）**：复盘定级 P0 二项——verify.ps1 第 3 门（7u 新增）依赖 `node --check`，而本开发机无 node → 门禁**静默跳过**，恰好在 7u 事故类别（内嵌 JS 语法错误整段失效）上失明；本次 v4 换肤 15 处 CSS 令牌替换也只能做括号配平粗检。**落地**：①新 `scripts/jscheck.py`——纯 python 字符级状态机校验（node 零依赖）：括号配平（字符串/注释/正则/模板字面量按原子 token 跳过）、字符串/模板/正则全部终止校验、`/` 除号 vs 正则字面量消歧（前一有效 token 为 return/typeof 等关键字或运算符 → 正则；标识符/数字/`)` → 除号）；②verify.ps1 第 3 门改双通道——node 在 → `node --check`（权威）；node 缺 → jscheck.py，**不再静默跳过**（两者皆缺才判 BLIND 计入失败）；③`scripts/jscheck_selftest.py` 固化 5 用例（真实文件必须过 + 四类故障必须被抓：字符串内嵌未转义引号[7u 事故类]/括号不配平/模板字面量未终止/多余右括号），接入 verify 第 3 步前置——门禁自身也要防回归。**开发实证**：jscheck 首版即踩两个真 bug（模板 `${...}` 结尾 `}` 会"偷"外层作用域括号——因 `${` 未向括号栈压占位符；深度登记方向反），均被自测用例抓住后修复——这正是固化为门禁前置的理由。**验证**：verify.ps1 五门全绿（JSCHECK SELFTEST OK → JS SYNTAX OK (python fallback) → DRIFT OK → RENAME-GATE OK）；pre-commit 钩子直调 verify.ps1 自动覆盖。

7z. **【已完成】复盘批次③ Dashboard 前端源码三拆分（10-16）**：复盘定级 P1——index.html 单文件 1675 行/~90KB（CSS+DOM+JS 全内嵌，四轮视觉重构靠脚本打补丁），但运行时契约是"单文件嵌入页"（csproj 只嵌 index.html；GET / 直读流；tests 40+ 项契约逐字节断言在页面内容上）——**不能改产物形态，只能拆源码**。**落地**：`DashboardPages/app.css`（28839 字符，原 `<style>` 内容）+ `app.js`（53980 字符，原 `<script>` 内容）为编辑源，index.html 退化为生成产物（HTML 骨架 + 内联 css/js）；`scripts/inline_dashboard.py` 三模式——`--extract`（一次性抽取，已执行并验证往返字节一致）/ 默认 build（源→产物）/ `--check`（内存重建比对，漂移报行级 diff，exit 1）；verify.ps1 新增 **6/6 DASHBOARD INLINE 门**（手改单侧即拦，修复命令随错误信息给出）。**编辑流实测**：改 app.js → build → index.html 含改动 → 还原 → 字节级复原 → check 绿；漂移注入实测 exit 1 + 定位行。**契约零动**：csproj 无需改（显式 Include 无 glob，app.css/js 不会被误嵌）；tests 875/875 不经任何改动（页面内容不变）。**验证**：六门全绿（build/tests/js-syntax+selftest/drift/rename-gate/dashboard-inline）。DEVELOPER.md §2 补编辑流说明。

8b. **【已完成】P2 双下沉：② 记忆检索 token 化倒排 + ③ /api/tasks 下沉 SQLite 查询层（10-17，用户复盘后拍板）**：② AgentMemoryStore 新增 `memory_terms` 倒排表（term→memory_id，weight 2=topic/1=content，同 (id,term) 只存高权重），Add/合并更新同步重建索引，Initialize 存量回填（terms 空且有存量行时一次性全量）；Search 由「LIKE 子串下推 + C# Contains 打分」改为「输入 Tokenize 精确匹配倒排 + 每 term 取字段高权重求和」（与旧评分 topic+2 else content+1 等价）。**语义升级（明示）**：LIKE 子串匹配 → token 精确匹配——Method1500 天然不被 _ 通配误匹配（无需转义）、term 索引可命中（非全表扫描）；唯一子串依赖断言 `Search("注释") content 命中` 随语义改 `Search("加了注释")` 并新增反例锁（"注释" 不再模糊召回仅含"加了注释"的记忆）。③ IMetricsCollector 新增 `GetFilteredRecentAsync(q,status,since,take)`——q 子串（LIKE 转义 + ESCAPE）、status ok/fail/susp、since（started_at ≥ UTC epoch）全下推 SQL（WHERE+LIMIT 库内），DashboardHost /api/tasks 移除「上限内样本内存过滤」（语义等价：无过滤=GetRecentAsync；status 非法值不加条件；since 边界 UTC 归一）。**测试**：新增记忆倒排 7 断言（索引落库/精确命中/权重优先/合并重建/存量回填）+ /api/tasks 下推 2 断言（过滤后 LIMIT 全量过滤再截断）；tests 875 → **885/885**，六门全绿。

8a. **【已完成】复盘批次④ 文档归档（10-16）**：项目复盘定级 P1——HANDOVER.md 累积至 1167 行（2026-09-22 初撰架构盘点 + 10-03~10-16 四十余批次追加式记录），检索成本极高。**处置（纯文档搬迁，零知识删除）**：lines 1–1098（至 7q）+ 旧计划项 6/7 + 2026-09-22 事实清单 → 整体迁入 `docs/history/HANDOVER-archive-2026-09-22_10-15.md`（带归档横幅）；本文重构为「当前状态速览 + 近两周批次（7r~7z）+ 回滚安全矩阵」三段式，回滚矩阵去 §12 悬空编号改「附」。**配套**：rename-gate.py 白名单从"路径含 HANDOVER.md"扩为"含 HANDOVER.md 或位于 docs/history/"（历史文件必然含旧名 LocalAiWorkbench 迁移记录，否则归档即触发门禁）；DEVELOPER.md 文档地图补 history 指针。**验证**：rename-gate/drift/其余门禁全绿；tests 875/875 不受影响（无代码改动）。

## 附：回滚安全交互矩阵（已审计 · 2026-10-03 P-A/P-B/P-C 批次，tests 267 项固化）

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

