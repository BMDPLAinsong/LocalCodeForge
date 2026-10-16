#!/usr/bin/env python3
# ============================================================
# inline_dashboard.py -- dashboard single-page source split.
#
# Why: DashboardPages/index.html grew to ~1700 lines (CSS + DOM +
# JS all in one file, four visual redesigns patched by script).
# The runtime contract stays "one embedded single page" (csproj
# embeds index.html; GET / returns it; tests assert on it), so we
# split the SOURCE without changing the artifact:
#
#   DashboardPages/app.css    <style> content   (edit here)
#   DashboardPages/app.js     <script> content  (edit here)
#   DashboardPages/index.html generated: skeleton + inlined css/js
#
# Modes:
#   --extract   one-time: index.html -> app.css / app.js
#   (default)   build:    app.css / app.js -> index.html
#   --check     gate:     build in memory, compare with disk;
#                        exit 1 on drift (commit blocked)
#
# Workflow: edit app.css/app.js, run this script (or let verify.ps1
# catch the drift), commit both sources and the regenerated page.
# ============================================================
import io
import os
import re
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
PAGE = os.path.join(ROOT, "src", "Agent.Dashboard", "DashboardPages", "index.html")
CSS = os.path.join(ROOT, "src", "Agent.Dashboard", "DashboardPages", "app.css")
JS = os.path.join(ROOT, "src", "Agent.Dashboard", "DashboardPages", "app.js")

STYLE_RE = re.compile(r"(<style>)(.*?)(</style>)", re.S)
SCRIPT_RE = re.compile(r"(<script>)(.*?)(</script>)", re.S)


def read(path):
    with io.open(path, encoding="utf-8") as f:
        return f.read()


def write(path, text):
    with io.open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def render(html, css, js):
    """Inline css/js into the page skeleton. Raises on shape drift."""
    out, n1 = STYLE_RE.subn(lambda m: m.group(1) + css + m.group(3), html, count=1)
    if n1 != 1:
        raise SystemExit("index.html: expected exactly one <style> block")
    out, n2 = SCRIPT_RE.subn(lambda m: m.group(1) + js + m.group(3), out, count=1)
    if n2 != 1:
        raise SystemExit("index.html: expected exactly one <script> block")
    return out


def extract():
    html = read(PAGE)
    m = STYLE_RE.search(html)
    if not m:
        raise SystemExit("index.html: no <style> block found")
    write(CSS, m.group(2))
    m = SCRIPT_RE.search(html)
    if not m:
        raise SystemExit("index.html: no <script> block found")
    write(JS, m.group(2))
    print("[extract] app.css %d chars, app.js %d chars" % (len(read(CSS)), len(read(JS))))


def build():
    html = read(PAGE)
    out = render(html, read(CSS), read(JS))
    if out == html:
        print("[build] index.html already in sync (%d chars)" % len(html))
    else:
        write(PAGE, out)
        print("[build] index.html regenerated (%d chars)" % len(out))
    return 0


def check():
    html = read(PAGE)
    out = render(html, read(CSS), read(JS))
    if out == html:
        print("[check] dashboard sources in sync")
        return 0
    # pinpoint which side drifted (first differing line)
    a, b = html.split("\n"), out.split("\n")
    for i in range(max(len(a), len(b))):
        x = a[i] if i < len(a) else "<EOF>"
        y = b[i] if i < len(b) else "<EOF>"
        if x != y:
            print("[check] DRIFT at line %d:" % (i + 1))
            print("  index.html: %s" % x[:100])
            print("  sources   : %s" % y[:100])
            break
    print("[check] DRIFT: run `python scripts/inline_dashboard.py` "
          "(sources app.css/app.js -> index.html)")
    return 1


def main(argv):
    mode = argv[1] if len(argv) > 1 else ""
    if mode == "--extract":
        extract()
        return 0
    if mode == "--check":
        return check()
    if mode in ("", "--build"):
        return build()
    print("usage: inline_dashboard.py [--extract|--build|--check]")
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
