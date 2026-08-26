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

## 2. Tools (8)

| Tool | Mô tả |
|---|---|
| `list_printers` | Máy in + trạng thái (available/offline), khổ giấy, duplex/màu, khay giấy, máy ảo |
| `print_files` | In hàng loạt file (paths, printer, copies, duplex, paper, pageRange, color) → job_ids + ước lượng trang |
| `print_with_preset` | In theo preset đã lưu (presetName, paths, printer?) |
| `get_presets` / `save_preset` | Xem / lưu bộ cấu hình in tái sử dụng |
| `list_jobs` / `job_status` | Hàng đợi đầy đủ (id, state, error có code/message/hint) |
| `cancel_job` | Hủy job đang chờ (Queued) |

> Engine in: file Office (DOCX/XLSX/PPTX) in bằng **app gốc trên máy user** (Word/Excel/PowerPoint COM,
> như Print Conductor) → fallback shell "printto" khi không có app đó hoặc định dạng khác (PDF, ảnh...).
> Xem `src/Printonator.Spool/Printing/OfficeComPrintEngine.cs`.

Mọi tool trả JSON `{ok:true, ...}` hoặc `{ok:false, error:{code, category, message, hint}}` — không ném,
không lộ đường dẫn/Detail cho AI.

## 3. An toàn (PrintGuard) — fail-closed

| Biến env / config | Mặc định | Ý nghĩa |
|---|---|---|
| `PRINTONATOR_REQUIRE_APPROVE` | `true` | Job từ AI phải duyệt mới in. **Standalone không có màn duyệt → nếu true, `print_files` trả lỗi APPROVAL_REQUIRED.** Muốn AI tự in: đặt `false` + allowlist |
| `PRINTONATOR_ALLOWED_PRINTERS` | rỗng | Chỉ in vào các máy này (phân tách `,`). **Rỗng + không duyệt = từ chối khởi hành (fail-closed)** |
| `PRINTONATOR_MAX_PAGES_PER_BATCH` | `200` | Trang/lô (cộng dồn cả hàng đợi). File chưa probe số trang = ngân sách bảo thủ 50 trang |
| `PRINTONATOR_MAX_FILES_PER_BATCH` | `50` | Số file/lô |
| `PRINTONATOR_MAX_COPIES_PER_FILE` | `100` | Bản in tối đa/file (chống in 999 bản) |
| `PRINTONATOR_GUARD_FILE` | — | File JSON cấu hình McpGuardConfig (khi có màn Settings) |
| `PRINTONATOR_AUDIT_LOG` | `%APPDATA%\Printonator\audit.log` | Audit JSON lines, chỉ ghi whitelist field (không lộ path/secret) |

Ví dụ tự in:
```bash
$env:PRINTONATOR_REQUIRE_APPROVE="false"
$env:PRINTONATOR_ALLOWED_PRINTERS="Canon LBP151 (222),Microsoft Print to PDF"
$env:PRINTONATOR_MAX_PAGES_PER_BATCH="300"
dotnet run --project src/Printonator.Mcp
```

> Roadmap — approve thật: host MCP **in-process trong UI** (cùng PrintQueue), nút duyệt trên MainWindow
> cho job `Source=Mcp, State=AwaitingApproval` (Core đã có `ApproveJob`/`RejectJob` + test).

## 4. Penpot — CHỈ LÀ NGUỒN THIẾT KẾ (không phải đối tượng in)

Penpot không liên quan tới in. Bản thiết kế UI của app đã vẽ sẵn trên Penpot (self-host) — **dùng MCP của Penpot
để ĐỌC cấu trúc/text shapes** rồi dựng app bám theo. Xem `docs/COMPARISON_PENPOT.md` (trích xuất từng element).

> Bản trước có tool `penpot_print_file` (in board) — đÃ bỏ vì sai vai trò; Penpot chỉ để đọc design,
> không phải nguồn file in.

## 5. Luồng job (trạng thái)

```
User(UI)  → Queued → Converting → Spooling → Done / Error(reason code + tiếng Việt)
Mcp + approve on → AwaitingApproval → (ApproveJob) → Queued → ... (in-process UI, roadmap)
Mcp approve off → Enqueue thẳng như User
```

## 6. Kiểm thử nhanh

```bash
dotnet build Printonator.sln
dotnet test tests/Printonator.Core.Tests      # 52  (gồm PrintGuard/Preset/Approve/Queue)
dotnet test tests/Printonator.UITests         # 4   (FlaUI, launch app thật)
```