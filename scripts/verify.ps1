# ============================================================
# verify.ps1 -- one-command gate for LocalCodeForge
#   1. build       (exit code must be 0)
#   2. tests       (exit code must be 0; harness: 0=all pass, 1=has failure)
#   3. js-syntax   (embedded index.html <script>: node --check, or
#                   python fallback scripts/jscheck.py when node absent)
#   6. dashboard-inline (app.css/app.js sources vs generated index.html)
#   4. drift-check (exit code must be 0)
# Exit 0 = all green. Non-zero = gate failed (CI / git hook friendly).
# Usage:  powershell -NoProfile -File scripts\verify.ps1
#
# NOTE: this file is intentionally pure ASCII. PowerShell 5.1 reads a
# BOM-less UTF-8 script as ANSI and garbles non-ASCII (breaking quote
# pairing) -- see drift-check.ps1 for the same workaround. The two
# Chinese tokens used for DISPLAY are built from code points.
# ============================================================
$ErrorActionPreference = "Continue"

$RepoRoot  = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$sln       = Join-Path $RepoRoot "LocalCodeForge.slnx"
$testProj  = Join-Path $RepoRoot "tests\Agent.Infrastructure.Tests\Agent.Infrastructure.Tests.csproj"
$driftPath = Join-Path $PSScriptRoot "drift-check.ps1"

# Chinese tokens built from code points (encoding-safe): 结果 / 失败
$RESULT_WORD = "$([char]0x7ED3)$([char]0x679C)"
$FAIL_WORD   = "$([char]0x5931)$([char]0x8D25)"

$failed = @()

function Step($n) { Write-Host ""; Write-Host "=== $n ===" -ForegroundColor Cyan }
function Ok($m)   { Write-Host $m -ForegroundColor Green }
function Bad($m)  { Write-Host $m -ForegroundColor Red }

# ---------------- 1/3 build ----------------
Step "1/6 BUILD (expect exit 0)"
& dotnet build $sln --no-incremental -v q 2>&1 | Select-Object -Last 5 | ForEach-Object { Write-Host $_ }
$buildOk = ($LASTEXITCODE -eq 0)
if ($buildOk) { Ok "BUILD OK" } else { Bad "BUILD FAILED (exit $LASTEXITCODE)"; $failed += "build" }

# ---------------- 2/3 tests ----------------
Step "2/6 TESTS (expect exit 0)"
if ($buildOk) {
    $testOut  = & dotnet run --project $testProj 2>&1 | Out-String
    $testCode = $LASTEXITCODE
    $last = ($testOut -split "`n" | Where-Object { $_.Contains($RESULT_WORD) -or $_.Contains($FAIL_WORD) } | Select-Object -Last 1)
    if ($last) { Write-Host $last.Trim() }
    $testsOk = ($testCode -eq 0)
    if ($testsOk) { Ok "TESTS OK" } else { Bad "TESTS FAILED (exit $testCode)"; $failed += "tests" }
} else {
    Write-Host "TESTS SKIPPED (build failed)" -ForegroundColor Yellow
    $failed += "tests(skipped)"
}

