# ============================================================
# drift-check.ps1 -- dual-path drift self-check (10-14 P3-5)
#
# PURPOSE: batch-gate helper. Scans src/*.cs comments for the
#   "drift / dual-path / single-source" warning markers this codebase
#   uses to flag places where two implementations must stay in sync,
#   and prints the per-file distribution. Exceeding the threshold
#   warns and exits non-zero (so it can be wired into CI later).
#
#   NOTE: a hit is NOT an error by itself -- most of these comments
#   explain WHY two places must stay in sync, which is valuable
#   knowledge. The threshold exists to catch a rising trend of NEW
#   un-converged duplicates, not to drive the count to zero.
#
#   ASCII-ONLY BY DESIGN: Windows PowerShell 5.1 reads BOM-less .ps1
#   files as ANSI (GBK here), which mangles non-ASCII text and can
#   break string quoting (see HANDOVER 3s: the PS 5.1 UTF-8/BOM trap).
#   Keeping this file ASCII removes that whole class of failure.
#
# BASELINE (2026-10-14): 51 hits / 19 files. Threshold 60 (~18% headroom).
#
# USAGE:  powershell -File scripts\drift-check.ps1 [-WarnThreshold 60]
# ============================================================

param(
    [int]$WarnThreshold = 60
)

$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'src'

if (-not (Test-Path $src)) {
    Write-Error "src directory not found: $src"
    exit 2
}

$files = Get-ChildItem $src -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }

# Pattern built from code points (ASCII-safe file): the three CJK markers as WHOLE words.
#   漂移 U+6F02 U+79FB | 双路径 U+53CC U+8DEF U+5F84 | 单一来源 U+5355 U+4E00 U+6765 U+6E90
# NOTE: match whole words, not single chars -- "漂/双/单" alone also hits 单独/双重/单点 etc.
$wDrift = [string]([char]0x6F02) + [char]0x79FB
$wDual = [string]([char]0x53CC) + [char]0x8DEF + [char]0x5F84
$wSingle = [string]([char]0x5355) + [char]0x4E00 + [char]0x6765 + [char]0x6E90
$pattern = $wDrift + '|' + $wDual + '|' + $wSingle
$hits = Select-String -Path $files.FullName -Pattern $pattern -Encoding UTF8
$fileCount = @($hits | Select-Object -ExpandProperty Path -Unique).Count

Write-Host "== drift-warning comments: $($hits.Count) hits / $fileCount files (threshold $WarnThreshold) =="
$hits | Group-Object { Split-Path $_.Path -Leaf } |
    Sort-Object Count -Descending |
    ForEach-Object { "  {0,-40} {1}" -f $_.Name, $_.Count }

if ($hits.Count -gt $WarnThreshold) {
    Write-Warning "Hits exceed threshold ($($hits.Count) > $WarnThreshold): new un-converged duplicates are likely."
    Write-Warning "For each hit: if it marks a real second implementation, converge it back to its single source;"
    Write-Warning "if it is explanatory only, raise the threshold and record the reason in the script header."
    exit 1
}

Write-Host "Within threshold. Goal is not zero hits -- it is 'no NEW second implementation'."
exit 0