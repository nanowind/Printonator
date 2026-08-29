---
name: printonator-mcp
version: 0.1.8
description: Hướng dẫn dùng MCP server Printonator để AI in file từ câu lệnh (liệt kê máy in, tự chọn máy, in hàng loạt word/excel/ppt qua app gốc, preset, duyệt job, xem/hủy job, tra cứu mã lỗi). Dùng khi người dùng yêu cầu "in giùm", "in file", "in máy nào rảnh", hỏi máy in, hoặc in một lô tài liệu. Kèm cấu hình env PRINTONATOR_* an toàn. Penpot trong repo chỉ là nguồn thiết kế UI (docs/COMPARISON_PENPOT.md), không liên quan in.
allowed-tools:
  - list_printers
  - pick_printer
  - print_files
  - print_with_preset
  - get_presets
  - save_preset
  - approve_job
  - reject_job
  - list_jobs
  - job_status
  - cancel_job
  - get_guard_config
  - get_error_reference
---

# Printonator MCP — AI in file giùm

MCP server để AI in file thay người dùng bằng tiếng Việt tự nhiên. AI KHÔNG cần biết tên máy — có thể bảo "in máy nào rảnh thì in".

## 1. Server hoạt động thế nào

- **2 transport**: HTTP `http://127.0.0.1:3939/mcp` (loopback — chỉ máy này) hoặc stdio (`--stdio`, cho Claude Code/Desktop).
- **Envelope contract**: mọi tool trả `{ok:true, ...}` hoặc `{ok:false, error:{code, category, message, hint, suggestedAction?}}`. Không bao giờ ném exception, không lộ đường dẫn/Detail cho AI.
- **Trạng thái job**: `Queued` → `Converting` → `Spooling` → `Done` / `Error`. Job từ AI khi cần duyệt nằm ở `AwaitingApproval`.

## 2. Chạy server

```powershell
dotnet build Printonator.sln
dotnet run --project src/Printonator.Mcp            # HTTP http://127.0.0.1:3939/mcp
Printonator.Mcp.exe --stdio                          # stdio (cho Claude Code/Desktop)
```

## 3. Cấu hình an toàn (BẮT BUỘC nắm)

Đọc từ env (`PRINTONATOR_*`) hoặc file `PRINTONATOR_GUARD_FILE` lúc khởi động. **Fail-closed**: thiếu/hỏng → an toàn, không tự mở.

| Env | Mặc định | Ý nghĩa |
|---|---|---|
| `PRINTONATOR_REQUIRE_APPROVE` | `true` | `true` → job AI vào trạng thái chờ duyệt, phải `approve_job` mới in |
| `PRINTONATOR_ALLOWED_PRINTERS` | rỗng | Tên máy được phép, phân cách dấu phẩy (vd `"HP LaserJet Pro M404,Canon LBP"`). Rỗng + không duyệt = cấm tự in |
| `PRINTONATOR_MAX_PAGES_PER_BATCH` | `200` | Giới hạn trang/lô (file chưa rõ trang tính 50 trang/file) |
| `PRINTONATOR_MAX_FILES_PER_BATCH` | `50` | Giới hạn file/lô |
| `PRINTONATOR_MAX_COPIES_PER_FILE` | `100` | Giới hạn bản/file (chống AI in 999 bản) |
| `PRINTONATOR_AUDIT_LOG` | `%APPDATA%\Printonator\audit.log` | Nhật ký AI in (chỉ field an toàn) |

**Hai kịch bản chính:**
- **Mặc định (fail-closed)**: `REQUIRE_APPROVE=true` → AI nhập lệnh in → jobs chờ duyệt → AI/người duyệt `approve_job`. An toàn nhất.
- **AI tự in (tự động hoàn toàn)**: AI tự chọn máy + in, không cần người duyệt:
  ```powershell
  $env:PRINTONATOR_REQUIRE_APPROVE="false"
  $env:PRINTONATOR_ALLOWED_PRINTERS="HP LaserJet Pro M404,Canon LBP151 (222)"
  dotnet run --project src/Printonator.Mcp
  ```

## 4. Tools (13) — signatures

- `list_printers()` → máy in + available/paper/duplex/color/trays/virtual.
- `pick_printer(paper?, requireDuplex?, requireColor?)` — tự chọn máy tốt nhất (máy vật lý available trước máy ảo).
- `print_files(paths, printer?, copies?, duplex?, paper?, pageRange?, colorMode?, paperSource?, scaleMode?, pagesPerSheet?, parity?, quality?)` — in lô; `printer` bỏ trống = tự chọn máy (nếu có allowlist).
- `print_with_preset(presetName, paths, printer?)` — in theo preset đã lưu.
- `get_presets()` / `save_preset(name, ...)` — xem/lưu cấu hình in dùng lại.
- `approve_job(jobId)` / `reject_job(jobId)` — duyệt/từ chối job đang chờ duyệt.
- `list_jobs(status?)` / `job_status(jobId)` — xem hàng đợi / 1 job (state, error + suggestedAction).
- `cancel_job(jobId)` — hủy job đang chờ.
- `get_guard_config()` — cấu hình an toàn hiện tại (AI có tự in được không).
- `get_error_reference(code?)` — tra cứu bảng mã lỗi + cách AI xử lý.

