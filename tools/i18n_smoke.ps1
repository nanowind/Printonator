$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SmokeWin32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
"@

$AppPath = 'C:\Users\scraw\Projects\Printonator\src\Printonator.UI\bin\Debug\net8.0-windows10.0.19041.0\Printonator.UI.exe'
$OutDir  = 'C:\Users\scraw\Projects\Printonator\i18n'
$LogFile = 'C:\Users\scraw\Projects\Printonator\tools\i18n_smoke.log'

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$header = @"
==============================================================
i18n SMOKE  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
App: $AppPath
(round 2: FULL culture tags — round 1 two-letter all fell back to vi-VN, see log)
==============================================================
"@
Set-Content -Path $LogFile -Value $header -Encoding utf8
Write-Output $header

function Log([string]$text) { $text | Tee-Object -FilePath $LogFile -Append }

$old = @(Get-Process -Name 'Printonator.UI' -ErrorAction SilentlyContinue)
foreach ($p in $old) { Log "pre-clean: kill leftover Printonator.UI pid $($p.Id)"; Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 800

# (env value, short code for filename)
$langs = @(
    @('vi-VN','vi'), @('en-US','en'), @('zh-CN','zh'), @('ru-RU','ru'), @('ja-JP','ja')
)
$results = @()

foreach ($pair in $langs) {
    $envVal = $pair[0]; $code = $pair[1]
    Log ""
    Log "==== SMOKE env=$envVal (short=$code) ===="
    $env:PRINTONATOR_LANGUAGE = $envVal

    $proc = Start-Process -FilePath $AppPath -PassThru
    Log "started pid=$($proc.Id) env=$envVal wait 4s"

    Start-Sleep -Seconds 4
    $proc.Refresh()

    $alive = $true
    try { $alive = -not $proc.HasExited } catch { $alive = $true }
    $title = ''
    if ($alive) { try { $title = $proc.MainWindowTitle } catch { $title = '' } }
    Log "alive=$alive title='$title'"

    if ($alive) {
        $hwnd = [IntPtr]::Zero
        try { $hwnd = $proc.MainWindowHandle } catch { $hwnd = [IntPtr]::Zero }
        if ($hwnd -ne [IntPtr]::Zero) {
            [SmokeWin32]::ShowWindow($hwnd, 9) | Out-Null
            [SmokeWin32]::SetForegroundWindow($hwnd) | Out-Null
            Start-Sleep -Milliseconds 500
            $proc.Refresh()
        }
    }

    $shot = "$OutDir\smoke_$code.png"
    try {
        $bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($bounds.X, $bounds.Y, 0, 0, $bmp.Size)
        $bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        Log "screenshot=$shot ok"
    } catch {
        Log "screenshot FAILED: $($_.Exception.Message)"
    }

    $exitNote = ''
    if (-not $alive) {
        $exitCode = ''
        try { $exitCode = $proc.ExitCode } catch {}
        $exitNote = "EXITED exit=$exitCode"
        Log $exitNote
        try {
            $since = (Get-Date).AddMinutes(-2)
            $evts = @(Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=$since} -ErrorAction SilentlyContinue |
                Where-Object { $_.Message -match 'Printonator.UI|\.NET Runtime|Application Error|XamlParse' } |
                Select-Object -First 6)
            foreach ($e in $evts) {
                $msg = $e.Message
                if ($msg.Length -gt 1500) { $msg = $msg.Substring(0, 1500) }
                Log ("EVENT[{0}] id={1} prov={2}:" -f $e.TimeCreated.ToString('HH:mm:ss'), $e.Id, $e.ProviderName)
                Log "  msg: $msg"
            }
            if ($evts.Count -eq 0) { Log 'eventlog: no matching events in last 2 min' }
        } catch { Log "eventlog query failed: $($_.Exception.Message)" }
    } else {
        $closed = $proc.CloseMainWindow()
        Log "CloseMainWindow returned=$closed"
        if ($closed) {
            if (-not $proc.WaitForExit(5000)) {
                Log "did not exit in 5s after close -> Stop-Process pid=$($proc.Id)"
                Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            } else { Log "exited cleanly after CloseMainWindow" }
        } else {
            Log "CloseMainWindow failed -> Stop-Process pid=$($proc.Id)"
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
        Start-Sleep -Milliseconds 800
        $rem = @(Get-Process -Name 'Printonator.UI' -ErrorAction SilentlyContinue)
        foreach ($r in $rem) { Log "post-clean leftover Printonator.UI pid $($r.Id)"; Stop-Process -Id $r.Id -Force -ErrorAction SilentlyContinue }
        Start-Sleep -Milliseconds 500
    }

    $results += [pscustomobject]@{ Env=$envVal; Short=$code; Alive=$alive; Title=$title; Shot=$shot; Note=$exitNote }
}

Log ""
Log "==== SUMMARY ===="
foreach ($r in $results) {
    Log ("{0} ({1}): alive={2} title='{3}' shot={4} {5}" -f $r.Env, $r.Short, $r.Alive, $r.Title, $r.Shot, $r.Note)
}
Log "END $(Get-Date -Format 'HH:mm:ss')"
