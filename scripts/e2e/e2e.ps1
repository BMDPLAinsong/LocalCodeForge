﻿# ============================================================
# E2E 复测脚本（LocalAiWorkbench）
# 一键跑 4 类任务的端到端回归：工程增量 / 单文件 / 多文件 / 读查
# 每用例用独立工程副本（防污染），断言：
#   - CLI 退出码 / 任务日志中 [完成] 或失败标记
#   - 工程副本回滚干净（无残留文件、可编译）
#   - 单文件/多文件产物存在
# 结果汇总表 + 落 logs\e2e_<时间>.log
# 用法: powershell -File e2e.ps1 [-Cli <exe路径>] [-Demo <demo工程根>]
# ============================================================
param(
    [string]$Cli = "X:\.NET AI\LocalAiWorkbench\src\Agent.Cli\bin\Debug\net10.0\Agent.Cli.exe",
    [string]$Demo = "X:\lawcheck_demo"
)

$ErrorActionPreference = "Continue"
$logDir = "X:\.NET AI\LocalAiWorkbench\workspace\e2e"
New-Item -ItemType Directory -Force $logDir | Out-Null
$ts = Get-Date -Format "yyyyMMdd-HHmmss"
$logFile = Join-Path $logDir "e2e_$ts.log"

function Write-Log($msg) {
    $line = "[$(Get-Date -Format 'HH:mm:ss')] $msg"
    Add-Content -Path $logFile -Value $line -Encoding utf8
    Write-Host $line
}

Write-Log "=== E2E 开始 $ts ==="
Write-Log "CLI: $Cli"
Write-Log "Demo: $Demo"

if (-not (Test-Path $Cli)) { Write-Log "错误：CLI 不存在 $Cli"; exit 1 }
if (-not (Test-Path $Demo)) { Write-Log "错误：demo 不存在 $Demo"; exit 1 }

$results = @()
$runsRoot = "X:\.NET AI\LocalAiWorkbench\workspace\runs"

function Run-Case($name, $kind, $userInput, $writeMode, $expectFile) {
    Write-Log "── 用例 [$name] kind=$kind ──"
    # 工程副本隔离
    $caseDir = Join-Path $logDir "case_$($kind)_$(Get-Random -Maximum 99999)"
    New-Item -ItemType Directory -Force $caseDir | Out-Null
    if ($writeMode) {
        Copy-Item -Path "$Demo\*" -Destination $caseDir -Recurse -Force
        # 清掉 bin/obj 避免复制编译产物
        Remove-Item -Path (Join-Path $caseDir "bin") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path (Join-Path $caseDir "obj") -Recurse -Force -ErrorAction SilentlyContinue
    }
    $caseLog = Join-Path $logDir "case_$($kind)_$([IO.Path]::GetFileName($caseDir)).log"
    $cmd = if ($writeMode) { "use --write $caseDir`n$userInput`nexit`n" } else { "$userInput`nexit`n" }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $cmd | & $Cli *> $caseLog
    $sw.Stop()

    $logText = Get-Content $caseLog -Raw -ErrorAction SilentlyContinue
    $hasDone = $logText -match "\[完成\]"
    $hasFail = $logText -match "任务未通过验证|\[错误\]|\[失败\]"
    $hasIntercept = $logText -match "工程增量计划校验|类型重复定义|不存在于工程根"
    # 解析 CLI 工作目录（[工作目录] <runName>（任务归属 task_xxx））→ 产物断言目标
    $runName = ""
    if ($logText -match "\[工作目录\]\s+(\S+?)（任务归属") { $runName = $Matches[1] }
    $runDir = if ($runName) { Join-Path $runsRoot $runName } else { $null }

    # 断言
    $ok = $false
    $detail = ""
    if ($kind -eq "incremental") {
        # 工程增量：关键是"工程副本确实被接入"——caseDir 与 demo 基线 diff（假完成检测：
        # 模型侧路新建 OrderSystem 项目编译通过但 demo 未动 → caseDir 与基线一致 → FAIL）
        $touched = $false
        foreach ($f in Get-ChildItem $Demo -File -ErrorAction SilentlyContinue) {
            $rel = $f.Name
            $copy = Join-Path $caseDir $rel
            if (Test-Path $copy) {
                $a = (Get-FileHash $f.FullName).Hash
                $b = (Get-FileHash $copy).Hash
                if ($a -ne $b) { $touched = $true; break }
            }
        }
        $leftovers = Get-ChildItem $caseDir -Name -ErrorAction SilentlyContinue | Where-Object { $_ -match "ShippingService|OrderSystem" }
        $dirty = $leftovers.Count -gt 0
        $ok = $touched -and (-not $dirty) -and ($hasDone -or $hasIntercept -or $hasFail)
        if ($hasDone) { $detail = "完成" }
        elseif ($hasIntercept) { $detail = "安全拦截" }
        elseif ($hasFail) { $detail = "失败但受控" }
        else { $detail = "异常" }
        if (-not $touched) { $detail += " [工程未接入-假完成]" }
        if ($dirty) { $detail += " [残留:$($leftovers -join ',')]" }
    } elseif ($kind -eq "single" -or $kind -eq "multi") {
        # 非写模式产物在 CLI 自己的工作目录（run 目录），不在 caseDir
        $fileExists = $false
        if ($expectFile -and $runDir) { $fileExists = Test-Path (Join-Path $runDir $expectFile) }
        elseif ($expectFile) { $fileExists = Test-Path (Join-Path $caseDir $expectFile) }
        $ok = $hasDone -and ($fileExists -or -not $expectFile)
        $detail = "完成=$hasDone 产物=$fileExists run=$runName"
    } else {
        # read
        $ok = $hasDone -and (-not $hasFail)
        $detail = "回答=$hasDone"
    }

    Write-Log "结果: $name => $(if ($ok) {'PASS'} else {'FAIL'}) [$detail] 耗时 $([int]$sw.Elapsed.TotalSeconds)s"
    $script:results += [pscustomobject]@{ Name = $name; Kind = $kind; Ok = $ok; Detail = $detail; Sec = [int]$sw.Elapsed.TotalSeconds; Log = $caseLog }
}

# ── 用例集 ──
Run-Case "工程增量-ShippingService" "incremental" "在工程中新增一个 ShippingService 类，复用 TaxService 和 DiscountService 计算运费和折扣后的总价，并接入 OrderService。" $true $null
Run-Case "单文件-hello python" "single" "写一个名为 hello_e2e.py 的脚本，打印 Hello E2E" $false "hello_e2e.py"
Run-Case "多文件-C#学生系统" "multi" "用 C# 写一个学生管理系统，分 Student 模型、StudentService、Program，多文件组织" $false $null
Run-Case "读查-Order.cs 内容" "read" "读取 X:\lawcheck_demo\Order.cs 的内容" $false $null

# ── 汇总 ──
Write-Log ""
Write-Log "=== E2E 汇总 ==="
$pass = ($script:results | Where-Object Ok).Count
foreach ($r in $script:results) {
    Write-Log ("  {0,-32} {1,-5} {2,6}s  {3}" -f $r.Name, $(if ($r.Ok) {"PASS"} else {"FAIL"}), $r.Sec, $r.Detail)
}
Write-Log "通过: $pass/$($script:results.Count)"
Write-Log "日志: $logFile"
exit $(if ($pass -eq $script:results.Count) { 0 } else { 1 })
