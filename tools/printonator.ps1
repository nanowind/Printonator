# printonator.ps1 - CLI mỏng điều khiển in qua MCP server Printonator
#
# Lệnh chạy được khi app Printonator đang MỞ (MCP server chạy nền trong app tại
# http://127.0.0.1:3939/mcp — xem docs/MCP.md). Chỉ gọi MCP, KHÔNG đụng engine in.
#
# Protocol: Streamable HTTP + JSON-RPC. Script TỰ làm initialize handshake nếu
# server yêu cầu (tools/call trả lỗi "chưa khởi tạo" -> gửi initialize +
# notifications/initialized rồi thử lại một lần). Giữ header Mcp-Session-Id nếu
# server trả về.
#
# Encoding: UTF-8 (có BOM) để Windows PowerShell 5.1 đọc đúng tiếng Việt.

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Command,
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)]
    [string[]]$Files,
    [string]$Printer,
    [int]$Copies = 1,
    [switch]$Help
)

$ErrorActionPreference = 'Stop'

# Ghi tiếng Việt ra màn hình đúng (5.1 console mặc định không phải UTF-8)
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$ServerUri             = 'http://127.0.0.1:3939/mcp'
$script:SessionId      = $null
$script:McpInitialized = $false
$script:jsonRpcId      = 0

# Ánh xạ lệnh CLI -> tool MCP (tên tool trong docs/MCP.md + src/Printonator.Mcp/PrintTools.cs)
$toolMap = @{
    'list-printers' = 'list_printers'
    'pick-printer'  = 'pick_printer'
    'print-files'   = 'print_files'
    'list-jobs'     = 'list_jobs'
    'job-status'    = 'job_status'
    'cancel-job'    = 'cancel_job'
    'presets'       = 'get_presets'
}

function Show-Usage {
    Write-Output @'
Printonator CLI - điều khiển in qua MCP server (chạy nền trong app Printonator).

Cách dùng:
  .\tools\printonator.ps1 <lệnh> [tham số...]

Lệnh:
  list-printers                        Liệt kê máy in + trạng thái (available/offline, khổ giấy, duplex/màu)
  pick-printer                         Tự chọn máy in phù hợp nhất
  print-files <file1> [file2...]       In hàng loạt file
                                        [-Printer "Tên máy"] [-Copies N]   (mặc định Copies = 1)
  list-jobs                            Xem hàng đợi in
  job-status <jobId>                   Xem chi tiết 1 job (trạng thái, lỗi nếu có)
  cancel-job <jobId>                   Hủy job đang chờ
  presets                              Xem các bộ cấu hình in đã lưu

Ví dụ:
  .\tools\printonator.ps1 list-printers
  .\tools\printonator.ps1 print-files C:\hopdong.docx C:\hopdong2.pdf -Printer "Canon LBP151 (222)" -Copies 2
  .\tools\printonator.ps1 job-status 3f2c9e10-abcd-0000-0000-000000000000

Yêu cầu: app Printonator đang MỞ. Nếu chưa mở, hãy mở app rồi chạy lại.
'@
}

function Fail-Print {
    param([string]$Message, [int]$ExitCode = 1)
    Write-Output ''
    Write-Output "LOI: $Message"
    Write-Output ''
    Write-Output 'Huong dan:'
    Write-Output '  - Khong ket noi duoc MCP? Hay MO app Printonator roi chay lai (MCP server chi chay nền trong app).'
    Write-Output '  - Mo tools/printonator.ps1 hoac docs/MCP.md de xem chi tiet.'
    exit $ExitCode
}

# Chuyển $resp.Headers (WebHeaderCollection trên 5.1, Dictionary trên 7) về hashtable
function Convert-HeadersToHashtable {
    param($Headers)
    $map = @{}
    if ($null -eq $Headers) { return $map }
    if ($Headers -is [System.Net.WebHeaderCollection]) {
        $keys = @($Headers.AllKeys)
    }
    else {
        $keys = @($Headers.Keys)
    }
    foreach ($k in $keys) {
        if ($null -eq $k) { continue }
        $map[[string]$k] = $Headers[[string]$k]
    }
    return $map
}

function Get-HeaderValue {
    param($HeaderMap, [string]$Name)
    $v = $HeaderMap[$Name]
    if ($null -eq $v) { return $null }
    if ($v -is [array]) { return [string]$v[0] }
    return [string]$v
}

function New-JsonRpcId {
    $script:jsonRpcId++
    return $script:jsonRpcId
}

