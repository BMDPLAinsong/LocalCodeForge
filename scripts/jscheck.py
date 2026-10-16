#!/usr/bin/env python3
# ============================================================
# jscheck.py -- JS syntax gate, pure-python fallback channel.
#
# Why: verify.ps1 step 3 used `node --check` only. On machines
# without node the gate silently skipped -- exactly how the
# 10-16 dashboard bug (unescaped quote inside a string literal
# killed the whole inline <script>) shipped unnoticed.
#
# What it checks (a real character-level state machine, not a
# grep heuristic):
#   * every inline <script> block in the target html (or a raw
#     .js file)
#   * balanced () [] {} across code (strings / comments / regex
#     / template literals are skipped as atomic tokens)
#   * all string / template / regex literals terminated
#   * division `/` vs regex-literal `/` disambiguation via the
#     previous significant token (keywords like return/typeof
#     allow a regex; identifiers/numbers/`)` mean division)
#
# Deliberately NOT checked (out of scope for a gate): full ES
# grammar (ASI, reserved words, scope). It catches the class of
# bug that actually shipped: unterminated / unbalanced literals.
#
# Usage:  python jscheck.py <file.html|file.js> [more files...]
# Exit 0 = all pass; 1 = at least one violation (message + line).
# Pure ASCII output (PowerShell 5.1 pipe safe).
# ============================================================
import io
import re
import sys

# tokens after which a `/` starts a regex literal, not division
REGEX_PRECEDE_KEYWORDS = {
    "return", "typeof", "instanceof", "in", "of", "new", "delete",
    "void", "case", "do", "else", "yield", "await", "throw",
}
IDENT_CHARS = re.compile(r"[A-Za-z0-9_$]")


class JsSyntaxError(Exception):
    def __init__(self, msg, line, col):
        super().__init__(msg)
        self.msg = msg
        self.line = line
        self.col = col


