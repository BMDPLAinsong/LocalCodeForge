# -*- coding: utf-8 -*-
import io, os

# 仓库根 = 本脚本所在目录（不硬编码本地路径，clone 到任意位置可跑）
ROOT = os.path.dirname(os.path.abspath(__file__))

t = open(os.path.join(ROOT, 'e2e_diag.txt'), 'rb').read().decode('utf-8', errors='replace')
out = io.open(os.path.join(ROOT, 'e2e_summary.txt'), 'w', encoding='utf-8')

# 1. 根因1：use 工程根应为 X:\（非 bin\Debug 目录）
proj_line = [l for l in t.splitlines() if '[工程]' in l]
out.write('工程行: %s\n' % (' | '.join(proj_line) if proj_line else '无'))
use_ok = any('[工程] X:\\' in l or ('[工程]' in l and 'bin' not in l and 'X:\\' in l) for l in t.splitlines())
out.write('根因1(use路径=X:\\): %s\n' % use_ok)

# 2. 指代继承触发
ref_line = [l for l in t.splitlines() if '[会话写目标]' in l]
out.write('指代继承: %s\n' % (' | '.join(ref_line) if ref_line else '无'))

# 3. 目标文件是否真实写入 + 内容
target = r'X:\_law_e2e_test.txt'
if os.path.exists(target):
    content = open(target, 'r', encoding='utf-8', errors='replace').read()
    out.write('目标文件: 存在, 内容=[%s]\n' % content.strip())
    out.write('内容正确(含hello-e2e): %s\n' % ('hello-e2e-20261008' in content))
else:
    out.write('目标文件: 不存在\n')

# 4. 任务目录是否有同名残留（写错地方）
import glob
resid = glob.glob(os.path.join(ROOT, 'workspace', 'runs', '*', '*_law_e2e_test.txt*'))
out.write('任务目录残留: %d 个\n' % len(resid))

# 5. 第二轮写 X:\_law_e2e_test.txt 是否被路径越界拒绝
rejected = [l for l in t.splitlines() if '_law_e2e_test.txt' in l and '越界' in l]
out.write('目标写被拒: %s\n' % ('有' if rejected else '无'))

out.write('--- 关键行 ---\n')
for l in t.splitlines():
    if '[工程]' in l or '[会话写目标]' in l or '[显式授权]' in l or '[任务汇总]' in l or '路径越界' in l:
        out.write(l + '\n')
out.close()
