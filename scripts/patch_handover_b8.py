# -*- coding: utf-8 -*-
# 10-13 批次8：HANDOVER 顶部插入条目（保持 CRLF 行尾 + UTF-8 BOM）
p = r'X:\.NET AI\LocalAiWorkbench\docs\HANDOVER.md'
with open(p, 'r', encoding='utf-8-sig', newline='') as f:
    t = f.read()
assert '\r\n' in t, 'expected CRLF'

NL = '\r\n'
entry = NL.join([
    "> > > > **10-13 批次8（10-13 B）：文本兜底路径 read_file 无上下文压缩（双路径漂移修复）**",
    "> 根因：30B 走情况 B（TryParseToolCallsFromText 文本兜底）——该路径仅 run_command",
    "> 截断，read_file 无短路/截断/折叠：X1 目标文件全文注入 38KB + read_file 结果 39KB",
    "> 原样进上下文 = 77KB，CPU 30B prefill >20min 超时实证（\"读盘根 index.html 并修复\"",
    "> 任务挂起假象：日志显示\"卡在读文件后无下文\"，实为 read_file 结果未压缩导致上下文膨胀）。",
    "> 修复：压缩逻辑抽公共方法 CondenseToolResult（短路/截断/折叠 + _ctxToolBytes 累计），",
    "> 情况 A 结构化调用与情况 B 文本兜底共用——消双路径漂移（ReadPathMatches 短路断言",
    "> Suites_Tools 986-990 已有，正斜杠归一已覆盖）。",
    "> 复测（读盘根 index.html 并修复语法错误）：[上下文预算] 目标文件全文注入（38344 字符）",
    "> → read_file 短路命中（已全文注入，消双份冗余）——文本路径压缩生效，上下文从 77KB",
    "> 降至约 38KB。任务 20min HttpClient 超时系 30B CPU 推理速度边界（3060 6G 部分",
    "> offload，GPU 利用率 15%），非架构缺陷。",
    "> tests 810 全绿 · commit 23465df",
    "",
]) + NL

marker = '> > > > **10-13 批次7'
idx = t.find(marker)
assert idx > 0, 'marker not found'
t = t[:idx] + entry + t[idx:]
with open(p, 'w', encoding='utf-8-sig', newline='') as f:
    f.write(t)
print('inserted at', idx)
