# ============================================================
# verify.ps1 -- one-command gate for LocalAiWorkbench
#   1. build       (exit code must be 0)
#   2. tests       (exit code must be 0; harness: 0=all pass, 1=has failure)
#   3. js-syntax   (embedded index.html <script> must pass node --check)
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
$sln       = Join-Path $RepoRoot "LocalAiWorkbench.slnx"
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
Step "1/3 BUILD (expect exit 0)"
& dotnet build $sln --no-incremental -v q 2>&1 | Select-Object -Last 5 | ForEach-Object { Write-Host $_ }
$buildOk = ($LASTEXITCODE -eq 0)
if ($buildOk) { Ok "BUILD OK" } else { Bad "BUILD FAILED (exit $LASTEXITCODE)"; $failed += "build" }

# ---------------- 2/3 tests ----------------
Step "2/3 TESTS (expect exit 0)"
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
# and killed the whole dashboard script silently. Extract script block with
# python, syntax-check with node, then delete the temp file.
Step "3/4 JS SYNTAX (index.html <script> via node --check)"
$htmlPath = Join-Path $RepoRoot "src\Agent.Dashboard\DashboardPages\index.html"
$tmpJs    = Join-Path $env:TEMP ("lcf_jscheck_" + [guid]::NewGuid().ToString("N") + ".js")
$nodeExe  = Get-Command node -ErrorAction SilentlyContinue
if (-not $nodeExe) {
    Write-Host "node not found -- JS gate skipped" -ForegroundColor Yellow
} elseif (-not (Test-Path $htmlPath)) {
    Write-Host "index.html not found -- JS gate skipped" -ForegroundColor Yellow
} else {
    $pyCode = "import re,io,sys; s=io.open(sys.argv[1],encoding='utf-8').read(); m=re.findall(r'<script>(.*?)</script>', s, re.S); io.open(sys.argv[2],'w',encoding='utf-8').write(m[0] if m else '')"
    & python -c $pyCode $htmlPath $tmpJs 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Bad "JS EXTRACT FAILED (python)"; $failed += "js-syntax"
    } else {
        & node --check $tmpJs 2>&1 | Select-Object -First 5 | ForEach-Object { Write-Host $_ }
        if ($LASTEXITCODE -eq 0) { Ok "JS SYNTAX OK" } else { Bad "JS SYNTAX FAILED (exit $LASTEXITCODE)"; $failed += "js-syntax" }
    }
    Remove-Item $tmpJs -ErrorAction SilentlyContinue
}

# ---------------- 4/4 drift gate ----------------
Step "4/4 DRIFT CHECK (expect exit 0)"
if (Test-Path $driftPath) {
    & powershell -NoProfile -File $driftPath 2>&1 | Select-Object -Last 3 | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -eq 0) { Ok "DRIFT OK" } else { Bad "DRIFT CHECK FAILED (exit $LASTEXITCODE)"; $failed += "drift-check" }
} else {
    Write-Host "drift-check.ps1 not found -- skipped" -ForegroundColor Yellow
}

# ---------------- summary ----------------
Step "SUMMARY"
if ($failed.Count -eq 0) { Ok "ALL GREEN"; exit 0 }
foreach ($f in $failed) { Bad "FAILED: $f" }
Bad "GATE FAILED"; exit 1
