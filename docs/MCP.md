# Printonator — MCP Server ("AI in giùm")

> Tài liệu dùng cho AI client (Claude Code/Desktop, Hermes...) kết nối Printonator để in file thay người.
> Snapshot 2026-08-25 — code thật tại `src/Printonator.Mcp` + `src/Printonator.Spool`.

## 1. Chạy server

```bash
# HTTP (endpoint http://127.0.0.1:3939/mcp) — dành cho client hỗ trợ streamable HTTP
dotnet run --project src/Printonator.Mcp

# stdio — client spawn trực tiếp (Claude Code: claude mcp add printonator -- <path>\Printonator.Mcp.exe --stdio)
dotnet run --project src/Printonator.Mcp -- --stdio
```

- **Chỉ bind loopback** (`127.0.0.1`), không CORS — người khác trong LAN không ra lệnh in được.

## Cấu hình client (đăng ký server)

**Claude Code (stdio):**
```bash
claude mcp add printonator -t stdio -- <đường_dẫn>\Printonator.Mcp.exe --stdio
```

**Kilo (project/global `kilo.json` → `mcp`):**
```jsonc
{ "mcp": { "printonator": {
    "type": "local",
    "command": ["<đường_dẫn>\\Printonator.Mcp.exe", "--stdio"],
    "environment": { "PRINTONATOR_REQUIRE_APPROVE": "false", "PRINTONATOR_ALLOWED_PRINTERS": "Microsoft Print to PDF" }
} } }
```

**Claude Desktop (`%APPDATA%\Claude\claude_desktop_config.json`):**
```json
{ "mcpServers": { "printonator": { "command": "C:\\...\\Printonator.Mcp.exe", "args": ["--stdio"], "type": "stdio" } } }
```

**Client dùng `.mcp.json`** (VS Code / nhiều IDE): thêm entry `{ "command": "...", "args": ["--stdio"], "type": "stdio" }`.
> ⚠️ **Đừng commit** entry trỏ đường dẫn tuyệt đối vào `.mcp.json` của repo — máy khác/CI sẽ vỡ. Bỏ vào user-scope hoặc docs như file này.

## 2. Tools (13)

| Tool | Mô tả |
|---|---|
| `list_printers` | Máy in + trạng thái (available/offline), khổ giấy, duplex/màu, khay giấy, máy ảo |
| `pick_printer` | Tự chọn máy in tốt nhất: máy vật lý available trước máy ảo; lọc theo khổ/duplex/màu. AI không biết in máy nào → dùng tool này |
| `print_files` | In hàng loạt file (paths, printer?, copies, duplex, paper, pageRange, colorMode, paperSource, scaleMode, pagesPerSheet, parity, quality) → job_ids + ước lượng trang. **Bỏ trống `printer` = tự chọn máy vật lý sẵn sàng** (khi có allowlist) |
| `print_with_preset` | In theo preset đã lưu (presetName, paths, printer?) |
| `get_presets` / `save_preset` | Xem / lưu bộ cấu hình in tái sử dụng |
| `approve_job` / `reject_job` | Duyệt / từ chối job đang chờ duyệt (state=awaitingapproval) |
| `list_jobs` / `job_status` | Hàng đợi đầy đủ (id, state, error có code/message/hint/suggestedAction) |
| `cancel_job` | Hủy job đang chờ (Queued) |
| `get_guard_config` | Xem cấu hình an toàn đang áp dụng (AI có tự in được không) |
| `get_error_reference` | Tra cứu bảng mã lỗi → nghĩa + AI nên làm gì (xem §4) |

> Engine in: file Office (DOCX/XLSX/PPTX) in bằng **app gốc trên máy user** (Word/Excel/PowerPoint COM,
> như Print Conductor) → fallback shell "printto" khi không có app đó hoặc định dạng khác (PDF, ảnh...).
> Xem `src/Printonator.Spool/Printing/OfficeComPrintEngine.cs`.

Mọi tool trả JSON `{ok:true, ...}` hoặc `{ok:false, error:{code, category, message, hint, suggestedAction}}` —
không ném, không lộ đường dẫn/Detail cho AI.

## 3. An toàn (PrintGuard) — fail-closed

| Biến env / config | Mặc định | Ý nghĩa |
|---|---|---|
| `PRINTONATOR_REQUIRE_APPROVE` | `true` | Job từ AI phải duyệt mới in. **Standalone không có màn duyệt → nếu true, `print_files` trả lỗi APPROVAL_REQUIRED.** Muốn AI tự in: đặt `false` + allowlist |
| `PRINTONATOR_ALLOWED_PRINTERS` | rỗng | Chỉ in vào các máy này (phân tách `,`). **Rỗng + không duyệt = từ chối khởi hành (fail-closed)** |
| `PRINTONATOR_MAX_PAGES_PER_BATCH` | `200` | Trang/lô (cộng dồn cả hàng đợi). File chưa probe số trang = ngân sách bảo thủ 50 trang |
| `PRINTONATOR_MAX_FILES_PER_BATCH` | `50` | Số file/lô |
| `PRINTONATOR_MAX_COPIES_PER_FILE` | `100` | Bản in tối đa/file (chống in 999 bản) |
| `PRINTONATOR_GUARD_FILE` | — | File JSON cấu hình McpGuardConfig (thay env) |
| `PRINTONATOR_AUDIT_LOG` | `%APPDATA%\Printonator\audit.log` | Audit JSON lines, chỉ ghi whitelist field (không lộ path/secret) |