# Gửi 1 request HTTP tới MCP, trả về đối tượng JSON-RPC đã parse (hoặc $null nếu body rỗng).
# Kết nối thất bại / HTTP lỗi -> báo lỗi rõ + exit code khác 0.
function Invoke-McpPost {
    param([string]$BodyJson)
    $headers = @{ 'Accept' = 'application/json, text/event-stream' }
    if ($script:SessionId) { $headers['Mcp-Session-Id'] = $script:SessionId }
    $params = @{
        Uri             = $ServerUri
        Method          = 'Post'
        Body            = $BodyJson
        ContentType     = 'application/json'
        Headers         = $headers
        TimeoutSec      = 30
        UseBasicParsing = $true
    }
    try {
        $resp = Invoke-WebRequest @params
    }
    catch {
        $status = $null
        if ($_.Exception -and $_.Exception.Response) {
            $status = $_.Exception.Response.StatusCode
        }
        if ($status) {
            Fail-Print ("MCP server phản hồi lỗi HTTP {0}. Hãy mở Printonator rồi thử lại." -f $status) 2
        }
        Fail-Print ("Không kết nối được MCP server tại {0}. Hãy mở Printonator rồi thử lại." -f $ServerUri) 2
    }

    $respHeaders = Convert-HeadersToHashtable $resp.Headers
    $sid = Get-HeaderValue $respHeaders 'Mcp-Session-Id'
    if ($sid) { $script:SessionId = $sid }

    if ([string]::IsNullOrWhiteSpace($resp.Content)) { return $null }

    $contentType = Get-HeaderValue $respHeaders 'Content-Type'
    # MCP HTTP có thể trả SSE (text/event-stream) thay vì JSON thuần — lấy dòng data cuối
    if ($contentType -and $contentType -match 'event-stream') {
        $dataLine = ($resp.Content -split "`r?`n") | Where-Object { $_ -like 'data:*' } | Select-Object -Last 1
        $text = [string]$dataLine -replace '^data:\s*', ''
        if ([string]::IsNullOrWhiteSpace($text)) { return $null }
        return $text | ConvertFrom-Json
    }
    return $resp.Content | ConvertFrom-Json
}

# Bọc 1 lệnh JSON-RPC (request có id, hoặc notification không id nếu -Notify)
function Invoke-JsonRpc {
    param([string]$Method, $Params, [switch]$Notify)
    $body = @{ jsonrpc = '2.0'; method = $Method }
    if ($Notify) {
        if ($null -eq $Params) { $Params = @{} }
        $body['params'] = $Params
    }
    else {
        $body['id'] = New-JsonRpcId
        if ($null -ne $Params) { $body['params'] = $Params }
    }
    $json = $body | ConvertTo-Json -Depth 10
    return Invoke-McpPost -BodyJson $json
}

# Handshake MCP: initialize + notifications/initialized (thử 2 phiên bản protocol)
function Initialize-Mcp {
    foreach ($v in @('2025-06-18', '2024-11-05')) {
        $params = @{
            protocolVersion = $v
            capabilities    = @{}
            clientInfo      = @{ name = 'printonator.ps1'; version = '0.1.0' }
        }
        $resp = Invoke-JsonRpc -Method 'initialize' -Params $params
        if ($null -ne $resp -and $null -eq $resp.error) {
            if ($resp.result.protocolVersion) { $script:McpInitialized = $true }
            $script:McpInitialized = $true
            try { Invoke-JsonRpc -Method 'notifications/initialized' -Params @{} -Notify | Out-Null } catch { }
            return $true
        }
        $msg = ''
        if ($resp -and $resp.error) { $msg = [string]$resp.error.message }
        # Chỉ thử phiên bản khác nếu lỗi liên quan tới protocolVersion
        if ($msg -notmatch 'protocol|version') { break }
    }
    Fail-Print ('Không khởi tạo được phiên MCP (initialize): {0}' -f $msg) 1
}

# Gọi 1 tool MCP; tự initialize rồi thử lại 1 lần nếu server báo chưa khởi tạo
function Invoke-McpTool {
    param([string]$Tool, $Arguments)
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        $resp = Invoke-JsonRpc -Method 'tools/call' -Params @{ name = $Tool; arguments = $Arguments }
        if ($null -eq $resp) { Fail-Print 'MCP không trả kết quả.' 1 }
        if ($null -ne $resp.error) {
            $code = $resp.error.code
            $msg  = ''
            if ($resp.error.message) { $msg = [string]$resp.error.message }
            $needsInit = ($code -eq -32002 -or $code -eq -32005) -or ($msg -match 'initialize')
            if ($needsInit -and -not $script:McpInitialized) {
                Initialize-Mcp
                continue
            }
            Fail-Print ("MCP trả lỗi ({0}): {1}" -f $code, $msg) 1
        }
        return $resp
    }
}

# ============ In kết quả thân thiện ============