## 5. AI workflow chuẩn (auto-print từ prompt)

1. `get_guard_config` → nếu `canAutoPrint:false` thì giải thích cho người dùng / nhờ cấu hình (xem §3).
2. `pick_printer` (hoặc `print_files`) với `printer` bỏ trống → máy vật lý rảnh phù hợp.
3. `print_files(paths, printer?, duplex?, paper?, ...)` → nhận `jobIds`.
4. Poll `job_status` / `list_jobs` mỗi ~1–2s cho tới `Done` hoặc `Error`.
5. Nếu lỗi → `get_error_reference <code>` → làm theo `aiAction` → thử lại.
6. Nếu `pendingApproval:true` → `list_jobs status=awaitingapproval` + `approve_job`.

## 6. Error handling & recovery

Mọi lỗi đều có `{ok:false, error:{code, message, hint, suggestedAction}}`. Đọc `error.hint` và hành động theo `error.suggestedAction` / `get_error_reference`. Bảng đầy đủ lấy từ `get_error_reference` — không cần nhớ. Vài cái hay gặp:

| Mã | AI làm gì |
|---|---|
| `PRINTER_OFFLINE` | `list_printers` xem máy available; `pick_printer` chọn máy khác, in lại |
| `PRINTER_NO_PERMISSION` | `get_guard_config` xem allowlist; chọn máy trong danh sách hoặc báo người dùng thêm `PRINTONATOR_ALLOWED_PRINTERS` |
| `APPROVAL_REQUIRED` / `pendingApproval` | `list_jobs status=awaitingapproval` + `approve_job` |
| `JOB_NOT_FOUND` | `list_jobs` lấy job_id đúng, thử lại |
| `MAX_BATCH_EXCEEDED` | Chia nhỏ lô (giảm số file/trang/bản) |
| `SPOOLER_BUSY` | Chờ vài giây, `job_status` lại |

## 7. Approval flow (khi REQUIRE_APPROVE=true)

AI gửi `print_files` → trả `{ok:true, pendingApproval:true, jobIds, note}` (jobs ở `AwaitingApproval`). Tiếp `list_jobs status=awaitingapproval` → `approve_job(jobId)` (in) hoặc `reject_job(jobId)` (hủy). Job được duyệt → `Queued` → in.

## 8. Ví dụ (tiếng Việt)

> **Người dùng:** "In file này 2 mặt khổ A4, máy nào rảnh thì in" (file: C:\hopdong.docx)
>
> AI gọi: `get_guard_config` → `{canAutoPrint:true}` · `list_printers` → Canon LBP rảnh (available, A4, duplex) · `print_files(paths:["C:\hopdong.docx"], printer:"Canon LBP151 (222)", duplex:true, paper:"A4")` → `{ok:true, jobIds:[...], printer:"Canon LBP151 (222)"}` · `job_status` sau ~1-2s → `{state:"Done"}` → báo: "Đã in xong 2 mặt A4 vào Canon LBP151 (222)."
>
> **Variant lỗi:** `print_files` → `{error:{code:"SPOOLER_BUSY", hint:"..."}}` → AI chờ vài giây → `job_status` lại → in xong.

## 9. Cấu hình client

- **Claude Code**: `claude mcp add printonator -- <đường dẫn>\Printonator.Mcp.exe --stdio`
- **Kilo / VS Code**: `.mcp.json` user-scope hoặc `kilo.json`, `command:[..., "--stdio"]`, khuyến nghị `environment: {"PRINTONATOR_REQUIRE_APPROVE":"false", "PRINTONATOR_ALLOWED_PRINTERS":"..."}`.
- **Claude Desktop**: `%APPDATA%\Claude\claude_desktop_config.json` (stdio).
- ⚠️ KHÔNG commit entry trỏ đường dẫn tuyệt đối vào `.mcp.json` của repo.

## 10. Kiểm thử nhanh

```powershell
dotnet test tests/Printonator.Core.Tests      # 78
dotnet test tests/Printonator.Mcp.Tests       # 6 (tool shape, error reference, pick)
dotnet test tests/Printonator.Spool.Tests     # 4 (E2E print-to-PDF)
```