# Printonator — Kiến trúc & Hướng dẫn chuyển giao (Handoff)

> Dành cho AI khác (hoặc dev mới) nắm codebase và tiếp tục thực hiện.
> Trạng thái: 2026-08-25 — snapshot hiện tại của repo. Đọc kèm `CONCEPT.md` (tầm nhìn) + `docs/COMPARISON_PRINT_CONDUCTOR.md` (gap analysis) + `docs/DESIGN_SYSTEM.md` (UI).

## 1. Tổng quan

**Printonator** = app in hàng loạt (bulk printing) cho Windows 10/11, C#/.NET 8 WPF, MIT.
Luồng chính: kéo/thả N file → chọn máy in → cấu hình (bản, 2 mặt, khổ, page range) → **In tất cả / In selected** → theo dõi trạng thái từng job → lỗi có mã + tiếng Việt + gợi ý.

Điểm khác biệt chiến lược (CONCEPT §6): **MCP server** để AI in giùm — đã code (13 tools, guard, approve qua MCP, skill + plugin; xem docs/MCP.md).

## 2. Solution structure (4 projects)

```
Printonator.sln
├─ src/
│  ├─ Printonator.Core/          # Logic lõi, không phụ thuộc UI
│  │  ├─ PrintQueue.cs           # Queue engine: drain, retry, state machine, engine registry, approve/cancel
│  │  ├─ Presets/PresetStore.cs  # JSON preset (save/load/delete, backup file hỏng)
│  │  ├─ Safety/PrintGuard.cs    # Allowlist máy in + quota trang/lô/copies + approve + audit (fail-closed)
│  │  └─ Models/
│  │     ├─ PrintJob.cs          # Job + Config + Source(User/Mcp) + State(AwaitingApproval) + SectionMap + Result<T>
│  │     └─ PrintError.cs        # PrintError + ErrorCodes + PrintErrorCategory
│  ├─ Printonator.Spool/         # Windows Spooler/Printer (net8.0-windows)
│  │  ├─ Printing/PrinterService.cs    # GetPrintQueues + GetPrintCapabilities thật (trạng thái, khổ giấy, khay)
│  │  ├─ Printing/InstalledApps.cs     # Phát hiện Word/Excel/PowerPoint (ProgID COM)
│  │  ├─ Printing/OfficeComPrintEngine.cs  # In Office bằng app gốc (COM PrintOut, như Print Conductor)
│  │  └─ Printing/SpoolPrintEngine.cs  # Engine shell "printto" fallback (mọi định dạng)
│  ├─ Printonator.Mcp/           # MCP server "AI in giùm" (stdio + HTTP 127.0.0.1:3939/mcp)
│  │  ├─ Program.cs / AppServices.cs
│  │  ├─ PrintTools.cs           # 8 tools + PrintGuard chặn trước khi Enqueue (cổng tuần tự chống TOCTOU)
│  │  ├─ Probing/PdfPageCountProbe.cs  # Đếm trang PDF best-effort cho quota
│  └─ Printonator.UI/            # WPF (net8.0-windows)
│     ├─ MainWindow.xaml(.cs)    # Toàn bộ UI: job table, toolbar, bulk bar, bell, search, sort, toast, progress
│     ├─ PrinterConfigWindow     # Màn "Printers & paper setup" (trạng thái/khổ giấy/capabilities/Scan)
│     ├─ PageRangeDialog.xaml    # Dialog chọn trang (+ preview "→ Will print physical pages")
│     ├─ PaperSetupDialog.xaml   # Dialog khổ giấy / duplex / màu
│     └─ app.ico
└─ tests/
   ├─ Printonator.Core.Tests/    # xUnit — 78 tests (page-range, section, queue, approve, preset, guard, error-routing)
   ├─ Printonator.Spool.Tests/   # xUnit — 4 E2E print-to-PDF
   ├─ Printonator.Mcp.Tests/     # xUnit — 6 (tool shape, error reference, pick printer)
   └─ Printonator.UITests/       # xUnit + FlaUI.UIA3 — 27 UI tests
```

