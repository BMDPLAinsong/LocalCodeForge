# -*- coding: utf-8 -*-
import io
p = r'X:\.NET AI\LocalAiWorkbench\docs\HANDOVER.md'
s = io.open(p, encoding='utf-8-sig').read()
marker = '> **10-09 追加：模型升级为 strata qwen3.8-'
assert marker in s, 'marker not found'
seg = '''

> **10-09 读任务三件套（"分析项目"必败修复）**
> 背景：`use <工程> + 分析一下这个项目` 实测 20 步/359s 截断失败——模型发散探索（4 list_dir + 16 read_file）、3 次猜错路径（AgentLoop.cs / ProjectAnalyzer.cs / TaskEvent.cs 不存在且无候选提示）、30B CPU 每步 ~18s、max_steps=20 一刀切。
> 三个工程化修复（数据驱动，非提示词；方案 1→2→3 确认后实施）：
> 1. **read_file 失败附候选路径**：文件不存在 + 工程模式 → `ProjectScanner.FindFileCandidates`（精确同名→全列；扩展名相同 + 名字公共前缀 ≥4 模糊匹配，TaskEvent.cs→TaskEventHub.cs）→ 拦截消息附"工程中相近文件（候选路径）"，模型不再继续猜；沙箱模式无工程索引保持原样。数据源：`ProjectScanner.ListFilesWithSize`（复用 Walk 排除规则）。
> 2. **分析类任务注入核心文件清单**：命中 `TaskFeatureClassifier.ReadQuery`（确定性词表，含"分析/解释/总结"前缀）→ 注入 `ProjectScanner.CoreFileLines`（入口 + sln/slnx + 依赖清单 + 按体积 Top8 源码文件，均为真实相对路径）——模型 5-8 步读完核心文件即可总结，不再逐文件发散。
> 3. **读任务步数分级**：`AgentOptions.ReadQueryMaxSteps=40`（默认），AgentLoop 循环前按 ReadQuery 特征类取 `max(MaxSteps, ReadQueryMaxSteps)`（11 处引用统一替换 effectiveMaxSteps）；写任务保持 MaxSteps=20 防空转。
> 验证：构建 0 错误；tests 648→665（+17：分类词表回归 5 / 配置 2 / 候选匹配 4 / 核心清单 4 / read_file 模式分支 2）；E2E 复测同款任务 **12 步自主收敛、0 猜错路径**（对比失败日志 20 步截断/3 次猜错），总结质量完整（结构/特性/技术栈正确）。改动文件：AgentOptions.cs、ProjectScanner.cs、ReadFileTool.cs、AgentLoop.cs。

'''
s = s.replace(marker, seg + marker, 1)
io.open(p, 'w', encoding='utf-8-sig', newline='').write(s)
print('插入完成, len =', len(s))
