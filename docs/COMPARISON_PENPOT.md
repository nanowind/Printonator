# Đối chiếu Printonator (app) vs Thiết kế Penpot — Element Gap Analysis (2026-08-25)

> Nguồn: Penpot self-host (docker, 7 boards) — đọc cấu trúc + text shapes qua MCP 2026-08-25.
> Mục đích: AI khác đọc xong biết element nào có trong thiết kế nhưng CHƯA có trong app → làm theo thứ tự.
> Board tham chiếu: "Printonator A - Job Queue" (155 children), "Printonator A - Printer Config" (63), "Printonator A - Preset" (48), "Security Warning" (10). B (61/47/46) là variant phụ — không ưu tiên.
> **Cập nhật**: 2026-08-25 (tối) — sau đợt "bổ sung tính năng + MCP Penpot" → nhiều mục đã chuyển ✅. Trạng thái ghi ngay trong bảng.

## 1. Màn Job Queue (board chính)

| Element trong Penpot | Trong app? | Ghi chú / việc cần làm |
|---|---|---|
| Logo (P đen) + "Printonator" | ✅ Có | Header |
| **Icon search "⌕"** | ✅ **Có** | SearchBox góc phải header (lọc theo tên file, live) |
| Bell + badge "2" | ✅ Có | Đã làm popover giống Penpot |
| Nút "+ Add files" | ✅ Có | — |
| **Combo "HP LaserJet Pro M404" (máy in chọn)** | ✅ Có PrinterCombo | Giờ hiển thị PrinterInfo đầy đủ (DisplayMemberPath=Name) + chấm trạng thái |
| Nút "Paper setup" | ✅ Có | — |
| **Nút "Print all (12)" — có số lượng** | ✅ **Có** | Nút tự cập nhật "Print all (N)" = số job đang chờ |
| **Nút "Print selected"** | ✅ Có | — |
| **Error banner "Printer not reachable" + Retry** | ✅ Có | ErrorBanner + RetryBtn |
| **PROGRESS BAR** | ✅ **Có** | Footer ProgressBar + % + taskbar progress (done/total) |
| Column headers: Name / Pages to print / Settings / Status / Error | ✅ Có | + **sort khi bấm cột** |
| Job rows: format badge, name, pages range, settings "4x / A4 / 2-sided", status pill, error text | ✅ Có | **"· via Word" ✅** (Office COM Word giữ page setup; Excel/PPT kế tiếp) |
| **Status pill "Ready"** | ✅ Có | State binding |
| **✓ icon khi done, ↻ Reloaded badge** | ✅ **Có** | StateText "✓ Done" + badge "↻ Reloaded" (WasReloaded) |
| **Bulk bar: "2 files selected", Copies, Duplex, Paper, Apply** | ✅ Có | BulkBar + Apply |
| Footer "12 files \| 0 printed \| 1 error" | ✅ Có | FooterStats |
| **Toast "Added 3 files to queue"** | ✅ **Có** | Toast góc phải dưới, auto ẩn 4s — cả "added", "đã in xong", reload |
| **Notifications popover: Update / Security** | ✅ Có | NotifPopup (đã làm) |
| **PageRangeDialog: preview "→ Will print physical pages 3-5"** | ✅ **Có** | Preview live khi gõ; section S2:1-3 map sang trang vật lý |

## 2. Màn Printer Config ("Printers & paper setup")

| Element trong Penpot | Trong app? | Ghi chú |
|---|---|---|
| **Toàn bộ màn "Printers & paper setup" + "← Back"** | ✅ **Có** | `PrinterConfigWindow` (nút "Printers" trên toolbar) |
| **List AVAILABLE PRINTERS (4)** | ✅ Có | `PrinterService` = LocalPrintServer.GetPrintQueues + GetPrintCapabilities |
| **Mỗi máy: name + status + PAPER + CAPABILITIES + Selected/Select** | ✅ **Có** | Status dot, Available/Offline chip, khổ giấy, Duplex/Màu/Khay, badge "ảo" |
| **Cảnh báo "Offline printers won't receive jobs..."** | ✅ **Có** | OfflineBanner trong PrinterConfigWindow |
| **DEFAULT PAPER BY DOCUMENT TYPE (A4→Word/Excel/PDF, A3→drawing, A5→receipts)** | ✅ **Có** | `DefaultPaperFor`: DWG/DXF/PLT→A3, TXT/CSV→A5, còn lại A4 |
| **"Check printer health / Scan printers"** | ✅ **Có** | Nút "Scan printers" nạp lại trạng thái; chấm xanh/đỏ trên MainWindow |