**Tóm tắt 1 dòng mỗi class:**
- `PrintQueue` — hàng đợi in: `Enqueue` (thêm + tự in) / `AddOnly` (thêm, chờ user bấm in) / `ProcessExisting` (in job đã có, KHÔNG duplicate) / `RemoveJob` (xóa lock-safe); retry tối đa `MaxRetries`; `JobStateChanged` event cho UI.
- `PrintJob` — 1 file; `Config` (PrintConfig: Copies, Duplex, PaperSize, Color, PrinterName, PageRange); `State` (internal set — chỉ Core đổi được qua `SetState`); `ResolvePhysicalPages()` map page-range → danh sách trang vật lý (section-aware).
- `PrintError` — mọi lỗi có `Code` (hằng ErrorCodes), `Category` (App/Config/Printer/System), `Message` tiếng Việt, `Hint` gợi ý; **không catch nào nuốt lỗi** (convention cứng).
- `ShellPrintEngine` — `IPrintEngine`; `CanHandle => true` (mọi định dạng); `PrintAsync` dùng `ProcessStartInfo.Verb = "printto"` + tên máy in.

## 3. Luồng dữ liệu chính

### 3.1 Thêm file
```
User kéo thả / Ctrl+V / "+ Add files"
  → AddFiles(paths) (MainWindow.xaml.cs:153)
  → _queue.AddOnly(job)          # thêm vào Jobs, KHÔNG in
  → UpdateFooter()               # "N files | X printed | Y error"
```
> **Lưu ý:** `AddOnly` ≠ `Enqueue`. AddOnly chỉ thêm; Enqueue thêm + drain ngay. Demo jobs dùng AddOnly.

### 3.2 In
```
Bấm "Print all" (PrintAll_Click)
  → lọc Jobs có State == Queued
  → PrintJobs(ready) → _queue.ProcessExisting(job)   # KHÔNG enqueue lại → không duplicate
  → DrainOnceAsync → SetState(Converting) → ProcessWithRetryAsync
  → engine.PrintAsync → Done / Error(retryable? → retry ≤ MaxRetries)
```
> **Print all**: chỉ in Queued. **Print selected**: in Queued + in lại Done/Error/Cancelled (nếu chọn).

### 3.3 State machine
```
Queued → Converting → (engine) → Done
                ↓ retryable? → Converting (≤ MaxRetries, delay RetryDelayMs)
                ↓ không retryable / hết retry → Error(PrintError)
     (cancel) → Cancelled
```

### 3.4 Lỗi → UI
```
Engine trả Result<bool>.Fail(PrintError) hoặc throw
  → SetState(Error, error) → JobStateChanged?.Invoke(job)
  → OnJobStateChanged (MainWindow) Dispatcher.Invoke → ShowBanner(code, message, hint)
```

## 4. Quy ước & quy tắc cứng (đọc trước khi sửa)

1. **Không nuốt exception** — mọi `catch` phải tạo `PrintError` (code/category/message/hint) → event/banner.
2. **State chỉ Core đổi** — `PrintJob.State` có `internal set`; UI gọi `_queue.ProcessExisting` chứ không gán trực tiếp.
3. **In không enqueue lại** — dùng `ProcessExisting` khi user bấm in trên job đã có (tránh duplicate dòng — bug đã fix).
4. **UI thread** — `Dispatcher.Invoke` cho mọi cập nhật UI từ event queue.
5. **Tiếng Việt không dấu trong comment** khi cần nhanh (mặc định comment tiếng Việt có dấu).
6. **Sort/thêm cột mới** — giữ header row và item template ColumnDefinitions KHỚP NHAU (skill dotnet-desktop: grid column discipline).
7. **Nút WPF không có CornerRadius** — dùng `RoundedBtn`/`GhostBtn` style (ControlTemplate Border). Dialog mới phải tự copy 2 style này vào Window.Resources (đã vướng XamlParseException "Cannot find resource 'GhostBtn'" — PageRangeDialog/PaperSetupDialog tự có).
8. **Test bắt buộc** — Core logic (page-range, queue) phải có unit test; UI thay đổi hành vi → thêm FlaUI test.