def check_js(src):
    """State machine over one JS source. Raises JsSyntaxError."""
    i, n = 0, len(src)
    line, col = 1, 1
    stack = []          # (char, line, col) for ( [ {
    last_sig = ""       # last significant char in code mode
    last_word = ""      # last identifier-like token in code mode
    # modes: "code" | "line-comment" | "block-comment" | "sq" | "dq"
    #        | "tpl" | "regex"   ("code" pushed again for ${ ... })
    modes = ["code"]
    # parallel to tpl-expr code pushes: stack depth at ${ so the
    # matching } (and only it) returns to template mode
    expr_depths = []

    def advance(k=1):
        nonlocal i, line, col
        for _ in range(k):
            if i < n and src[i] == "\n":
                line += 1
                col = 1
            else:
                col += 1
            i += 1

    while i < n:
        mode = modes[-1]
        c = src[i]

        if mode == "line-comment":
            if c == "\n":
                modes.pop()
            advance()
            continue

        if mode == "block-comment":
            if c == "*" and i + 1 < n and src[i + 1] == "/":
                advance(2)
                modes.pop()
            else:
                advance()
            continue

        if mode in ("sq", "dq"):
            if c == "\\":                      # escape: skip next char
                advance(2)
                continue
            if (mode == "sq" and c == "'") or (mode == "dq" and c == '"'):
                advance()
                modes.pop()
                last_sig = '"'
                last_word = ""
            else:
                advance()
            continue

        if mode == "regex":
            if c == "\\":
                advance(2)
                continue
            if c == "[":
                stack.append(("[", line, col))
                advance()
                continue
            if c == "]":
                if stack and stack[-1][0] == "[":
                    stack.pop()
                advance()
                continue
            if c == "/" and not any(e[0] == "[" for e in stack):
                advance()
                while i < n and IDENT_CHARS.match(src[i]):   # flags g/i/m/s/u/y
                    advance()
                modes.pop()
                last_sig = "/"
                last_word = ""
                continue
            if c == "\n":                      # regex cannot span lines
                raise JsSyntaxError("unterminated regex literal (newline)", line, col)
            advance()
            continue

        if mode == "tpl":
            if c == "\\":
                advance(2)
                continue
            if c == "`":
                advance()
                modes.pop()
                last_sig = "`"
                last_word = ""
                continue
            if c == "$" and i + 1 < n and src[i + 1] == "{":
                advance(2)
                modes.append("code")
                # the ${ opener IS a bracket: push it so the matching }
                # pops exactly this one (without it, every template
                # expression's closing } steals an outer-scope bracket).
                # Record the depth BEFORE the push: while the expression
                # is open the stack holds this brace, so any inner block's
                # } leaves depth >= recorded+1 and cannot false-trigger.
                expr_depths.append(len(stack))
                stack.append(("{", line, col))
                last_sig = "{"
                last_word = ""
                continue
            advance()
            continue

        # ---- code mode ----
        if c == "/" and i + 1 < n and src[i + 1] == "/":
            modes.append("line-comment")
            advance(2)
            continue
        if c == "/" and i + 1 < n and src[i + 1] == "*":
            modes.append("block-comment")
            advance(2)
            continue
        if c == "'":
            modes.append("sq")
            advance()
            continue
        if c == '"':
            modes.append("dq")
            advance()
            continue
        if c == "`":
            modes.append("tpl")
            advance()
            continue
        if c == "/":
            regex_ok = (
                last_sig in ("", "(", "[", "{", ",", ";", "=", ":", "!", "&",
                             "|", "?", "+", "-", "*", "%", "^", "~", "<", ">")
                or last_word in REGEX_PRECEDE_KEYWORDS
            )
            if regex_ok:
                modes.append("regex")
                advance()
                continue
            advance()                         # division operator
            last_sig = "/"
            last_word = ""
            continue
        if c in "([{":
            stack.append((c, line, col))
            advance()
            last_sig = c
            last_word = ""
            continue
        if c in ")]}":
            want = {")": "(", "]": "[", "}": "{"}[c]
            if not stack:
                raise JsSyntaxError("unmatched closing '%s'" % c, line, col)
            top = stack.pop()
            if top[0] != want:
                raise JsSyntaxError(
                    "closing '%s' does not match opening '%s' (line %d col %d)"
                    % (c, top[0], top[1], top[2]), line, col)
            # } that closes a template ${ ... } returns to tpl mode
            # (only when the popped brace IS the ${ opener: stack is
            #  back at the depth recorded when the expression started)
            if (c == "}" and expr_depths
                    and expr_depths[-1] == len(stack)):
                expr_depths.pop()
                modes.pop()
            advance()
            last_sig = c
            last_word = ""
            continue
        if IDENT_CHARS.match(c):
            j = i
            while j < n and IDENT_CHARS.match(src[j]):
                j += 1
            last_word = src[i:j]
            advance(j - i)
            last_sig = "w"
            continue
        if not c.isspace():
            last_sig = c
            last_word = ""
        advance()

    if modes[-1] != "code":
        raise JsSyntaxError("unterminated %s at end of input" % modes[-1], line, col)
    if stack:
        top = stack[-1]
        raise JsSyntaxError(
            "unclosed '%s' (opened line %d col %d)" % (top[0], top[1], top[2]),
            top[1], top[2])


def extract_scripts(html):
    """All inline <script>...</script> bodies (no src= external)."""
    return [m.group(1) for m in
            re.finditer(r"<script(?![^>]*\bsrc=)[^>]*>(.*?)</script>", html, re.S | re.I)]


def check_file(path):
    with io.open(path, encoding="utf-8") as f:
        raw = f.read()
    if path.lower().endswith(".js"):
        targets = [("js", raw)]
    else:
        blocks = extract_scripts(raw)
        if not blocks:
            print("[SKIP] %s: no inline <script> blocks" % path)
            return True
        targets = [("script#%d" % (k + 1), b) for k, b in enumerate(blocks)]
    ok = True
    for name, src in targets:
        try:
            check_js(src)
            print("[OK] %s (%s): %d chars" % (path, name, len(src)))
        except JsSyntaxError as e:
            ok = False
            print("[FAIL] %s (%s): line %d col %d: %s"
                  % (path, name, e.line, e.col, e.msg))
    return ok


def main(argv):
    if len(argv) < 2:
        print("usage: jscheck.py <file.html|file.js> [...]")
        return 2
    ok = True
    for p in argv[1:]:
        try:
            ok = check_file(p) and ok
        except IOError as e:
            print("[FAIL] cannot read %s: %s" % (p, e))
            ok = False
    print("JS SYNTAX %s" % ("OK" if ok else "FAILED"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