## 3. Màn Preset / Settings

| Element trong Penpot | Trong app? | Ghi chú |
|---|---|---|
| **Presets list (Hop dong Lien doanh 4x/A4/2-sided/HP M404 + Delete + New preset)** | ✅ **Có (mới)** | `PresetManagerWindow` (danh sách, đổi tên, xóa, áp dụng) + PresetStore JSON + MCP get_presets/save_preset/print_with_preset; Print Settings có combo profile + Lưu/Xóa |
| **AI PRINT VIA MCP: Status Running, Endpoint http://localhost:3939/mcp, Tools list** | ⚠️ Một phần | **Server có** (Printonator.Mcp, HTTP/stdio, 13 tools, endpoint :3939/mcp — xem docs/MCP.md); **màn UI theo dõi MCP CHƯA có** |
| **SAFETY: Approved printers only (5), Max pages/batch (200), Require approve (On), Audit (Yes)** | ⚠️ Một phần | **Guard có** (PrintGuard: allowlist, 200 trang/lô, RequireApprove=true mặc định, audit JSON); **nút duyệt job `AwaitingApproval` có trên MainWindow (Approve all/Reject all)**; màn UI Safety đầy đủ CHƯA có |

## 4. Màn Security Warning (board 7)

| Element | Trong app? | Ghi chú |
|---|---|---|
| **Cảnh báo "This build is NOT trusted..."** | ⚠️ Một phần | AboutWindow đã có dòng "Bản build chưa xác thực chữ ký số" (About.SecurityNote, 5 ngôn ngữ); hạ tầng ký bản phát hành (minisign) đã có trong release.yml — màn warning đầy đủ theo Penpot chưa làm |

## 5. Tổng hợp nhanh — trạng thái sau đợt này

### ✅ Đã xong (đợt 2026-08-25):
1. **Progress bar** — footer + taskbar (done/total)
2. **Toast success** khi add file / in xong / reload
3. **"Print all (N)"** — count live
4. **Icon search ⌕** — lọc theo tên
5. **✓ done + ↻ Reloaded badge**
6. **PageRangeDialog preview** "→ Will print physical pages"
7. **Màn Printer Config** — trạng thái + paper + capabilities + Scan + offline banner
8. **Default paper theo loại file**
9. **Sort theo cột** (Name/Pages/Settings/Status)
10. **MCP server + guard** — 13 tools, allowlist/quota/approve/audit
11. **Engine app gốc Office** (Word/Excel/PPT COM) cho DOCX/XLSX/PPTX — fallback shell

### ✅ Đã xong thêm (các đợt 2026-08-26..30):
12. **Màn Preset UI** — `PresetManagerWindow` (list + đổi tên + xóa + áp dụng) + profile combo trong Print Settings
13. **Nút duyệt job `AwaitingApproval`** — Approve all / Reject all trên MainWindow (host MCP in-process dùng chung PrintQueue)
14. **PDF engine + LibreOffice** — `WindowsPdfRasterizer` (Windows.Data.Pdf, không cần PDFium) + `LibreOfficePrintEngine` + `BrowserPrintEngine`
15. **Security Warning (bản nhẹ)** — dòng "chưa xác thực chữ ký số" trong AboutWindow (5 ngôn ngữ)

### ❌ Còn thiếu (thứ tự đề xuất):
- **Màn MCP/Safety UI đầy đủ** (Status/Endpoint/Tools + allowlist/quota/approve/audit dạng bảng)
- **Màn Security Warning đầy đủ theo Penpot** (phát hiện clone/signature thật sự — hiện mới có dòng thông báo)

## 6. Ghi chú cho AI thực hiện tiếp

- Đọc design Penpot: mở Penpot UI (docker đang chạy: frontend/backend/exporter/mcp) — 7 boards tên "Printonator A/B ...".
- `PrintJob.WasReloaded` ✅ đã hiển thị; `PrinterInfo` đã có StatusText/CapabilitiesSummary/PaperSummary cho UI.
- Màn Preset: reuse `PresetStore` + `Preset.ToPrintConfig()`; màn MCP: host MCP **in-process trong UI** (cùng PrintQueue) để approve thật = nút trên MainWindow.
- Penpot: **CHỈ là nguồn thiết kế UI** (đọc qua penpot MCP), KHÔNG phải nguồn file in — đã bỏ tool `penpot_print_file`. Muốn đọc design: kết nối penpot MCP rồi đối chiếu `docs/COMPARISON_PENPOT.md`.