## 5. Engine in — trạng thái hiện tại & kế hoạch

Nguyên tắc: **dynamic theo máy user, KHÔNG bundle thư viện** (app nhẹ, installer nhỏ). Máy có gì dùng đó,
ưu tiên theo thứ tự đăng ký trong registry (engine đầu tiên `CanHandle(format)` thắng).

| Engine | Trạng thái | Ghi chú |
|---|---|---|
| `OfficeComPrintEngine` (MS Office COM) | ✅ Hoạt động | Word/Excel/PPT có trên máy → PrintOut giữ page setup/section; chạy STA thread + timeout 60s; duyệt range/copies/duplex(Word); PageCount thật qua ComputeStatistics/ActivePrinter |
| `LibreOfficePrintEngine` (dynamic) | ✅ Có (2026-08-26) | `LibreOfficeLocator` dò `soffice.exe` trên máy (registry HKLM/HKCU `Software\LibreOffice\LibreOffice` → InstallPath, đường dẫn mặc định, env `PRINTONATOR_LIBREOFFICE`) → `soffice --headless --pt "máy"` (hoặc `-p` máy mặc định); timeout 120s + kill; KHÔNG bundle LibreOffice |
| `BrowserPrintEngine` (dynamic render) | ✅ Có (2026-08-26) | PDF/ảnh/TXT: **Chrome trước, Edge sau** (xác thực: Chrome headless ổn định; một số bản Edge 151+ không chạy headless/CDP — máy này) → headless `Page.printToPDF` qua CDP (ClientWebSocket BCL, không NuGet): áp thật page range (non-PDF — pageRanges CDP), scale, khổ giấy (inch), landscape, margin (Fill=0), **preferCSSPageSize khi khổ "theo tài liệu"**. FAST-PATH: cấu hình như default (A4/dọc/All/AsDocument) → shell thẳng, KHÔNG render. **PDF page SLICING** dùng WindowsPdfRasterizer (Windows.Data.Pdf — API có sẵn, không lib): render trang chọn → PNG → HTML → printToPDF đúng khổ gốc. **Lọc trang lẻ/chẵn** (Parity) + **DPI rasterize** theo chất lượng (High 200/Medium 150/Low 100/Draft 75). Render lỗi → rớt mềm về shell. |
| `SpoolPrintEngine` (shell printto) | ✅ Fallback mọi định dạng | PDF/ảnh/TXT qua app mặc định của Windows (Edge/Adobe...); **Win32Exception 1155** nếu file không có handler — báo lỗi rõ |
| WIC (ảnh) | ❌ Chưa code | roadmap — Windows Imaging Component có sẵn (không cần bundle) |

**Cách gắn engine mới:** `queue.RegisterEngine(engine)` (registry DANH SÁCH, `PickEngine` = engine đầu tiên CanHandle).
Engine implement `IPrintEngine { bool CanHandle(string format); Task<Result<bool>> PrintAsync(PrintJob, CancellationToken); }`.

## 6. Testing

```bash
dotnet test Printonator.sln          # Core 78 + Spool 4 + Mcp 6 + UI 27 = 115 tests
dotnet build Printonator.sln
dotnet format Printonator.sln        # format chuẩn (đã chạy, tree sạch)
```

