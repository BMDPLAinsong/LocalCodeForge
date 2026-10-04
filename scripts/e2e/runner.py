# -*- coding: utf-8 -*-
"""E2E CLI runner：用 python subprocess 启动 CLI 并喂 stdin（确定性，绕开 PS 5.1
   -File 模式向子进程 stdin 注入 UTF-8 BOM 的 bug——BOM 会让 CLI 的 use 命令不命中）。
用法: python runner.py <cli_exe> <cmd_base64> <out_log_path>
"""
import subprocess
import sys
import base64

exe = sys.argv[1]
cmd = base64.b64decode(sys.argv[2]).decode("utf-8")
out_path = sys.argv[3]

p = subprocess.run([exe], input=cmd.encode("utf-8"), capture_output=True)
data = p.stdout + b"\n--- stderr ---\n" + p.stderr
try:
    text = data.decode("utf-8")
except UnicodeDecodeError:
    text = data.decode("gbk", errors="replace")
with open(out_path, "w", encoding="utf-8", newline="") as f:
    f.write(text)
sys.exit(0 if p.returncode == 0 else 1)
