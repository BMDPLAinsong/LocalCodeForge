#!/usr/bin/env python3
# ============================================================
# jscheck_selftest.py -- regression tests for jscheck.py.
#
# Why: jscheck.py is a GATE (verify.ps1 step 3 fallback). A gate
# that silently rots is worse than no gate -- during its own
# development it had two real bugs (template ${...} closing brace
# stealing an outer-scope bracket; wrong depth bookkeeping) that
# false-positived on the healthy dashboard. These cases pin the
# behaviour:
#   1. real index.html               -> OK
#   2. unescaped quote in a string   -> FAIL (the 10-16 shipped bug)
#   3. unbalanced opening brace      -> FAIL
#   4. unterminated template literal -> FAIL
#   5. stray closing paren           -> FAIL
#
# Usage:  python jscheck_selftest.py [repo_root]
# Exit 0 = all pass; 1 = regression. Pure ASCII output.
# ============================================================
import io
import os
import subprocess
import sys
import tempfile

ROOT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "..")
HTML_PATH = os.path.join(ROOT, "src", "Agent.Dashboard", "DashboardPages", "index.html")
CHK = os.path.join(ROOT, "scripts", "jscheck.py")


def run(path):
    r = subprocess.run([sys.executable, CHK, path], capture_output=True, text=True)
    return r.returncode, (r.stdout + r.stderr).strip()


def main():
    if not os.path.exists(HTML_PATH):
        print("[SKIP] index.html not found")
        return 0
    html = io.open(HTML_PATH, encoding="utf-8").read()
    tmp = tempfile.mkdtemp()
    failures = []

    # 1) real file must pass
    code, out = run(HTML_PATH)
    print("REAL-FILE:", "OK" if code == 0 else "FAIL")
    if code != 0:
        failures.append("real file flagged: " + out.splitlines()[-1])

    # broken variants: (name, anchor, replacement)
    cases = [
        ("quote", 'emptyState("\u5f53\u524d\u8fc7\u6ee4\u4e0b\u65e0\u5339\u914d\u4e8b\u4ef6"',
                  'emptyState("\u5f53\u524d\u8fc7\u6ee4\u4e0b\u65e0\u5339\"\u914d\u4e8b\u4ef6"',
                 "unescaped quote inside string"),
        ("brace", "function segBar(parts, opts) {", "function segBar(parts, opts) {{",
                 "unbalanced opening brace"),
        ("tpl", 'label: "\u6210\u529f" },', 'label: "\u6210\u529f },',
               "unterminated template literal"),
        ("paren", "const fmtMs = (ms) =>", "const fmtMs = (ms) =>)",
                  "stray closing paren"),
    ]
    for name, old, new, desc in cases:
        if old not in html:
            failures.append("anchor missing for case %s (file drifted?)" % name)
            print("ANCHOR-%s: MISSING" % name.upper())
            continue
        p = os.path.join(tmp, "b_%s.html" % name)
        io.open(p, "w", encoding="utf-8").write(html.replace(old, new, 1))
        code, out = run(p)
        print("%s-BUG:" % name.upper(), "OK" if code == 1 else "FAIL", "(%s)" % desc)
        if code != 1:
            failures.append("case %s not detected" % name)

    if failures:
        for f in failures:
            print("[FAIL]", f)
        print("JSCHECK SELFTEST FAILED")
        return 1
    print("JSCHECK SELFTEST OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
