# test_full.ps1 — Build + test TOÀN BỘ repo, ghi log đầy đủ (tools/test_full.log) + phản chiếu console.
#
# Cách dùng (non-interactive — chạy được từ CI/agent, KHÔNG prompt):
#   pwsh tools/test_full.ps1                    # build + test
#   pwsh tools/test_full.ps1 -SkipBuild         # chỉ test (--no-build)
#
# Pattern bắt buộc của repo: `dotnet test` TRỰC TIẾP treo ở pha build trên máy này
# → luôn chạy `dotnet build` trước rồi `dotnet test --no-build`.
#
# Log: GHI ĐÈ mỗi lần chạy (header ghi rõ lệnh + thời gian + working dir).
# Cleanup (finally + trap): kill CHỈ các process thuộc test/app này (testhost, vstest.console,
# Printonator.UI) — TUYỆT ĐỐI KHÔNG đụng chrome/msedge/trình duyệt của user.

param(
    [switch]$SkipBuild,
    # Watchdog: mỗi giai đoạn test (Core / UI theo project) bị giới hạn thời gian; quá
    # ngưỡng → coi là HUNG, tree-kill cả subtree dotnet test (testhost/app UI) để KHÔNG
    # để process mồ côi + không block mãi. Lý do: test UI (FlaUI launch app) từng treo
    # vĩnh viễn trên máy này — không watchdog = vstest không trả, finally cleanup chạy
    # không kịp, script block mãi + rải mồ côi (đúng lỗi vừa gặp).
    [int]$TestTimeoutSec = 300
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot          # = thư mục repo (chứa Printonator.sln)
$logFile = Join-Path $PSScriptRoot "test_full.log"

$script:cleaned = $false

# ===== Header log: lệnh đầy đủ + thời gian + working dir =====
$runLine = if ($SkipBuild) { 'dotnet test Printonator.sln --no-build' } else { 'dotnet build Printonator.sln + dotnet test Printonator.sln --no-build' }
$header = @"
==============================================================
RUN   $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  (user: $([Environment]::UserName))
Cmd:  $runLine
Workdir: $root
Log:  $logFile  (ghi đè mỗi lần chạy)
==============================================================
"@
Set-Content -Path $logFile -Value $header -Encoding utf8
Write-Output $header

# ===== Cleanup: đóng MỌI process do test spawn (không đụng browser của user) =====
function Cleanup-Processes {
    if ($script:cleaned) { return }
    $script:cleaned = $true
    # Chỉ tên process thuộc hệ sinh thái dotnet test + app này; testhost.x86/testhost64 là
    # biến thể cùng họ (dotnet test) — KHÔNG có name nào là chrome/msedge/browser của user.
    $names = @('testhost', 'testhost.x86', 'testhost64', 'vstest.console', 'Printonator.UI')
    $killed = @()
    foreach ($name in $names) {
        foreach ($p in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            $killed += "$name($($p.Id))"
        }
    }
    $msg = if ($killed.Count -gt 0) { "cleaned: killed $($killed -join ', ')" }
           else                     { 'cleaned: no leftover testhost/vstest.console/Printonator.UI' }
    $msg | Tee-Object -FilePath $logFile -Append
}

# Log 1 dòng ra cả file lẫn console
function Log([string]$text) { $text | Tee-Object -FilePath $logFile -Append }

# Chạy lệnh, ghi toàn bộ output, trả về array dòng (string hóa để log sạch).
# KHÔNG dùng `& $cmdArgList` với array: PS 5.1 nối array thành 1 chuỗi rồi coi
# cả chuỗi là tên lệnh ("The term 'dotnet build ...' is not recognized").
# → tách exe + args, splat args: & $exe @args2 (params = positional args).
function Invoke-Logged([string[]]$cmdArgList) {
    $exe = $cmdArgList[0]
    $args2 = @($cmdArgList | Select-Object -Skip 1)
    $raw = @(& $exe @args2 2>&1)
    $lines = $raw | ForEach-Object { $_.ToString() }
    $lines | Tee-Object -FilePath $logFile -Append   # input vào tail: tự in ra console
    return ,$lines
}

try {
    trap {
        Log "`n[FATAL] $($_.Exception.Message)"
        exit 1
    }

    # ===== 1) Build =====
    if (-not $SkipBuild) {
        Log "`n==== [1/2] dotnet build Printonator.sln ===="
        $buildOut = @(Invoke-Logged @('dotnet', 'build', 'Printonator.sln'))
        $buildCode = $LASTEXITCODE
        if ($buildCode -ne 0) {
            Log "`nBUILD FAILED (exit $buildCode) — KHÔNG chạy test (--no-build sẽ fail/mơ hồ)."
            exit $buildCode
        }
        Log "`nBUILD OK."
    }
    else {
        Log "`n==== [SkipBuild] Bỏ qua dotnet build — chỉ chạy dotnet test --no-build. ===="
    }

    # ===== 2) Test — MỖI project một lần chạy TRỰC TIẾP, watchdog theo project =====
    # Chạy tách Core vs UI (KHÔNG gộp cả sln): Core nhanh/ổn định, UI chậm/dễ treo trên
    # máy này — kết quả tách biệt, exit code trung thực từng phần.
    # KHÔNG dùng Start-Job (khởi động cả 1 tiến trình pwsh/job hop — cực chậm). Dùng
    # Start-Process + Wait-Process -TimeoutSeconds: watchdog THẬT, treo thì kill tree.
    Log "`n==== [2/2] dotnet test (per-project, watchdog $TestTimeoutSec s/project) ===="
    $testOut = @(); $testCode = 0; $passed = 0; $failed = 0
    $projects = @(
        'tests/Printonator.Core.Tests/Printonator.Core.Tests.csproj',
        'tests/Printonator.Spool.Tests/Printonator.Spool.Tests.csproj',
        'tests/Printonator.UITests/Printonator.UITests.csproj'
    )
    foreach ($proj in $projects) {
        Log "`n-- test project: $proj --"
        $logThis = Join-Path $PSScriptRoot ("test_full_$(Split-Path $proj -Leaf).log")
        $args2 = @('test', $proj, '--no-build', '-v', 'minimal')
        # Redirect stdout/stderr vào file riêng (ghi đè) — tránh giữ ống cũa Bash, chạy trực tiếp.
        $p = Start-Process -FilePath 'dotnet' -ArgumentList $args2 -NoNewWindow `
            -WorkingDirectory $root -RedirectStandardOutput $logThis `
            -RedirectStandardError "$($logThis).err" -PassThru
        # Watchdog THẬT (PS 5.1 không có Wait-Process -TimeoutSeconds): poll tới $TestTimeoutSec
        $deadline = (Get-Date).AddSeconds($TestTimeoutSec)
        while (-not $p.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500; $p.Refresh() }
        if (-not $p.HasExited) {
            # TREO: kill toàn bộ subtree test/app UI (KHÔNG đụng browser user), tính lỗi 124, ngừng lô.
            Log "[WATCHDOG] $proj KHÔNG kết thúc trong $TestTimeoutSec s — treo. Kill subtree."
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            Cleanup-Processes
            $testCode = 124
            $testOut += "[WATCHDOG TIMEOUT] $proj"
            if (Test-Path $logThis) { Get-Content $logThis | ForEach-Object { Log $_ } }
            if (Test-Path "$($logThis).err") { Get-Content "$($logThis).err" | ForEach-Object { Log $_ } }
            break  # ngừng — không chạy project sau khi một project treo
        }
        $codeThis = $p.ExitCode
        if ($codeThis -ne 0) {
            if ($testCode -eq 0) { $testCode = $codeThis }
            $testOut += "[EXIT $codeThis] $proj"
        }
        # in log riêng của project vào cả log chính
        if (Test-Path $logThis) { Get-Content $logThis | ForEach-Object { Log $_ } }
        if (Test-Path "$($logThis).err") { Get-Content "$($logThis).err" | ForEach-Object { Log $_ } }
    }

    # Đếm PASS/FAIL từ output các dòng kiểu "Passed: 62" / "Failed: 3" (xunit/VSTest summary)
    $text = ($testOut -join "`n")
    $passed = 0; $failed = 0
    [regex]::Matches($text, 'Passed:\s*(\d+)') | ForEach-Object { $passed += [int]$_.Groups[1].Value }
    [regex]::Matches($text, 'Failed:\s*(\d+)')  | ForEach-Object { $failed  += [int]$_.Groups[1].Value }

    # ===== Tổng kết rõ ràng =====
    if ($testCode -eq 0 -and $failed -eq 0) {
        Log "`nRESULT: PASSED — tests: Passed=$passed Failed=$failed  (dotnet test exit $testCode)"
        $final = 0
    }
    elseif ($testCode -eq 124) {
        # Watchdog đã can thiệp (một project treo) → kết quả KHÔNG thể đếm đầy đủ; nói rõ hơn.
        Log "`nRESULT: WATCHDOG TIMEOUT — một/nhiều project treo (xem dòng [WATCHDOG] ở trên). Passed=$passed Failed=$failed"
        $final = 124
    }
    else {
        Log "`nRESULT: FAILED — tests: Passed=$passed Failed=$failed  (dotnet test exit $testCode)"
        $final = if ($testCode -ne 0) { $testCode } else { 1 }
    }
}
finally {
    Cleanup-Processes
    Log "END $(Get-Date -Format 'HH:mm:ss') — XEM LOG: $logFile  (dòng tổng kết ở trên)"
}

exit $final