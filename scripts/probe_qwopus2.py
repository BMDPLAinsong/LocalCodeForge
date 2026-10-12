# -*- coding: utf-8 -*-
"""Qwopus3.5-4B 补充测试：B XML 完整性/多轮 + D 重试"""
import json, re, time, urllib.request

MODEL = "SetneufPT/Qwopus3.5-4B-Coder-MTP_Q4_64k_8GB-GPU"
BASE = "http://127.0.0.1:11434"

def chat(messages, temperature=0.3, num_ctx=32768, num_predict=None, timeout=300):
    body = {"model": MODEL, "messages": messages, "stream": False,
            "options": {"temperature": temperature, "num_ctx": num_ctx}}
    if num_predict is not None:
        body["options"]["num_predict"] = num_predict
    req = urllib.request.Request(BASE + "/api/chat", data=json.dumps(body).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8"))

sys_protocol = (
    "你是运行在 Agent 工作台里的编码助手。你可以调用以下工具：\n"
    "1. write_file：参数 path（文件路径）、content（文件内容）。\n"
    "2. run_command：参数 command（要执行的命令）。\n"
    "3. read_file：参数 path（文件路径）。\n"
    "工具调用必须严格使用以下 XML 格式，一次只能有一个工具调用：\n"
    "<tool_call><name>write_file</name><arg_value>path 的值</arg_value><arg_value>content 的值</arg_value></tool_call>\n"
    "收到工具结果后继续下一步。任务完成后输出最终总结。"
)

# ── B1：完整 XML 输出检查（不截断）──
print("# B1. XML 工具调用完整性")
r1 = chat([
    {"role": "system", "content": sys_protocol},
    {"role": "user", "content": "写一个 Python 计算器方法并运行验证"},
], num_predict=600)
c1 = r1.get("message", {}).get("content", "")
m = re.search(r"<tool_call>.*?</tool_call>", c1, re.S)
if m:
    xml = m.group(0)
    print("[XML 命中] 长度", len(xml))
    print("[XML 前 400]", xml[:400])
    print("[XML 后 200]", xml[-200:])
    name = re.search(r"<name>(.*?)</name>", xml, re.S)
    args = re.findall(r"<arg_value>(.*?)</arg_value>", xml, re.S)
    print(f"[解析] name={name.group(1) if name else None!r} args={len(args)} 个: {[a[:40] for a in args]!r}")
else:
    print("[XML 未命中] content 前 300:", c1[:300])
    print("——工具链不稳信号")

# ── B2：模拟内核多轮（把 B1 XML 喂回，工具成功，看下一跳）──
print("\n# B2. 多轮连续性（工具结果 → 下一动作）")
msgs = [
    {"role": "system", "content": sys_protocol},
    {"role": "user", "content": "写一个 Python 计算器方法并运行验证"},
]
if m:
    msgs.append({"role": "assistant", "content": c1})
    msgs.append({"role": "user", "content": "<tool_result>write_file: 已写入 calculator.py，共 1800 字符。\n接下来请运行验证：</tool_result>"})
    r2 = chat(msgs, num_predict=500)
    c2 = r2.get("message", {}).get("content", "")
    m2 = re.search(r"<tool_call>.*?</tool_call>", c2, re.S)
    if m2:
        xml2 = m2.group(0)
        name2 = re.search(r"<name>(.*?)</name>", xml2, re.S)
        print(f"[B2 命中] name={name2.group(1) if name2 else None!r} → 多轮工具链可用")
        print("[B2 XML 前 300]", xml2[:300])
    else:
        print("[B2 未命中] content 前 300:", c2[:300])
        print("——工具结果后未继续走工具链（可能是收尾或掉链）")

# ── D：重试（限输出防超时）──
print("\n# D. 中文指令跟随（限 num_predict）")
try:
    rd = chat([
        {"role": "system", "content": "你是中文编码助手。用中文回答，输出简洁。"},
        {"role": "user", "content": "用 Python 写一个根据 IP 地址获取天气的方法（用免费免 key 的接口），给出完整代码和中文注释。"},
    ], num_predict=400, timeout=180)
    print("[D]", rd.get("message", {}).get("content", "")[:500])
except Exception as e:
    print("[D 超时/失败]", type(e).__name__, str(e)[:120])

print("\n=== 补充测试完成 ===")
