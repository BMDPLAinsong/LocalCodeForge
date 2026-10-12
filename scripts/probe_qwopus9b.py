# -*- coding: utf-8 -*-
"""Qwopus3.5-9B 能力边界测试（与 4B 同口径）"""
import json, re, urllib.request

MODEL = "SetneufPT/Qwopus3.5-9B-Coder_Q3_64k_8GB-GPU"
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

def show(label, resp, n=500):
    msg = resp.get("message", {})
    content = msg.get("content", "")
    tc = msg.get("tool_calls")
    print(f"\n=== {label} ===")
    print(f"[tool_calls] {json.dumps(tc, ensure_ascii=False) if tc else '无'}")
    print(f"[content] {content[:n]}")
    if resp.get("error"):
        print(f"[ERROR] {resp['error']}")

# ── A 直接代码生成 ──
print("# A. C# 奇数偶数 直接生成")
show("A", chat([
    {"role": "system", "content": "你是一个代码助手，直接输出完整可编译的代码。"},
    {"role": "user", "content": "用 C# 写一个判断奇数偶数的函数，含 Main 测试正数/负数/零。"},
]))

# ── B 工具调用：默认协议（裸 XML）──
sys_protocol = (
    "你是运行在 Agent 工作台里的编码助手。你可以调用以下工具：\n"
    "1. write_file：参数 path（文件路径）、content（文件内容）。\n"
    "2. run_command：参数 command（要执行的命令）。\n"
    "3. read_file：参数 path（文件路径）。\n"
    "工具调用必须严格使用以下 XML 格式，一次只能有一个工具调用：\n"
    "<tool_call><name>write_file</name><arg_value>path 的值</arg_value><arg_value>content 的值</arg_value></tool_call>\n"
    "收到工具结果后继续下一步。任务完成后输出最终总结。"
)
print("\n# B. 工具调用（默认裸 XML 协议）")
r1 = chat([{"role": "system", "content": sys_protocol},
           {"role": "user", "content": "写一个 Python 计算器方法并运行验证"}], num_predict=500)
c1 = r1.get("message", {}).get("content", "")
m1 = re.search(r"<tool_call>.*?</tool_call>", c1, re.S)
if m1:
    xml1 = m1.group(0)
    name1 = re.search(r"<name>(.*?)</name>", xml1, re.S)
    print(f"[XML 命中] name={name1.group(1) if name1 else None!r} 长度={len(xml1)}")
    print("[XML 前 300]", xml1[:300])
    print("[XML 尾 150]", xml1[-150:])
else:
    print("[XML 未命中] content 前 300:", c1[:300])

# ── B2 工具调用：CDATA 协议变体 ──
sys_cdata = (
    "你是运行在 Agent 工作台里的编码助手。工具：write_file(path, content)、run_command(command)。\n"
    "工具调用格式（严格 XML，一次一个）：\n"
    "<tool_call><name>write_file</name><arg_value>path</arg_value><arg_value><![CDATA[文件内容]]></arg_value></tool_call>\n"
    "注意：文件内容必须放在 CDATA 里，不允许裸换行或特殊字符直接出现。\n"
    "收到工具结果后继续。任务完成输出总结。"
)
print("\n# B2. 工具调用（CDATA 协议变体）")
r2 = chat([{"role": "system", "content": sys_cdata},
           {"role": "user", "content": "写一个 Python 计算器方法并运行验证"}], num_predict=500)
c2 = r2.get("message", {}).get("content", "")
m2 = re.search(r"<tool_call>.*?</tool_call>", c2, re.S)
if m2:
    xml2 = m2.group(0)
    name2 = re.search(r"<name>(.*?)</name>", xml2, re.S)
    print(f"[XML 命中] name={name2.group(1) if name2 else None!r} 含CDATA={'<![CDATA[' in xml2}")
    print("[XML 前 300]", xml2[:300])
    print("[XML 尾 100]", xml2[-100:])
else:
    print("[XML 未命中] content 前 300:", c2[:300])

# ── 多轮工具链闭环（CDATA 协议）──
print("\n# 多轮工具链：写 → 跑 → 收尾")
msgs = [{"role": "system", "content": sys_cdata},
        {"role": "user", "content": "写一个 Python 计算器方法并运行验证"}]
def extract(xml):
    name = re.search(r"<name>(.*?)</name>", xml, re.S)
    return name.group(1) if name else None
for turn in range(4):
    rr = chat(msgs)
    cc = rr.get("message", {}).get("content", "")
    mm = re.search(r"<tool_call>.*?</tool_call>", cc, re.S)
    if not mm:
        print(f"[第{turn+1}轮] 无工具调用（收尾）→ content 前 200: {cc[:200]!r}")
        break
    nm = extract(mm.group(0))
    print(f"[第{turn+1}轮] 调用 {nm}")
    msgs.append({"role": "assistant", "content": cc})
    if nm == "write_file":
        msgs.append({"role": "user", "content": "<tool_result>write_file: 已写入，共 1500 字符。\n下一步请运行验证。</tool_result>"})
    elif nm == "run_command":
        msgs.append({"role": "user", "content": "<tool_result>run_command: 退出码 0，程序输出正常。\n任务已完成，请给出总结。</tool_result>"})
    else:
        msgs.append({"role": "user", "content": "<tool_result>read_file: 文件内容（略）</tool_result>"})

# ── C 规划 JSON ──
print("\n# C. 规划 JSON（多文件任务）")
show("C", chat([
    {"role": "system", "content": "你是任务规划器。把用户需求拆成文件清单，只输出 JSON，格式：{\"summary\":\"...\",\"files\":[{\"path\":\"...\",\"purpose\":\"...\",\"requirements\":\"...\"}]}，不要输出其他文字。"},
    {"role": "user", "content": "设计一个学生管理系统，C# 多文件：Student 模型、StudentService、Program。"},
]))

# ── D 中文长任务（限输出防超时）──
print("\n# D. 中文指令跟随（天气免 key）")
try:
    show("D", chat([
        {"role": "system", "content": "你是中文编码助手。用中文回答，输出简洁。"},
        {"role": "user", "content": "用 Python 写一个根据 IP 地址获取天气的方法（用免费免 key 的接口），给出完整代码和中文注释。"},
    ], num_predict=400, timeout=180), n=500)
except Exception as e:
    print("[D 超时/失败]", type(e).__name__, str(e)[:120])

print("\n=== 9B 测试完成 ===")
