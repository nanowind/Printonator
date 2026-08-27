# Printonator

**In hàng loạt cho Windows 10/11** — kéo thả N file, chọn máy in, cấu hình một lần, in cả loạt — hoặc nhờ AI in giùm qua MCP. C#/.NET 8, WPF, **100% local**, MIT.

![Logo](printonatorLogo.png)

**Local-first**: không cloud, không telemetry — dữ liệu in không rời máy.  
**Miễn phí & mã nguồn mở** — xem [LICENSE](LICENSE).

---

## Vì sao Printonator?

In hàng loạt bằng tay là khổ: mở từng file → Ctrl+P → chọn máy → OK, lặp lại vài chục lần, mất thời gian và dễ sót/trùng. Với hồ sơ thầu, hóa đơn, hợp đồng, GCN — hàng tá file đòi quy định in khác nhau (số bản, 2 mặt, khổ giấy). **Printonator gom hết lại: kéo vào, chọn máy, bấm in một lần.**

So với phần mềm in hàng loạt thương mại (Print Conductor, priPrinter…):

| | Print Conductor | **Printonator** |
|---|---|---|
| Giá | Trả phí / license theo máy | **MIT miễn phí**, mã nguồn mở |
| Office | Phụ thuộc MS Office bản quyền | **LibreOffice** — không cần bản quyền |
| AI in giùm | ❌ | ✅ **MCP AI-native** |
| Data | License server | 100% local, no telemetry |

---

## Tính năng chính

### Drop → Select → In
- **Kéo & thả** / **Ctrl+V** / nút **"+ Add files"** — thêm nhiều file cùng lúc (PDF, DOCX/XLSX/PPTX, hình, TXT…).
- **Multi-select**: Ctrl+Click chọn rời, Shift+Click chọn dải, Ctrl+A chọn hết.
- **Double-click** mở file bằng app gốc để xem/sửa; sau khi đóng **tự nạp bản mới nhất** (badge "Reloaded").

### Cấu hình đúng theo ý muốn
- **Từng file hoặc cả nhóm**: số bản, 2 mặt/1 mặt, khổ giấy, màu, khay, scale, N-up — qua Bulk bar "Cấu hình in".
- **Page range section-aware (DOCX)**: `All`, `2,5`, `3-4`, `1-2,7`, `S2:1-3` — **preview trực tiếp** "→ Will print physical pages", hết cảnh nhầm trang.
- **Cột Settings rõ ràng** trong hàng đợi: 2 mặt/màu "theo máy", gom bản, N trang/tờ — bấm vào ô là chỉnh ngay.
- **Preset**: lưu/tái dùng bộ cấu hình ("Hợp đồng 2 mặt"…).

### Máy in thật, in đúng
- Danh sách máy in **thật** + trạng thái (online/offline/error), khổ giấy, duplex/màu/khay.
- **Chọn máy mặc định Windows** khi mở app + **nhắc nhẹ** xác nhận đúng máy trước khi in (tránh in lộn máy).
- Mở **Printing Preferences / Printer Properties** native của driver — đúng cửa sổ Windows.
- Một nút **In ngữ cảnh**: có chọn → "Print (N)", không → "Print all (N)".
- **Pre-flight confirm** khi lô lớn: xem tổng tờ ước tính + máy in trước khi in.

### Trạng thái rõ ràng, không nuốt lỗi
- **Progress** footer + taskbar, cột Status (✓ Done / lỗi), toast + bell notification.
- Lỗi **có mã + tiếng Việt + gợi ý** (`PRINTER_OFFLINE`, `INVALID_PAGE_RANGE`…), **không bao giờ nuốt lỗi**.
- Popup **"Đã in xong"** khi hết lô — tuỳ chọn **xóa file đã in khỏi hàng đợi**.

### AI in giùm (MCP)
Claude, Hermes hay assistant nói chuẩn MCP kết nối là in thay bạn — **có guardrail an toàn**: allowlist máy in, giới hạn trang/lô, chế độ duyệt, audit log, **fail-closed**.

---

## Bắt đầu

---

## Bắt đầu

