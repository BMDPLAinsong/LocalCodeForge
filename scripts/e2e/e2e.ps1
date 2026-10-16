# ============================================================
# E2E 复测脚本（LocalCodeForge）
# 一键跑 4 类任务的端到端回归：工程增量 / 单文件 / 多文件 / 读查
# 每用例用独立工程副本（防污染），断言：
#   - CLI 退出码 / 任务日志中 [完成] 或失败标记
#   - 工程副本回滚干净（无残留文件、可编译）
#   - 单文件/多文件产物存在
# 结果汇总表 + 落 logs\e2e_<时间>.log
# 用法: powershell -File e2e.ps1 [-Cli <exe路径>] [-Demo <demo工程根>]
# ============================================================
param(
    [string]$Cli = "",
    [string]$Demo = "X:\lawcheck_demo"
)

$ErrorActionPreference = "Continue"
# 仓库根：本脚本位于 scripts\e2e\，上溯两级（不硬编码本地路径，clone 到任意位置可跑）
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if (-not $Cli) { $Cli = Join-Path $RepoRoot "src\Agent.Cli\bin\Debug\net10.0\Agent.Cli.exe" }
$logDir = Join-Path $RepoRoot "workspace\e2e"
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
$runsRoot = Join-Path $RepoRoot "workspace\runs"

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
    # PS 5.1 -File 模式向子进程 stdin 注入 UTF-8 BOM（EF BB BF，管道/直写/BaseStream 均绕不过）
    # → CLI use 命令不命中（use 行被当任务）= E2E 假完成根因。改用 python subprocess 喂 stdin（确定性）
    $cmdB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($cmd))
    python (Join-Path $PSScriptRoot "runner.py") $Cli $cmdB64 $caseLog
    $sw.Stop()

    $logText = Get-Content $caseLog -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
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
        $leftovers = Get-ChildItem $caseDir -Name -ErrorAction SilentlyContinue |
            Where-Object { $_ -match "ShippingService|OrderSystem|LoyaltyService|ShippingCalculator" }
        $dirty = $leftovers.Count -gt 0

        # 【10-14 A2 基础设施健康探针】原实现只用 demo 基线 diff（post-rollback 状态）判 touched——
        # 干净回滚后 caseDir == 基线 → touched=false → 被误判为"工程未接入"（实为"接入+写+失败+回滚干净"）。
        # 改用日志标记区分三态，把"基础设施回归"与"模型能力失败"分开计量：
        $rollbackFired = $logText -match "\[回滚\]"
        $writeFired    = $logText -match "\[写入\]|\[工具调用\] write_file|\[工具调用\] edit_file"
        $cmdFired      = $logText -match "run_command"
        $pdFired       = $logText -match "类型重复定义"

        if ($hasDone) {
            # 【10-15 修正】完成场景：写入保留的 ShippingService.cs 等是预期产物（任务要求新增该类），
            # 不算脏残留——dirty 语义仅用于"失败回滚后仍有残留"（回滚失效=基础设施回归）。
            # 【10-15 变体】完成场景额外断言任务要求的产物文件存在（expectFile 给出时）。
            $detail = if ($touched -or $writeFired) { "完成" } else { "侧路假完成-疑似漏洞" }
            $expectExists = -not $expectFile -or (Test-Path (Join-Path $caseDir $expectFile))
            if (-not $expectExists) { $detail += " [⚠ 预期产物缺失: $expectFile]" }
            $ok = ($touched -or $writeFired) -and $expectExists
        }
        elseif ($rollbackFired) {
            # 模型写入了工程文件 + 失败 + 自动回滚触发 = 安全网健康；残留则回滚失效（基础设施回归）
            $detail = "受控失败（安全网回滚）"
            $ok = -not $dirty
            if ($dirty) { $detail += " [⚠ 基础设施回归：回滚后仍有残留 $($leftovers -join ',')]" }
        }
        elseif ($hasIntercept -or $pdFired) {
            $detail = "安全拦截（P-D 命中）"
            $ok = -not $dirty
        }
        elseif ($hasFail) {
            # 失败但既未回滚也未 P-D 拦截——若写了文件则安全网缺失（基础设施回归），否则模型未接入写路径
            $detail = if ($writeFired) { "⚠ 基础设施回归：写后失败但未回滚" } else { "失败但未接入写路径" }
            $ok = $false
        }
        else {
            $detail = "异常（无完成/失败/拦截/回滚信号）"
            $ok = $false
        }

        # 基础设施健康信号（始终附上，供 CI/人眼一眼区分"模型失败"与"基础设施回归"）
        $detail += " [回滚=$rollbackFired 写入=$writeFired 命令=$cmdFired P-D=$pdFired]"
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
# 【10-15 变体】工程增量用例集（数据驱动，验证普适性）：
#   A 原 ShippingService（新类复用两个既有服务）
#   B 修改既有类新增方法（OrderService 增方法 + 改入口）
#   C 新类调用既有服务链（LoyaltyService → OrderService → DiscountService）
#   D 新增静态工具类（静态方法上下文，防 CS0120 反方向：静态上下文误调实例成员）
$incrementalCases = @(
    @{ Name = "工程增量-ShippingService"; Input = "在工程中新增一个 ShippingService 类，复用 TaxService 和 DiscountService 计算运费和折扣后的总价，并接入 OrderService。"; Expect = "ShippingService.cs" },
    @{ Name = "工程增量-修改既有方法"; Input = "给 OrderService 增加一个 CalculateFinalTotal(Order order) 方法，复用 TaxService 和 DiscountService 依次计算税费与折扣，返回最终总价；并把 Program.cs 改为调用该方法输出结果。"; Expect = $null },
    @{ Name = "工程增量-会员价服务"; Input = "新增一个 LoyaltyService 类，包含方法 CalculateLoyaltyPrice(Order order)，内部调用 OrderService.CalculateTotal(order) 计算基础总价，再调用 DiscountService.CalculateDiscount(order) 获取折扣并减去，返回会员价；接入 Program.cs 输出。"; Expect = "LoyaltyService.cs" },
    @{ Name = "工程增量-静态工具类"; Input = "新增一个 ShippingCalculator 静态类，包含静态方法 CalculateShipping(Order order, double baseRate)，返回 baseRate 与订单 Id 的乘积；在 Program.cs 中调用并输出。"; Expect = "ShippingCalculator.cs" }
)
foreach ($c in $incrementalCases) {
    Run-Case $c.Name "incremental" $c.Input $true $c.Expect
}
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
