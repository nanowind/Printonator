# test_shell_props.ps1 — Mo "Printer Properties" qua shell verb (ContextMenu "Properties") cua
# folder Devices and Printers, thay cho printui /p (khong hoat dong tren may nay).
# Ghi log + tu dong dang cua so dialog test. Khong block.
#
# Cach dung: pwsh tools/test_shell_props.ps1 -PrinterName "LBP242/243"

param(
    [Parameter(Mandatory = $true)][string]$PrinterName,
    [int]$WaitSeconds = 8
)

$ErrorActionPreference = 'Continue'
$logFile = Join-Path $PSScriptRoot "test_printui.log"
$log = { param($msg) "$(Get-Date -Format 'HH:mm:ss.fff')  $msg" | Out-File -FilePath $logFile -Append -Encoding utf8 }

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class WinEnum2 {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    public static Dictionary<string, string> Snapshot() {
        var r = new Dictionary<string, string>();
        EnumWindows((h, l) => {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            if (sb.Length == 0) return true;
            uint pid; GetWindowThreadProcessId(h, out pid);
            r[h.ToString()] = sb.ToString() + "|pid=" + pid;
            return true;
        }, IntPtr.Zero);
        return r;
    }
    public static bool CloseWindow(IntPtr h, int waitMs) {
        PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
        System.Threading.Thread.Sleep(waitMs);
        return true;
    }
}
"@

& $log "=============================================================="
& $log "SHELLPROPS  Printer='$PrinterName'  Wait=${WaitSeconds}s  (user: $([Environment]::UserName))"

$before = [WinEnum2]::Snapshot()
& $log "Baseline: $($before.Count) cua so."

# Tim printer trong Devices and Printers (CLSID A8A91A66) — neu khong co thi thu Printers and Faxes (26EE0668)
$paths = @(
    "shell:::{A8A91A66-3A7D-4424-8D24-04E180695C7A}",
    "shell:::{26EE0668-A00A-44D7-9371-BEB064C98683}"
)

$found = $false
foreach ($p in $paths) {
    if ($found) { break }
    try {
        $shell = New-Object -ComObject Shell.Application
        $folder = $shell.NameSpace($p)
        if ($null -eq $folder) { & $log "Khong mo duoc namespace: $p"; continue }
        $item = $null
        foreach ($i in $folder.Items()) {
            if ($i.Name -eq $PrinterName) { $item = $i; break }
        }
        if ($null -eq $item) { & $log "Khong thay printer trong: $p"; continue }
        & $log "Tim thay '$PrinterName' trong $p — invoke verb 'properties'..."
        $item.InvokeVerb('properties')
        $found = $true
        break
    } catch {
        & $log "Loi COM: $($_.Exception.Message)"
    }
}

if (-not $found) { & $log "KET LUAN: KHONG tim duoc printer trong Devices/Printers folder." }

# Poll cua so moi
$dialog = $null
for ($i = 1; $i -le $WaitSeconds; $i++) {
    Start-Sleep -Seconds 1
    $now = [WinEnum2]::Snapshot()
    foreach ($w in $now.GetEnumerator()) {
        if (-not $before.ContainsKey($w.Key)) {
            & $log "  [+$($i)s] CUA SO MOI: $($w.Key) = $($w.Value)"
            if ($w.Value -match "$PrinterName|Properties|property|Printers") { $dialog = $w.Key }
        }
    }
    if ($dialog) { break }
}

if ($dialog) {
    & $log "KET LUAN: DIALOG PROPERTIES DA MO ($dialog)"
    & $log "Dong dialog test sau 2s..."
    Start-Sleep -Seconds 2
    [WinEnum2]::CloseWindow([IntPtr]::new([int64]$dialog), 1000) | Out-Null
} else {
    & $log "KET LUAN: KHONG MO CUA SO NAO."
}

# Dong cac process moi cua dialog neu con sot
Get-Process rundll32,PrintIsolationHost,dllhost,explorer -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowTitle -match "$PrinterName|Properties" } |
    Stop-Process -Force -ErrorAction SilentlyContinue

& $log "Xong. Log: $logFile"
Write-Output "Xong. Log: $logFile"