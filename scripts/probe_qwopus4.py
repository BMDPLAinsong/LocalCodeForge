# -*- coding: utf-8 -*-
"""Qwopus3.5-4B 多轮工具链连续性（CDATA 协议）"""
import json, re, urllib.request

MODEL = "SetneufPT/Qwopus3.5-4B-Coder-MTP_Q4_64k_8GB-GPU"
BASE = "http://127.0.0.1:11434"

def chat(messages, temperature=0.3, num_ctx=32768, timeout=300):
    body = {"model": MODEL, "messages": messages, "stream": False,
            "options": {"temperature": temperature, "num_ctx": num_ctx}}
    req = urllib.request.Request(BASE + "/api/chat", data=json.dumps(body).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8"))

sys_cdata = (
    "你是运行在 Agent 工作台里的编码助手。工具：write_file(path, content)、run_command(command)。\n"
    "工具调用格式（严格 XML，一次一个）：\n"
    "<tool_call><name>write_file</name><arg_value>path</arg_value><arg_value><![CDATA[文件内容]]></arg_value></tool_call>\n"
    "注意：文件内容必须放在 CDATA 里。\n"
    "收到工具结果后继续下一步。任务完成输出总结。"
)

def extract(xml):
    name = re.search(r"<name>(.*?)</name>", xml, re.S)
    args = re.findall(r"<arg_value>(.*?)</arg_value>", xml, re.S)
    return (name.group(1) if name else None), [a[:60] for a in args]

msgs = [
    {"role": "system", "content": sys_cdata},
    {"role": "user", "content": "写一个 Python 计算器方法并运行验证"},
]

print("# 多轮工具链：写 → 跑 → 修复/收尾")
for turn in range(4):
    r = chat(msgs)
    c = r.get("message", {}).get("content", "")
    m = re.search(r"<tool_call>.*?</tool_call>", c, re.S)
    if not m:
        print(f"[第{turn+1}轮] 无工具调用（收尾）→ content 前 200: {c[:200]!r}")
        break
    name, args = extract(m.group(0))
    print(f"[第{turn+1}轮] 调用 {name}  args={args!r}")
    msgs.append({"role": "assistant", "content": c})
    # 模拟工具结果
    if name == "write_file":
        result = "<tool_result>write_file: 已写入，共 1500 字符。\n下一步请运行验证。</tool_result>"
    elif name == "run_command":
        result = "<tool_result>run_command: 退出码 0，程序输出正常。\n任务已完成，请给出总结。</tool_result>"
    else:
        result = "<tool_result>read_file: 文件内容（略）</tool_result>"
    msgs.append({"role": "user", "content": result})

print("\n=== 多轮测试完成 ===")
