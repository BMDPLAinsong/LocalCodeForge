# rename-gate.py -- fail the gate if the legacy name "LocalAiWorkbench" leaks
# outside the whitelist, so the rename LocalAiWorkbench -> LocalCodeForge cannot
# silently regress (anti-whack-a-mole).
#
# Whitelist (kept on purpose):
#   docs/HANDOVER.md -- the 7p migration record states the rename itself
#   ("GitHub 改名 LocalAiWorkbench -> LocalCodeForge") as history and must
#   keep the legacy name.
#   docs/history/ -- the archived full batch log (10-16 batch 4) quotes the
#   same migration records; the archive is history by design.
#
# Usage: python scripts/rename-gate.py <repo-root>
import os
import sys

ROOT = sys.argv[1]
SKIP_DIRS = {".git", "bin", "obj", "workspace"}
EXTS = (".cs", ".ps1", ".md", ".py", ".slnx", ".sh")
HIST_DIR = os.path.join("docs", "history") + os.sep

bad = []
for dp, dns, fns in os.walk(ROOT):
    dns[:] = [d for d in dns if d not in SKIP_DIRS]
    for fn in fns:
        if not fn.endswith(EXTS):
            continue
        p = os.path.join(dp, fn)
        rel = os.path.relpath(p, ROOT)
        if "HANDOVER.md" in p or fn == "rename-gate.py" or HIST_DIR in rel + os.sep:
            continue
        try:
            s = open(p, "rb").read().decode("utf-8", "replace")
        except Exception:
            continue
        for i, line in enumerate(s.splitlines(), 1):
            if "LocalAiWorkbench" in line:
                bad.append(os.path.relpath(p, ROOT) + ":" + str(i))

if bad:
    print("RENAME-GATE LEAK (legacy name outside whitelist):")
    print("\n".join(bad[:20]))
    sys.exit(1)
print("RENAME-GATE OK")