function Show-Printers([object]$data) {
    $printers = @($data.printers)
    if ($printers.Count -eq 0) { Write-Output 'Không có máy in nào.'; return }
    $i = 0
    foreach ($p in $printers) {
        $i++
        $state = if ($p.available) { 'SẢN SÀNG' } else { 'OFFLINE' }
        $v     = if ($p.virtual)   { '(máy ảo)' } else { '' }
        Write-Output ("{0}. {1}  [{2}] {3}" -f $i, $p.name, $state, $v)
        if ($p.paper) { Write-Output ("   Khổ giấy: {0}" -f ($p.paper -join ', ')) }
        Write-Output ("   Duplex: {0} | Màu: {1}" -f $(if ($p.duplex) { 'có' } else { 'không' }), $(if ($p.color) { 'có' } else { 'không' }))
        if ($p.trays) { Write-Output ("   Khay giấy: {0}" -f ($p.trays -join ', ')) }
        Write-Output ''
    }
}

function Show-PickedPrinter([object]$data) {
    Write-Output ("Máy in được chọn:  {0}" -f $data.printer)
    Write-Output ('  Sẵn sàng: {0} | Máy vật lý: {1} | Máy mặc định: {2}' -f `
        $(if ($data.available) { 'có' } else { 'không' }), `
        $(if ($data.physical) { 'có' } else { 'không' }), `
        $(if ($data.isDefault) { 'có' } else { 'không' }))
    if ($data.reason) { Write-Output ("  Lý do: {0}" -f $data.reason) }
    $cands = @($data.candidates)
    if ($cands.Count -gt 1) {
        Write-Output ''
        Write-Output 'Các máy khác đáp ứng (nếu muốn đổi):'
        foreach ($c in $cands) {
            if ($c.name -eq $data.printer) { continue }
            $st = if ($c.available) { 'sẵn sàng' } else { 'offline' }
            Write-Output ('  - {0}  ({1}{2})' -f $c.name, $st, $(if ($c.virtual) { ', máy ảo' } else { '' }))
        }
    }
}

function Show-PrintFiles([object]$data) {
    if ($data.pendingApproval) {
        Write-Output 'Đã đưa các file vào hàng đợi CHỜ DUYỆT (chưa in).'
        Write-Output ("  Số trang ước tính: {0}" -f $data.estimatedPages)
        if ($data.jobIds) { Write-Output ("  job_id: {0}" -f ($data.jobIds -join ', ')) }
        if ($data.note)   { Write-Output ("  Lưu ý: {0}" -f $data.note) }
        return
    }
    Write-Output 'Đã đưa các file vào hàng đợi in.'
    if ($data.jobIds)        { Write-Output ("  job_id: {0}" -f ($data.jobIds -join ', ')) }
    if ($data.estimatedPages){ Write-Output ("  Số trang ước tính: {0}" -f $data.estimatedPages) }
    if ($data.printer)       { Write-Output ("  Máy in: {0}" -f $data.printer) }
    Write-Output ''
    Write-Output 'Theo dõi: .\tools\printonator.ps1 job-status <jobId>'
}

function Show-Jobs([object]$data) {
    $jobs = @($data.jobs)
    if ($jobs.Count -eq 0) { Write-Output 'Hàng đợi trống.'; return }
    foreach ($j in $jobs) {
        Write-Output ("{0}  [{1}]  {2}  (trang {3} x {4} bản)" -f $j.id, $j.state, $j.fileName, $j.pages, $j.copies)
        if ($j.printer) { Write-Output ("  Máy in: {0}" -f $j.printer) }
        if ($j.error)   { Write-Output ("  Lỗi: {0} — {1}  (Gợi ý: {2})" -f $j.error.code, $j.error.message, $j.error.hint) }
        Write-Output ''
    }
}

function Show-Job([object]$data) {
    $j = $data.job
    if ($null -eq $j) { Write-Output 'Không có thông tin job.'; return }
    Write-Output ("job_id:      {0}" -f $j.id)
    Write-Output ("File:        {0}  [{1}]" -f $j.fileName, $j.format)
    Write-Output ("Trạng thái:  {0}" -f $j.state)
    Write-Output ('Trang:       {0} | Bản: {1} | 2 mặt: {2}' -f $j.pages, $j.copies, $(if ($j.duplex) { 'có' } else { 'không' }))
    if ($j.paper)   { Write-Output ("Khổ giấy:    {0}" -f $j.paper) }
    if ($j.printer) { Write-Output ("Máy in:      {0}" -f $j.printer) }
    if ($j.error) {
        Write-Output ''
        Write-Output ("Lỗi: {0}" -f $j.error.message)
        if ($j.error.code)           { Write-Output ("  Mã lỗi:   {0}" -f $j.error.code) }
        if ($j.error.hint)           { Write-Output ("  Gợi ý:    {0}" -f $j.error.hint) }
        if ($j.error.suggestedAction){ Write-Output ("  Nên làm:  {0}" -f $j.error.suggestedAction) }
    }
}

function Show-CancelJob([object]$data) {
    Write-Output ("job {0} — đã hủy. Trạng thái hiện tại: {1}" -f $data.jobId, $data.state)
}

function Show-Presets([object]$data) {
    $presets = @($data.presets)
    if ($presets.Count -eq 0) { Write-Output 'Chưa có preset nào.'; return }
    foreach ($p in $presets) {
        Write-Output ("- {0}" -f $p.name)
        Write-Output ('    Bản: {0} | 2 mặt: {1} | Khổ giấy: {2} | Màu: {3}' -f $p.copies, $(if ($p.duplex) { 'có' } else { 'không' }), $p.paper, $p.colorMode)
        if ($p.printer) { Write-Output ("    Máy in: {0}" -f $p.printer) }
        Write-Output ''
    }
}

# Parse response JSON-RPC, tách chuỗi JSON của tool (content[].text) rồi in thân thiện
function Show-ToolResult {
    param($Resp, [string]$Command)
    if ($null -eq $Resp) { Fail-Print 'MCP không trả kết quả.' 1 }

    $result = $Resp.result
    if ($null -eq $result) { Fail-Print 'MCP trả về result rỗng.' 1 }

    $text = $null
    foreach ($item in @($result.content)) {
        if ($item.type -eq 'text' -and -not [string]::IsNullOrWhiteSpace([string]$item.text)) {
            $text = [string]$item.text
        }
    }
    if ($null -eq $text) {
        if ($result.isError) { Fail-Print 'MCP tool báo lỗi nhưng không có chi tiết.' 1 }
        Write-Output '(không có kết quả trả về)'
        return
    }

    $data = $null
    try { $data = $text | ConvertFrom-Json } catch { }
    if ($null -eq $data) { Write-Output $text; return }

    if ($null -ne $data.error -or $false -eq $data.ok) {
        $err = $data.error
        if ($null -ne $err) {
            Write-Output ''
            if ($err.message) { Write-Output ("LOI: {0}" -f $err.message) } else { Write-Output 'LOI: không rõ nguyên nhân.' }
            if ($err.code) { Write-Output ("  Mã lỗi: {0}" -f $err.code) }
            if ($err.hint) { Write-Output ("  Gợi ý:  {0}" -f $err.hint) }
        }
        else {
            Write-Output 'LOI: không rõ nguyên nhân.'
        }
        exit 1
    }

    Write-Output ''
    switch ($Command) {
        'list-printers' { Show-Printers $data }
        'pick-printer'  { Show-PickedPrinter $data }
        'print-files'   { Show-PrintFiles $data }
        'list-jobs'     { Show-Jobs $data }
        'job-status'    { Show-Job $data }
        'cancel-job'    { Show-CancelJob $data }
        'presets'       { Show-Presets $data }
        default         { Write-Output $text }
    }
}

function Get-JobId {
    if (-not $Files -or $Files.Count -eq 0) {
        Write-Output 'Lệnh này cần jobId. Xem danh sách: .\tools\printonator.ps1 list-jobs'
        exit 2
    }
    return [string]$Files[0]
}

# ============ Điều phối chính ============

function Main {
    if ($Help -or [string]::IsNullOrWhiteSpace($Command)) {
        Show-Usage
        exit 0
    }

    $tool = $toolMap[$Command]
    if (-not $tool) {
        Write-Output "Không biết lệnh '$Command' — xem hướng dẫn bên dưới."
        Write-Output ''
        Show-Usage
        exit 2
    }

    $arguments = @{}
    switch ($Command) {
        'print-files' {
            if (-not $Files -or $Files.Count -eq 0) {
                Write-Output 'print-files cần ít nhất 1 file.'
                Write-Output 'Ví dụ: .\tools\printonator.ps1 print-files C:\hopdong.docx -Printer "Canon LBP151 (222)"'
                exit 2
            }
            $arguments['paths'] = @($Files | ForEach-Object { [string]$_ })
            if ($Printer) { $arguments['printer'] = $Printer }
            if ($Copies -ne 1) { $arguments['copies'] = $Copies }
        }
        'job-status' { $arguments['jobId'] = Get-JobId }
        'cancel-job' { $arguments['jobId'] = Get-JobId }
    }

    $resp = Invoke-McpTool -Tool $tool -Arguments $arguments
    Show-ToolResult -Resp $resp -Command $Command
}

Main