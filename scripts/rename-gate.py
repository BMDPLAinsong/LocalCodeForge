# rename-gate.py -- fail the gate if the legacy name "LocalAiWorkbench" leaks
# outside the whitelist, so the rename LocalAiWorkbench -> LocalCodeForge cannot
# silently regress (anti-whack-a-mole).
#
# Whitelist (kept on purpose):
#   1. Lines that also reference "X:" -- real on-disk paths such as the test
#      fixture path "X:\.NET AI\LocalAiWorkbench" (the folder is intentionally
#      NOT renamed) and scripts/patch_handover_b8.py which points at that folder.
#   2. docs/HANDOVER.md entirely -- its historical batch entries record the old
#      name as history and must not be rewritten.
#
# Usage: python scripts/rename-gate.py <repo-root>
import os
import sys

ROOT = sys.argv[1]
SKIP_DIRS = {".git", "bin", "obj", "workspace"}
EXTS = (".cs", ".ps1", ".md", ".py", ".slnx", ".sh")

bad = []
for dp, dns, fns in os.walk(ROOT):
    dns[:] = [d for d in dns if d not in SKIP_DIRS]
    for fn in fns:
        if not fn.endswith(EXTS):
            continue
        p = os.path.join(dp, fn)
        if "HANDOVER.md" in p or fn == "rename-gate.py":
            continue
        try:
            s = open(p, "rb").read().decode("utf-8", "replace")
        except Exception:
            continue
        for i, line in enumerate(s.splitlines(), 1):
            if "LocalAiWorkbench" in line and "X:" not in line:
                bad.append(os.path.relpath(p, ROOT) + ":" + str(i))

if bad:
    print("RENAME-GATE LEAK (legacy name outside whitelist):")
    print("\n".join(bad[:20]))
    sys.exit(1)
print("RENAME-GATE OK")