- **Core.Tests**: `PageRangeTests` (Theory: All/2,5/3-4/reversed/empty; invalid → InvalidPageRange; section S2:1-3 → [3,4,5]; section không tồn tại → SectionNotFound; ngoài giới hạn → Invalid) + `PrintQueueTests` (Enqueue→Done, AddOnly không tự in, ProcessExisting không duplicate, reprint Done, RemoveJob, error → PrintError).
- **UITests (FlaUI)**: tự launch `Printonator.UI.exe` từ `AppContext.BaseDirectory` (resolve 5 cấp `..\..\..\..\..\src\...`), tìm window, click/select qua AutomationId (`JobList`, `PrintAllBtn`, `PrintSelectedBtn`, `BulkCountText`...). `PrintSettingsWindow_Constructor_Loads` dựng window trên STA + nạp theme thủ công (`pack://application:,,,/Printonator.UI;component/Themes/*.xaml` — vì test host không resolve relative Source của App.xaml).
  - **Pitfall đã biết:** ContextMenu WPF popup KHÔNG lộ qua UIA3 (chỉ thấy "System") — test hành vi gián tiếp (bulk bar) thay vì menu. Border WPF không có AutomationPeer → assert qua TextBlock (BulkCountText). **SendInput flaky** (`Win32Exception Access is denied` trên máy này) → test bấm nút dùng `Invoke()` pattern, chỉ Dropdown test giữ mouse click thật (bắt regression z-order).

## 7. Những thứ chưa làm (từ gap analysis + CONCEPT) — thứ tự đề xuất

Xem chi tiết `docs/COMPARISON_PRINT_CONDUCTOR.md`. Tóm tắt:
1. **P0**: Engine PDF slicing thật để cắt **page range trên PDF** (browser viewer từ chối ranges — đã verify; cần lib/dịch vụ render nhẹ, hoặc dùng viewer API qua CDP)
2. **P1**: Cover/Report page; Single print job (gộp PDF); Preset/Profile (.ini) + CLI; Per-file printer
3. **P2**: Watermark; Post-processing; Watch folder; Email/CAD/HEIC; log file
4. **MCP server** — ✅ đã có `Printonator.Mcp` (13 tools: pick_printer/approve_job/reject_job/get_guard_config/get_error_reference..., HTTP/stdio, guard, approve qua MCP, skill `.claude/skills/printonator-mcp` + plugin `plugin/`, xem docs/MCP.md). Duyệt thật giờ qua MCP tool; host in-process trong UI (màn duyệt) còn là roadmap-optional

## 8. Môi trường & tooling

- .NET 8 SDK (dotnet 8.0.424), VSCode + C# Dev Kit (`ms-dotnettools.csdevkit`)
- Launch debug: F5 (launch.json type=dotnet, projectPath → Printonator.UI.csproj)
- Setup: `setup/printonator.iss` (Inno Setup, tiếng Việt) — build bằng `setup/build-setup.sh`
- Git: repo đã init (branch master); các file cấu hình cục bộ (AI tooling, workflow state, .env) nằm ngoài git theo `.gitignore`
- Printers test trên máy: "Microsoft Print to PDF", các Canon LBP, PDF-XChange...

## 9. Lỗi thường gặp & cách tránh

| Lỗi | Nguyên nhân | Cách tránh |
|---|---|---|
| XamlParseException "Cannot find resource 'GhostBtn'" | Dialog mới dùng StaticResource style của MainWindow | Copy RoundedBtn/GhostBtn vào Window.Resources dialog (đã làm cho 2 dialog) |
| MC3072: Button không có CornerRadius | WPF Button thật không có thuộc tính này | Dùng ControlTemplate Border (style RoundedBtn) |
| CS0234 Result<> trong Printonator.Core | Result ở namespace Models | `using Printonator.Core.Models` |
| FlaUI không thấy Border | Border không có AutomationPeer | Assert TextBlock/Button, không assert Border |
| Nút Print tạo dòng trùng | Enqueue lại job đã có | `ProcessExisting` (không thêm Jobs.Add) |
| dotnet test UI fail ngay | App exe chưa build / path sai | `dotnet build Printonator.sln` trước; AppPath resolve từ BaseDirectory |