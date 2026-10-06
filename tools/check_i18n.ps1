<#
    tools/check_i18n.ps1 - Translation consistency gate (anti AI-slop).
    Checks the Printonator Strings.json catalog:
      1. Key-set parity across the 5 languages (vi/en/zh/ru/ja)
      2. No empty/null values
      3. Placeholder {n} parity between vi and each language
      4. Flags "---" (untranslated keys)
      5. Flags Vietnamese diacritics leaking into the en column
      6. Flags escaped XML entities (e.g. "&amp;") — WPF renders them literally, write a plain '&'
    Used in: pre-commit (dev) + release.yml (ship gate).
#>
param(
    [string]$Catalog = "$PSScriptRoot\..\src\Printonator.UI\Localization\Strings.json"
)

function Write-Info($msg) { Write-Host "[i18n] $msg" }

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Catalog)) { Write-Error "Catalog not found: $Catalog"; exit 2 }

$json = Get-Content -Raw -Encoding UTF8 $Catalog | ConvertFrom-Json
$langs = @('vi','en','zh','ru','ja')
$missingLangs = $langs | Where-Object { -not $json.PSObject.Properties.Name.Contains($_) }
if ($missingLangs) { Write-Error "Missing languages: $($missingLangs -join ',')"; exit 2 }

$vi = $json.vi
$viKeys = @($vi.PSObject.Properties.Name)
Write-Info "vi: $($viKeys.Count) keys"

$issues = New-Object 'System.Collections.Generic.List[string]'
$untranslated = New-Object 'System.Collections.Generic.List[string]'
$enDiacritics = @('à','á','ạ','ả','ã','â','ấ','ầ','ẩ','ẫ','ậ','ă','ắ','ằ','ẳ','ẵ','ặ','è','é','ẹ','ẻ','ẽ','ê','ế','ề','ể','ễ','ệ','ì','í','ị','ỉ','ĩ','ò','ó','ọ','ỏ','õ','ô','ố','ồ','ổ','ỗ','ộ','ơ','ớ','ờ','ở','ỡ','ợ','ù','ú','ụ','ủ','ũ','ưứ','ử','ữ','ự','ỳ','ý','ỵ','ỷ','ỹ','đ','Đ')

# Sentinel values that intentionally stay verbatim in every language (written into Config.PrinterName etc).
$sentinelExempt = @('Main.PrinterDefaultName')

# 6. Escaped XML entity — L10n.S returns it raw, WPF Text shows it literally.
$entityPattern = '&(amp|lt|gt|quot|apos|#[0-9]+|#x[0-9A-Fa-f]+);'

# 6. Entity trong CHÍNH cột vi — loop dưới chỉ chạy 4 ngôn ngữ kia, mà vi là bản gốc + fallback của UI,
#    nên entity lọt vào đây vẫn ship xanh. Kiểm riêng, MỘT lần cho mỗi key (trước loop ngôn ngữ).
foreach ($key in $viKeys) {
    $v = [string]$vi.$key
    if ($v -match $entityPattern) {
        $issues.Add("[vi] '$key' contains escaped entity '$($Matches[0])' — write a plain '&' instead")
    }
}

# 1. Key-set parity + 2. empty/null + 3. placeholder parity + 4. untranslated
foreach ($lang in @('en','zh','ru','ja')) {
    $dict = $json.$lang
    $keys = @($dict.PSObject.Properties.Name)
    if ($keys.Count -ne $viKeys.Count) {
        $diff = @(Compare-Object $viKeys $keys)
        $issues.Add("[$lang] key-set mismatch: $($keys.Count) vs vi $($viKeys.Count) (diff $($diff.Count))")
        continue
    }
    foreach ($key in $viKeys) {
        $v = $dict.$key
        if ($null -eq $v) { $issues.Add("[$lang] key '$key' is null"); continue }
        if ($v.Trim() -eq '---') { $untranslated.Add("$lang/$key"); continue }
        # Placeholder parity: same set of {n}
        $viPh = [regex]::Matches(($vi.$key -as [string]), '\{\d+\}') | ForEach-Object { $_.Value }
        $langPh = [regex]::Matches(($v -as [string]), '\{\d+\}') | ForEach-Object { $_.Value }
        $setVi = @($viPh | Sort-Object -Unique) -join ','
        $setLang = @($langPh | Sort-Object -Unique) -join ','
        if ($setVi -ne $setLang) {
            $issues.Add("[$lang] '$key' placeholder mismatch: vi [$setVi] vs [$setLang]")
        }
        # 5. Vietnamese diacritics leaking into en -> strong red flag (except sentinels)
        if ($lang -eq 'en' -and $sentinelExempt -notcontains $key) {
            foreach ($ch in $enDiacritics) {
                if ($v.IndexOf($ch) -ge 0) {
                    $issues.Add("[en] key '$key' looks like leftover Vietnamese: '$v'")
                    break
                }
            }
        }
        # 6. Escaped XML entity — L10n.S returns it raw, WPF Text shows it literally
        if ([string]$v -match $entityPattern) {
            $issues.Add("[$lang] '$key' contains escaped entity '$($Matches[0])' — write a plain '&' instead")
        }
    }
}

# Report
if ($untranslated.Count -gt 0) {
    $sample = ($untranslated[0..([Math]::Min(4, $untranslated.Count-1))] -join ', ')
    Write-Info "$($untranslated.Count) keys still untranslated (---): e.g. $sample"
} else {
    Write-Info "All $($viKeys.Count) keys translated in all 5 languages."
}

if ($issues.Count -gt 0) {
    Write-Host "`n=== $($issues.Count) ISSUES ==="
    $issues | ForEach-Object { Write-Host "  $_" }
    Write-Host "=== /ISSUES ==="
    exit 1
}

Write-Info "CHECK PASS - key parity OK, placeholder parity OK, no Vietnamese leak into en."
exit 0