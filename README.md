# Printonator

**In hàng loạt (bulk printing) cho Windows 10/11** — kéo thả N file, chọn máy in, cấu hình 1 lần, in cả loạt — **hoặc nhờ AI in giùm qua MCP**. C#/.NET 8, WPF, MIT.

![Logo](printonatorLogo.png)

> 100% local — không cloud, không telemetry. Dữ liệu in của bạn không rời máy.

---

## Tính năng chính

- **DROP → SELECT → IN**: kéo & thả / Ctrl+V / "+ Add files" nhiều file (PDF, DOCX/XLSX/PPTX, ảnh, TXT…).
- **Cấu hình từng file hoặc cả nhóm**: số bản, 2 mặt/1 mặt, khổ giấy, màu — qua Bulk bar "Apply to selection".
- **Page range section-aware (DOCX)**: `All`, `2,5`, `3-4`, `1-2,7`, `S2:1-3` — preview trực tiếp "→ Will print physical pages".
- **Máy in thật**: danh sách + trạng thái (available/offline/error), khổ giấy, duplex/màu/khay — màn "Printers & paper setup" riêng + Scan.
- **Theo dõi chính xác**: progress bar + % (footer + taskbar), cột trạng thái (✓ Done / lỗi), toast thông báo, "Print all (N)".
- **Lỗi có mã + tiếng Việt + gợi ý**: `PRINTER_OFFLINE`, `INVALID_PAGE_RANGE`… không bao giờ nuốt lỗi.
- **AI in giùm (MCP)**: Claude/Hermes/assistant tự in theo câu lệnh — có allowlist, giới hạn trang/lô, duyệt, audit.
- **In bằng chính app gốc của máy user** (như Print Conductor): DOCX/XLSX/PPTX → Word/Excel/PowerPoint **COM** (giữ đúng page setup/section của file) → fallback shell "printto".
- **Preset**: lưu/tái dùng bộ cấu hình ("Hợp đồng 2 mặt"…) qua MCP.
- **Tìm kiếm & sắp xếp** danh sách job theo tên/trạng thái/cấu hình.
- **Thiết kế chuẩn**: UI dựng theo bản thiết kế vẽ sẵn trên Penpot (xem docs/COMPARISON_PENPOT.md).

## Quick start

```bash
dotnet build Printonator.sln          # build toàn bộ
dotnet run --project src/Printonator.UI   # chạy app WPF

dotnet test Printonator.sln           # 52 Core + 4 UI tests

# MCP server ("AI in giùm") — 9 tools trên http://127.0.0.1:3939/mcp
dotnet run --project src/Printonator.Mcp
```

## MCP — "AI in giùm"

| Item | Chi tiết |
|---|---|
| Server | `src/Printonator.Mcp` — HTTP `:3939/mcp` (mặc định) hoặc `--stdio`; **chỉ loopback, không CORS** |
| Tools (8) | `list_printers`, `print_files`, `print_with_preset`, `get/save_preset`, `list_jobs`, `job_status`, `cancel_job` |
| An toàn | `PRINTONATOR_REQUIRE_APPROVE` (mặc định `true` — AI không tự in), `PRINTONATOR_ALLOWED_PRINTERS`, `MAX_PAGES_PER_BATCH=200`, `MAX_COPIES_PER_FILE=100`, audit JSON — **fail-closed** |
| Engine | Office (DOCX/XLSX/PPTX) in qua Word/Excel/PowerPoint COM; không có app đó hoặc file khác → shell fallback |

Hướng dẫn chi tiết: **[docs/MCP.md](docs/MCP.md)** · Skill agent: `.kilo/skills/printonator-mcp/SKILL.md`

Ví dụ nhanh (PowerShell) — cho phép AI tự in vào 1 máy:
```powershell
$env:PRINTONATOR_REQUIRE_APPROVE="false"
$env:PRINTONATOR_ALLOWED_PRINTERS="Microsoft Print to PDF"
dotnet run --project src/Printonator.Mcp
```

## Kiến trúc (6 projects)

```
src/
├─ Printonator.Core     # Queue engine, state machine, PresetStore (profile), PrintGuard (an toàn AI in)
├─ Printonator.Spool    # Windows Spooler/Printer: PrinterService + engine dynamic + native driver dialogs
├─ Printonator.Mcp      # MCP server: 8 tools + guard + page-count probe
└─ Printonator.UI       # WPF: job table, Print Settings (2 cột), search/sort, toast, progress, Printer Config
tests/
├─ Printonator.Core.Tests  # 62 tests (page-range, section, queue, approve, preset, guard, print-config)
└─ Printonator.UITests     # 27 tests (FlaUI app thật + engine dynamic: LibreOffice locator, browser CDP params, PDF slicing)
```

Trạng thái engine in — **dynamic theo máy user, KHÔNG bundle thư viện (app nhẹ)**:
1. **MS Office (COM)** — Word/Excel/PowerPoint có sẵn → in đúng page setup/section như Print Conductor;
2. **LibreOffice** — nếu máy KHÔNG có MS Office nhưng CÓ LibreOffice (soffice) → `soffice --headless --pt`;
3. **Browser render (Chrome/Edge headless → CDP printToPDF)** — PDF/ảnh/TXT: áp THẬT page range (non-PDF),
   scale mode, khổ giấy, chiều ngang; máy nào cũng có browser;
4. **Shell "printto"** — fallback cuối (in nhanh "như mặc định" — không render, không mọc Chrome).

Bảng **Print settings** mới: page range (All/Ranges), color mode, khay giấy, scale mode, N-up, profile — kèm nút mở
**Printing Preferences / Printer Properties** native của driver (printui.dll). Chi tiết đối chiếu: [docs/COMPARISON_PRINT_CONDUCTOR.md](docs/COMPARISON_PRINT_CONDUCTOR.md).

## Tài liệu

| File | Nội dung |
|---|---|
| [CONCEPT.md](CONCEPT.md) | Tầm nhìn & thiết kế trừu tượng (MIT, MCP AI-native, guard) |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Kiến trúc + hướng dẫn chuyển giao cho AI/dev mới |
| [docs/MCP.md](docs/MCP.md) | Hướng dẫn dùng + cấu hình MCP server |
| [docs/COMPARISON_PENPOT.md](docs/COMPARISON_PENPOT.md) | Đối chiếu với thiết kế Penpot (element gap) |
| [docs/COMPARISON_PRINT_CONDUCTOR.md](docs/COMPARISON_PRINT_CONDUCTOR.md) | Đối chiếu với Print Conductor (gap + roadmap) |
| [docs/DESIGN_SYSTEM.md](docs/DESIGN_SYSTEM.md) | Design system Notion-style cho UI |

## Roadmap (ưu tiên)

1. **P0**: Engine render PDF slicing thật (cắt page range PDF đúng — PDF viewer browser từ chối ranges; cần lib/dịch vụ nhẹ) → Màn MCP/Safety UI + nút duyệt job `AwaitingApproval` (host MCP in-process trong app)
2. **v1.x**: Cover/Report page → Gộp PDF single-job → CLI + preset export → Per-file printer
3. **v2**: Watermark → Post-processing → Watch folder → Email/CAD/HEIC → Security Warning (clone detection) → i18n

## Giấy phép

MIT — miễn phí, mã nguồn mở. Xem [LICENSE](LICENSE).