# test_printui.ps1 — Kiem tra xem printui co mo duoc dialog Native (Printing Preferences / Printer
# Properties) cho mot may in bat ky hay khong. Ghi log day du ra tools/test_printui.log.
#
# Cach dung:
#   pwsh tools/test_printui.ps1 -PrinterName "LBP242/243"                 # thu /e (Printing Preferences)
#   pwsh tools/test_printui.ps1 -PrinterName "LBP242/243" -Verb "/p"      # thu /p (Printer Properties)
#
# Script KHONG de lai dialog kẹt: no ghi nhan cac PID no sinh ra va tu dong dong chung sau khi do.

param(
    [Parameter(Mandatory = $true)][string]$PrinterName,
    [ValidateSet("/e", "/p")][string]$Verb = "/e",
    [int]$WaitSeconds = 8
)

$ErrorActionPreference = 'Stop'
$logFile = Join-Path $PSScriptRoot "test_printui.log"
$log = { param($msg) "$(Get-Date -Format 'HH:mm:ss.fff')  $msg" | Out-File -FilePath $logFile -Append -Encoding utf8 }

# --- Add-Type: EnumWindows — do MOI cua so top-level dang hien thi (ke ca process cu) ---
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class WinEnum {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    public static Dictionary<string, string> Snapshot() {
        var r = new Dictionary<string, string>();
        EnumWindows((h, l) => {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            if (sb.Length == 0) return true; // bo cua so khong tieu de
            uint pid; GetWindowThreadProcessId(h, out pid);
            r[h.ToString()] = sb.ToString() + "|pid=" + pid;
            return true;
        }, IntPtr.Zero);
        return r;
    }
}
"@

# Noi dung log moi lan chay tach ra bang header
& $log "=============================================================="
& $log "RUN  Verb=$Verb  Printer='$PrinterName'  Wait=${WaitSeconds}s  (user: $([Environment]::UserName))"
& $log "Cmd: rundll32 printui.dll,PrintUIEntry $Verb /n`"$PrinterName`""

# --- 1) Baseline: cua so + process print-related dang chay ---
$baselineWins = [WinEnum]::Snapshot()
& $log "Baseline: $($baselineWins.Count) cua so top-level dang hien thi."
$baselinePids = @(Get-Process rundll32, PrintIsolationHost, PrintDialogHost, dllhost -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
foreach ($w in $baselineWins.GetEnumerator() | Where-Object { $_.Value -match 'print|may in|property|printer' -or $_.Key -match 'printui' }) {
    & $log "  base win: $($w.Key) = $($w.Value)"
}

# --- 2) Chay dialog: Gen qua cmd.exe de giong het duong dan C# ProcessStartInfo ---
# (cmd /c start "" /b giu nguyen args, khong re-quote nhu Start-Process)
$cmdLine = "start `"`" /b rundll32 `"printui.dll,PrintUIEntry`" $Verb /n`"$PrinterName`""
$a = Start-Process -FilePath "$env:SystemRoot\System32\cmd.exe" -ArgumentList "/c", "$cmdLine" -PassThru -WindowStyle Hidden
$spawned = @($a.Id)
& $log "Da khoi dong cmd PID=$($a.Id) (chay runt rundll32 detach)."

# --- 3) Poll: mo cua so MOI (diff so voi baseline) ---
$foundWindow = $false
$newWindowLines = @()
for ($i = 1; $i -le $WaitSeconds; $i++) {
    Start-Sleep -Seconds 1
    $now = [WinEnum]::Snapshot()
    foreach ($w in $now.GetEnumerator()) {
        if (-not $baselineWins.ContainsKey($w.Key)) {
            & $log "  [+$($i)s] CUA SO MOI: hwnd=$($w.Key)  $($w.Value)"
            $newWindowLines += "$($w.Key)=$($w.Value)"
            $foundWindow = $true
        }
    }
    if ($foundWindow) { break }
}

# Dem ca process print-related moi
$newProcs = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.Id -notin $baselinePids -and `
    ($_.ProcessName -eq 'rundll32' -or $_.ProcessName -eq 'PrintIsolationHost' -or
     $_.ProcessName -eq 'PrintDialogHost' -or $_.ProcessName -eq 'dllhost')
})
foreach ($p in $newProcs) { & $log "  process moi: $($p.Id) $($p.ProcessName) title='$($p.MainWindowTitle)'" ; $spawned += $p.Id }

# --- 4) Ket luan ---
if ($foundWindow) {
    & $log "KET LUAN: DIALOG DA MO (co cua so top-level moi)."
} elseif ($spawned.Count -gt 1) {
    & $log "KET LUAN: process moi xuat hien nhung KHONG thay cua so — dialog khong hien (hoac an)."
} else {
    & $log "KET LUAN: KHONG co gi xay ra — printui im lang."
}

# --- 5) Dong cac process da spawn (bao gom rundll32 con cua cmd) de khong de lai dialog kẹt ---
$spawned = @($spawned | Select-Object -Unique)
# Bat cac process print-related moi (con cua lenh rundll32 da tao) truoc khi dong
$residual = @(Get-Process rundll32, PrintIsolationHost, dllhost -ErrorAction SilentlyContinue | Where-Object { $_.Id -notin $baselinePids })
foreach ($r in $residual) { if ($r.Id -notin $spawned) { $spawned += $r.Id } }
foreach ($id in $spawned) {
    Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
}
& $log "Cleanup: da dong $($spawned.Count) process da spawn. XEM LOG: $logFile"
Write-Output "Xong. Log: $logFile"