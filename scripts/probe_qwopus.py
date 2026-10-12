# -*- coding: utf-8 -*-
"""Qwopus3.5-4B 能力边界测试（不动项目，纯 ollama API）"""
import json, time, urllib.request

MODEL = "SetneufPT/Qwopus3.5-4B-Coder-MTP_Q4_64k_8GB-GPU"
BASE = "http://127.0.0.1:11434"

def chat(messages, tools=None, temperature=0.3, num_ctx=32768, timeout=300):
    body = {"model": MODEL, "messages": messages, "stream": False,
            "options": {"temperature": temperature, "num_ctx": num_ctx}}
    if tools is not None:
        body["tools"] = tools
    req = urllib.request.Request(BASE + "/api/chat", data=json.dumps(body).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8"))

def show(label, resp):
    msg = resp.get("message", {})
    content = msg.get("content", "")
    tc = msg.get("tool_calls")
    print(f"\n=== {label} ===")
    print(f"[tool_calls] {json.dumps(tc, ensure_ascii=False) if tc else '无'}")
    print(f"[content] {content[:600]}")
    if resp.get("error"):
        print(f"[ERROR] {resp['error']}")
    return content, tc

# ── 测试 A：直接代码生成（无工具）──
print("# A. C# 奇数偶数 直接生成（模拟 Small 单文件写）")
show("A", chat([
    {"role": "system", "content": "你是一个代码助手，直接输出完整可编译的代码。"},
    {"role": "user", "content": "用 C# 写一个判断奇数偶数的函数，含 Main 测试正数/负数/零。"},
]))

# ── 测试 B：工具调用格式（模拟内核 XML 协议）──
print("\n# B. 工具调用格式（内核核心依赖：必须输出合法 XML 工具调用）")
sys_protocol = (
    "你是运行在 Agent 工作台里的编码助手。你可以调用以下工具：\n"
    "1. write_file：参数 path（文件路径）、content（文件内容）。\n"
    "2. run_command：参数 command（要执行的命令）。\n"
    "3. read_file：参数 path（文件路径）。\n"
    "工具调用必须严格使用以下 XML 格式，一次只能有一个工具调用：\n"
    "<tool_call><name>write_file</name><arg_value>path 的值</arg_value><arg_value>content 的值</arg_value></tool_call>\n"
    "收到工具结果后继续下一步。任务完成后输出最终总结。"
)
b1, b1tc = show("B1 工具调用第一步", chat([
    {"role": "system", "content": sys_protocol},
    {"role": "user", "content": "写一个 Python 计算器方法并运行验证"},
]))

# 若 B1 输出工具调用，喂回结果看连续多轮能力
if b1tc:
    msgs = [
        {"role": "system", "content": sys_protocol},
        {"role": "user", "content": "写一个 Python 计算器方法并运行验证"},
    ]
    msgs.append({"role": "assistant", "content": b1})
    # 模拟工具结果：write_file 成功
    msgs.append({"role": "user", "content": "<tool_result>write_file: 已写入 calculator.py</tool_result>"})
    b2, b2tc = show("B2 工具结果后（应继续调用或收尾）", chat(msgs))
else:
    print("B1 未输出工具调用——工具链不稳信号")

# ── 测试 C：规划 JSON 完整性（模拟 Planner）──
print("\n# C. 规划 JSON（多文件任务：必须输出完整可解析 JSON）")
c, _ = show("C", chat([
    {"role": "system", "content": "你是任务规划器。把用户需求拆成文件清单，只输出 JSON，格式：{\"summary\":\"...\",\"files\":[{\"path\":\"...\",\"purpose\":\"...\",\"requirements\":\"...\"}]}，不要输出其他文字。"},
    {"role": "user", "content": "设计一个学生管理系统，C# 多文件：Student 模型、StudentService、Program。"},
]))

# ── 测试 D：中文长上下文 + 指令跟随 ──
print("\n# D. 中文指令跟随（模拟复杂提示）")
show("D", chat([
    {"role": "system", "content": "你是中文编码助手。用中文回答，输出简洁。"},
    {"role": "user", "content": "用 Python 写一个根据 IP 地址获取天气的方法（用免费免 key 的接口），给出完整代码和中文注释。"},
]))

print("\n=== 测试完成 ===")
