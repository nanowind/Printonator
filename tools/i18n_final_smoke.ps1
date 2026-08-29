$ErrorActionPreference = 'Continue'

$app    = 'C:\Users\scraw\Projects\Printonator\src\Printonator.UI\bin\Debug\net8.0-windows10.0.19041.0\Printonator.UI.exe'
$outDir = 'C:\Users\scraw\Projects\Printonator\i18n'
$logPath = 'C:\Users\scraw\Projects\Printonator\tools\i18n_final_smoke.log'

if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

Start-Transcript -Path $logPath -Force | Out-Null
Write-Output "=== i18n final smoke start $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ==="
Write-Output "App exists: $(Test-Path $app)"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32Shot {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

function Capture-Window([IntPtr]$hWnd, [string]$path) {
  $rect = New-Object Win32Shot+RECT
  if (-not [Win32Shot]::GetWindowRect($hWnd, [ref]$rect)) { return $false }
  $w = $rect.Right - $rect.Left
  $h = $rect.Bottom - $rect.Top
  if ($w -le 0 -or $h -le 0) { return $false }
  $bmp = New-Object System.Drawing.Bitmap($w, $h)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w,$h)))
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  return $true
}

$langs = @('vi-VN','en-US','zh-CN','ru-RU','ja-JP')
$results = @()

foreach ($lang in $langs) {
  $row = [ordered]@{ Lang=$lang; Alive=$false; Title=''; Shot=''; Crash=$false; Note='' }
  try {
    $env:PRINTONATOR_LANGUAGE = $lang
    $p = Start-Process -FilePath $app -PassThru
    Remove-Item Env:PRINTONATOR_LANGUAGE
    Write-Output "[$lang] started PID=$($p.Id)"

    Start-Sleep -Seconds 4

    $p.Refresh()
    if ($p.HasExited) {
      $row.Crash = $true
      $row.Note = "EXITED code=$($p.ExitCode)"
      Write-Output "[$lang] PROCESS EXITED code=$($p.ExitCode)"
    } else {
      $row.Alive = $true
      try { $p.WaitForInputIdle(2000) | Out-Null } catch {}
      $p.Refresh()
      $title = $p.MainWindowTitle
      $row.Title = $title
      Write-Output "[$lang] alive, title='$title'"

      if ($title) {
        [Win32Shot]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
        Start-Sleep -Milliseconds 800
        $shot = Join-Path $outDir ("final_" + $lang + ".png")
        $ok = Capture-Window $p.MainWindowHandle $shot
        if ($ok) { $row.Shot = $shot; Write-Output "[$lang] screenshot -> $shot" }
        else { $row.Note += ' SHOT_FAIL'; Write-Output "[$lang] screenshot FAILED" }
      } else {
        $row.Note += ' NO_TITLE'
        Write-Output "[$lang] NO MainWindowTitle"
      }

      # close politely, then force-on-this-PID-only
      $closed = $p.CloseMainWindow()
      Write-Output "[$lang] CloseMainWindow returned $closed"
      Start-Sleep -Seconds 3
      $p.Refresh()
      if (-not $p.HasExited) {
        Write-Output "[$lang] still alive, Stop-Process (this PID only: $($p.Id))"
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
        $p.Refresh()
        if ($p.HasExited) { $row.Note += ' FORCE_KILLED' }
      }
    }
  } catch {
    $row.Crash = $true
    $row.Note = 'EXCEPTION: ' + $_.Exception.Message
    Write-Output "[$lang] EXCEPTION: $($_.Exception.Message)"
  }
  $results += $row
}

Write-Output ""
Write-Output "=== SUMMARY ==="
Write-Output ("{0,-8} {1,-6} {2,-40} {3}" -f 'Lang','Alive','Title','Screenshot')
foreach ($r in $results) {
  Write-Output ("{0,-8} {1,-6} {2,-40} {3}" -f $r.Lang, $r.Alive, $r.Title, (Split-Path $r.Shot -Leaf))
  if ($r.Note) { Write-Output ("         note: {0}" -f $r.Note) }
}
Stop-Transcript | Out-Null
Write-Output "LOG: $logPath"