# ---------------- 3/4 JS syntax gate ----------------
# 10-15: embedded index.html <script> must pass node --check. The old gate
# only checked HTML strings, so a broken JS syntax (unbalanced quote) shipped
# and killed the whole dashboard script silently.
# 10-16: node is NOT always installed (this dev box has none) -- the gate
# silently skipped, i.e. was blind exactly where the 10-16 bug class lives.
# Now: node present -> node --check (authoritative). node absent ->
# scripts/jscheck.py (pure-python state machine: balanced brackets,
# terminated string/template/regex literals, div-vs-regex disambiguation).
# The gate NEVER silently skips while the target file exists.
Step "3/6 JS SYNTAX (index.html <script>: node --check, python fallback)"
$htmlPath = Join-Path $RepoRoot "src\Agent.Dashboard\DashboardPages\index.html"
$tmpJs    = Join-Path $env:TEMP ("lcf_jscheck_" + [guid]::NewGuid().ToString("N") + ".js")
$pyGate   = Join-Path $PSScriptRoot "jscheck.py"
$pySelf   = Join-Path $PSScriptRoot "jscheck_selftest.py"
$nodeExe  = Get-Command node -ErrorAction SilentlyContinue
# 3a: the fallback checker itself must not rot (it had two real bugs
# during development: template ${} brace stealing, depth bookkeeping)
if (Test-Path $pySelf) {
    & python $pySelf $RepoRoot 2>&1 | Select-Object -Last 2 | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { Bad "JSCHECK SELFTEST FAILED (python fallback rotted)"; $failed += "js-selftest" }
}
if (-not (Test-Path $htmlPath)) {
    Write-Host "index.html not found -- JS gate skipped" -ForegroundColor Yellow
} elseif ($nodeExe) {
    $pyCode = "import re,io,sys; s=io.open(sys.argv[1],encoding='utf-8').read(); m=re.findall(r'<script>(.*?)</script>', s, re.S); io.open(sys.argv[2],'w',encoding='utf-8').write(m[0] if m else '')"
    & python -c $pyCode $htmlPath $tmpJs 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Bad "JS EXTRACT FAILED (python)"; $failed += "js-syntax"
    } else {
        & node --check $tmpJs 2>&1 | Select-Object -First 5 | ForEach-Object { Write-Host $_ }
        if ($LASTEXITCODE -eq 0) { Ok "JS SYNTAX OK (node)" } else { Bad "JS SYNTAX FAILED (node, exit $LASTEXITCODE)"; $failed += "js-syntax" }
    }
    Remove-Item $tmpJs -ErrorAction SilentlyContinue
} elseif (Test-Path $pyGate) {
    & python $pyGate $htmlPath 2>&1 | Select-Object -First 8 | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -eq 0) { Ok "JS SYNTAX OK (python fallback: jscheck.py)" } else { Bad "JS SYNTAX FAILED (python fallback, exit $LASTEXITCODE)"; $failed += "js-syntax" }
} else {
    Bad "node not found AND jscheck.py missing -- JS gate BLIND"; $failed += "js-syntax"
}

# ---------------- 4/6 drift gate ----------------
Step "4/6 DRIFT CHECK (expect exit 0)"
if (Test-Path $driftPath) {
    & powershell -NoProfile -File $driftPath 2>&1 | Select-Object -Last 3 | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -eq 0) { Ok "DRIFT OK" } else { Bad "DRIFT CHECK FAILED (exit $LASTEXITCODE)"; $failed += "drift-check" }
} else {
    Write-Host "drift-check.ps1 not found -- skipped" -ForegroundColor Yellow
}

# ---------------- 5/6 rename gate ----------------
# 10-16: renamed to LocalCodeForge. Fails the gate when the legacy project
# name leaks outside the whitelist (real on-disk paths with "X:" and
# docs/HANDOVER.md history). Anti-whack-a-mole: no silent regression.
Step "5/6 RENAME GATE (no legacy name outside whitelist)"
$renameGate = Join-Path $PSScriptRoot "rename-gate.py"
if (Test-Path $renameGate) {
    & python $renameGate $RepoRoot 2>&1 | Select-Object -Last 10 | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -eq 0) { Ok "RENAME-GATE OK" } else { Bad "RENAME-GATE FAILED (exit $LASTEXITCODE)"; $failed += "rename-gate" }
} else {
    Write-Host "rename-gate.py not found -- skipped" -ForegroundColor Yellow
}

# ---------------- 6/6 dashboard inline gate ----------------
# 10-16: index.html is a GENERATED single page (sources: app.css/app.js).
# Fails when sources and the embedded page drifted (someone hand-edited
# one side). Fix: python scripts/inline_dashboard.py
Step "6/6 DASHBOARD INLINE (app.css/app.js -> index.html in sync)"
$inlinePy = Join-Path $PSScriptRoot "inline_dashboard.py"
if (Test-Path $inlinePy) {
    & python $inlinePy --check 2>&1 | Select-Object -Last 3 | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -eq 0) { Ok "DASHBOARD INLINE OK" } else { Bad "DASHBOARD INLINE DRIFT (run: python scripts/inline_dashboard.py)"; $failed += "dashboard-inline" }
} else {
    Write-Host "inline_dashboard.py not found -- skipped" -ForegroundColor Yellow
}

# ---------------- summary ----------------
Step "SUMMARY"
if ($failed.Count -eq 0) { Ok "ALL GREEN"; exit 0 }
foreach ($f in $failed) { Bad "FAILED: $f" }
Bad "GATE FAILED"; exit 1