Ví dụ tự in:
```bash
$env:PRINTONATOR_REQUIRE_APPROVE="false"
$env:PRINTONATOR_ALLOWED_PRINTERS="Canon LBP151 (222),Microsoft Print to PDF"
$env:PRINTONATOR_MAX_PAGES_PER_BATCH="300"
dotnet run --project src/Printonator.Mcp
```

**Duyệt lệnh in qua MCP** (khi `REQUIRE_APPROVE=true`, mặc định): `print_files` không trả lỗi nữa —
jobs vào trạng thái `AwaitingApproval`, tool trả `{ok:true, pendingApproval:true, jobIds}`. Duyệt bằng
`approve_job(jobId)` (cho in) hoặc `reject_job(jobId)` (từ chối → Cancelled). Chỉ duyệt được job nguồn AI
(`Source=Mcp`) đang `AwaitingApproval`; xem hàng chờ duyệt bằng `list_jobs status=awaitingapproval`.

## 3b. Bảng mã lỗi (dùng được cho AI)

Tra cứu động: `get_error_reference` — gọi khi gặp `{ok:false, error:{code}}`. Bảng đủ mọi mã với cột
"AI nên làm gì". Một số hay gặp:

| Mã | Nghĩa | AI nên làm |
|---|---|---|
| `PRINTER_OFFLINE` | Máy offline | `list_printers` xem available; `pick_printer` chọn máy khác, in lại |
| `PRINTER_NO_PERMISSION` | Máy ngoài allowlist | `get_guard_config` xem allowlist; chọn máy trong đó hoặc báo người dùng thêm `PRINTONATOR_ALLOWED_PRINTERS` |
| `APPROVAL_REQUIRED` / `pendingApproval` | Cần duyệt | `list_jobs status=awaitingapproval` + `approve_job` |
| `JOB_NOT_FOUND` | job_id không có | `list_jobs` lấy đúng job_id |
| `MAX_BATCH_EXCEEDED` | Vượt giới hạn | Chia nhỏ lô |
| `SPOOLER_BUSY` | Máy/queue bận | Chờ vài giây, thử lại |

## 3c. AI workflow chuẩn (auto in từ prompt)

1. `get_guard_config` → `canAutoPrint:false` thì nhờ người dùng cấu hình, ngừng.
2. `pick_printer` (hoặc `print_files` bỏ trống `printer`) → chọn máy vật lý rảnh.
3. `print_files(paths, printer?, duplex?, paper?, ...)` → `jobIds`.
4. Poll `job_status` mỗi ~1–2s đến khi `Done`/`Error`.
5. Lỗi → `get_error_reference <code>` → làm theo `aiAction` → thử lại.

Ví dụ:

> **Người dùng:** "In file này 2 mặt khổ A4, máy nào rảnh thì in"
>
> AI: `list_printers` → Canon LBP rảnh · `print_files(paths:["C:\hopdong.docx"], printer:"Canon LBP151 (222)", duplex:true, paper:"A4")` → `{ok:true, jobIds}` · `job_status` → `{state:"Done"}` → **"Đã in xong 2 mặt A4 vào Canon LBP151 (222)."**

## 4. Penpot — CHỈ LÀ NGUỒN THIẾT KẾ (không phải đối tượng in)

Penpot không liên quan tới in. Bản thiết kế UI của app đã vẽ sẵn trên Penpot (self-host) — **dùng MCP của Penpot
để ĐỌC cấu trúc/text shapes** rồi dựng app bám theo. Xem `docs/COMPARISON_PENPOT.md` (trích xuất từng element).

> Bản trước có tool `penpot_print_file` (in board) — đÃ bỏ vì sai vai trò; Penpot chỉ để đọc design,
> không phải nguồn file in.

## 5. Luồng job (trạng thái)

```
User(UI)  → Queued → Converting → Spooling → Done / Error(reason code + tiếng Việt)
Mcp + approve on → AwaitingApproval → (approve_job) → Queued → ...
Mcp approve off → Enqueue thẳng như User
```

## 6. Kiểm thử nhanh

```bash
dotnet build Printonator.sln
dotnet test tests/Printonator.Core.Tests      # 78  (PrintGuard/Preset/Approve/Queue/error-routing)
dotnet test tests/Printonator.Mcp.Tests       # 6   (tool shape, error reference, pick printer)
dotnet test tests/Printonator.Spool.Tests     # 4   (E2E print-to-PDF)
```