### Cài đặt
Tải **Installer** từ [GitHub Releases](https://github.com/nanowind/Printonator/releases) — tự cài **.NET 8 Desktop Runtime** nếu máy chưa có (không cần làm gì thêm).

### Từ mã nguồn
```bash
dotnet build Printonator.sln              # build toàn bộ
dotnet run --project src/Printonator.UI   # chạy app WPF

dotnet test Printonator.sln               # 72 Core + 27 UI tests

# MCP server ("AI in giùm") — 9 tools trên http://127.0.0.1:3939/mcp
dotnet run --project src/Printonator.Mcp
```

---

## Engine in — dùng đúng app máy bạn có

Printonator in bằng chính phần mềm có sẵn trên máy (dynamic, **không bundle thư viện** — app nhẹ), theo thứ tự ưu tiên:

1. **MS Office (COM)** — Word/Excel/PowerPoint có sẵn → in **đúng page setup & section** của file như Print Conductor.
2. **LibreOffice** — nếu máy không có MS Office nhưng có LibreOffice → `soffice --headless --pt`.
3. **Browser render** (Chrome/Edge headless → CDP `printToPDF`) — PDF/ảnh/TXT: áp đúng page range, scale, khổ giấy, chiều.
4. **Shell "printto"** — fallback cuối (in nhanh "như mặc định").

> Bảng **Print settings** đầy đủ: page range, màu, khay giấy, scale, N-up, profile + dialog native của driver. Xem thêm [docs/COMPARISON_PRINT_CONDUCTOR.md](docs/COMPARISON_PRINT_CONDUCTOR.md).

---

## Kiến trúc

```
src/
├─ Printonator.Core     # Queue engine, state machine, PresetStore, PrintGuard (an toàn AI in)
├─ Printonator.Spool    # Windows Spooler/Printer: PrinterService + engine dynamic + driver dialogs
├─ Printonator.Mcp      # MCP server: 9 tools + guard + page-count probe
└─ Printonator.UI       # WPF: job table, Print Settings, bell notification, Info/About
tests/
├─ Printonator.Core.Tests  # 72 tests (page-range, section, queue, approve, preset, guard, print-config)
└─ Printonator.UITests     # 27 tests (FlaUI app thật + engine dynamic)
```

Mọi định dạng → engine theo thứ tự đăng ký (COM → LibreOffice → Browser render → shell). PDF/Office/anh in trực tiếp; PDF page range dùng WindowsPdfRasterizer cắt trang → HTML ảnh → headless `printToPDF`. Print qua **Windows Spooler API**. State machine: `Queued → Converting → Spooling → Done / Error(code)`. UI và engine tách rời — MCP/CLI dùng chung Core.

---

## MCP — AI in giùm

| Item | Chi tiết |
|---|---|
| **Server** | `src/Printonator.Mcp` — HTTP `:3939/mcp` (mặc định) hoặc `--stdio`; **chỉ loopback, không CORS** |
| **Tools (9)** | `list_printers`, `print_files`, `get_presets`, `save_preset`, `print_with_preset`, `list_jobs`, `job_status`, `cancel_job`, + approve/job detail |
| **An toàn** | `PRINTONATOR_REQUIRE_APPROVE` (mặc định `true` — AI không tự in), `PRINTONATOR_ALLOWED_PRINTERS`, `MAX_PAGES_PER_BATCH=200`, `MAX_COPIES_PER_FILE=100`, audit JSON — **fail-closed** |
| **Engine** | Office in qua Word/Excel/PowerPoint COM; không có app đó hoặc file khác → shell fallback |

Hướng dẫn chi tiết: **[docs/MCP.md](docs/MCP.md)**

Ví dụ — cho phép AI tự in vào một máy:
```powershell
$env:PRINTONATOR_REQUIRE_APPROVE="false"
$env:PRINTONATOR_ALLOWED_PRINTERS="Microsoft Print to PDF"
dotnet run --project src/Printonator.Mcp
```

---

## Tài liệu

| Tài liệu | Nội dung |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Kiến trúc + hướng dẫn chuyển giao |
| [docs/MCP.md](docs/MCP.md) | Cấu hình + dùng MCP server |
| [docs/DESIGN_SYSTEM.md](docs/DESIGN_SYSTEM.md) | Design system cho UI |
| [docs/COMPARISON_PRINT_CONDUCTOR.md](docs/COMPARISON_PRINT_CONDUCTOR.md) | So sánh với Print Conductor (gap + roadmap) |

---

## Roadmap

**v1.x** — Cover/Report page · Gộp PDF 1 job · CLI + preset export · Per-file printer · i18n (EN)  
**v2** — Watermark · Post-processing · Watch folder · Email/CAD/HEIC · Security warning · MCP/Safety UI + duyệt job

---

## Ủng hộ

Thấy Printonator hữu ích? **rate/review repo** [github.com/nanowind/Printonator](https://github.com/nanowind/Printonator) để mình phát triển tiếp.

**Liên hệ:** phucnguyenqlcn@gmail.com · Zalo/phone +84 907 907 804

## Giấy phép

MIT — miễn phí, mã nguồn mở. Xem [LICENSE](LICENSE).