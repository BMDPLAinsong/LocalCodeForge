# -*- coding: utf-8 -*-
"""Qwopus3.5-4B 工具协议适配测试：CDATA 变体"""
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

# CDATA 协议变体：content 必须用 <![CDATA[ ]]> 包裹（严格 XML 安全）
sys_cdata = (
    "你是运行在 Agent 工作台里的编码助手。工具：write_file(path, content)、run_command(command)。\n"
    "工具调用格式（严格 XML，一次一个）：\n"
    "<tool_call><name>write_file</name><arg_value>path</arg_value><arg_value><![CDATA[文件内容]]></arg_value></tool_call>\n"
    "注意：文件内容必须放在 CDATA 里，不允许裸换行或特殊字符直接出现。\n"
    "收到工具结果后继续。任务完成输出总结。"
)
print("# 协议变体：content 用 CDATA 包裹")
r = chat([
    {"role": "system", "content": sys_cdata},
    {"role": "user", "content": "写一个 Python 计算器方法并运行验证"},
])
c = r.get("message", {}).get("content", "")
m = re.search(r"<tool_call>.*?</tool_call>", c, re.S)
if m:
    xml = m.group(0)
    print("[XML 命中] 长度", len(xml))
    has_cdata = "<![CDATA[" in xml
    print("[含 CDATA]", has_cdata)
    print("[XML 前 350]", xml[:350])
    print("[XML 尾 150]", xml[-150:])
    # 检查裸 < 出现在 arg_value 文本（CDATA 外）
    name = re.search(r"<name>(.*?)</name>", xml, re.S)
    print(f"[name] {name.group(1) if name else None!r}")
else:
    print("[XML 未命中] content 前 400:", c[:400])
    print("——模型未遵守 CDATA 协议，或未输出工具调用")

print("\n=== CDATA 测试完成 ===")
