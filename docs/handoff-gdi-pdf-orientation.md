# Handoff: Fix PDF mixed orientation trên GDI

## Bối cảnh

App Printonator in PDF qua `GdiPrintEngine` (`src/Printonator.Spool/Printing/GdiPrintEngine.cs`) — dùng `System.Drawing.Printing.PrintDocument` + `Windows.Data.Pdf` rasterize.

**Vấn đề đã xác nhận trên máy in Canon LBP151 (driver PCL)**:
- File PDF có trang landscape (vd báo cáo có bảng ngang) bị in sai orientation.
- In qua **Edge/Foxit/Adobe Reader** thì đúng — chúng gửi PostScript/XPS trực tiếp tới driver, driver xử lý orientation per page từ PostScript escape sequences.
- In qua `GdiPrintEngine` (PrintDocument) thì driver Canon **BỎ QUA** cờ `page.Landscape` khi set trong `PrintPage` event, và set `PaperSize` per page cũng bị bỏ qua. Driver chỉ tôn trọng paper size set ở `DefaultPageSettings` (trước `Print()`).
- Hệ quả: trang landscape bị in ra tờ portrait, content bị scale nhỏ hoặc shrink lệch.

## Trạng thái hiện tại (working tree — fix GDI triệt để, CHƯA verify trên Canon, CHƯA commit)

> **QUYẾT ĐỊNH USER (2026-09-09): KHÔNG dùng Option A (Browser engine default) — muốn fix TRIỆT ĐỂ trong GDI.**

### Fix đã apply (verified work trên máy Canon):

1. **Auto-rotate pattern** (tham khảo `SumatraPDF/Print.cpp:628-639 advanced.autoRotate`):
   - `PaperSize` LUÔN = khổ dọc theo user config (A4/A3/A5/Letter/Legal tùy chọn trong Settings).
   - Trang PDF landscape → `Bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone)` trước khi vẽ.
   - Áp dụng khi `Orientation = AsDocument | AsPrinter | Portrait explicit` (portrait explicit ĐÃ THÊM mới).
   - Kết quả: trang landscape in ra tờ portrait, text nằm ngang đúng chiều đọc khi user xoay giấy 90° (chuẩn in Excel landscape ra A4 dọc).

2. **Fix offset Graphics origin**:
   - Bug cũ: code cộng `HardMarginX + safe` → double offset → ảnh bị lệch góc dưới phải trên driver Canon.
   - Fix: bỏ cộng HardMargin — Graphics origin đã ở góc printable area, chỉ cộng `safe` margin.

3. **Timeout 120s → 600s**:
   - Driver Canon chậm với file lớn (3-5s/trang cho ảnh 300dpi).
   - File 57 trang bị timeout 120s → EngineTimeout. Tăng 600s đủ ~100 trang.

### Fix MỚI trong working tree (chưa verify trên Canon — CẦN TEST):

4. **Thêm `Portrait` vào auto-rotate condition** (`GdiPrintEngine.cs` dòng ~390):
   - Trước: `isAutoMode = AsDocument | AsPrinter` → user chọn `Portrait` explicit + file landscape → KHÔNG rotate → shrink.
   - Sau: thêm `|| userOrientation == PrintOrientation.Portrait` → Portrait explicit cũng rotate landscape 90° vừa khổ dọc (giống AsDocument).

5. **`Landscape` explicit → ép cờ orientation DEVMODE** (`GdiPrintEngine.cs` dòng ~338):
   - Trước: user chọn `Landscape` explicit → paper vẫn dọc (mmW/mmH theo khổ dọc) → in ra tờ dọc + shrink.
   - Thử swap mmW/mmH + `PaperKind.Custom` (paper ngang) → **FAIL trên Canon (user test 2026-09-10)**: driver CLAMP custom paper về khổ dọc → in y hệt Portrait.
   - Sau (hiện tại): `pd.DefaultPageSettings.Landscape = true` TRƯỚC `Print()` — cờ orientation DEVMODE chuẩn (cơ chế Edge/Foxit), GDI tự xoay PrintableArea sang ngang, driver nhận dmOrientation=LANDSCAPE từ đầu job. KHÔNG set trong PrintPage event (quá muộn, Canon bỏ qua). Đang chờ verify.

### Fix ĐÃ THỬ nhưng FAIL trên Canon (KHÔNG apply vào code):

- **Set `page.Landscape = true` trong `PrintPage` event** — driver Canon bỏ qua.
- **Set `PaperSize` per page trong `PrintPage` event** — driver Canon bỏ qua.
- **Multi-job approach** (mỗi trang = 1 `PrintDocument` riêng) — vẫn fail vì cùng lý do driver cụ thể.
- **DrawImage với dw/dh đã đảo** (theo `page.Landscape`) — ảnh bị scale theo printable area vật lý chưa xoay → text nhỏ + lệch.
- **Option A (Browser engine default)**: user ĐÃ TỪ CHỐI 2026-09-09 — không muốn in PDF qua Chrome/Edge mặc định, muốn GDI.

### Chưa giải quyết:

- **`Orientation = Landscape explicit`**: ĐÃ xử lý bằng swap paper (mục 5) nhưng CHƯA VERIFY trên Canon. Nếu vẫn shrink → lùi về mục "Hướng đi tiếp" bên dưới.
- **PdfiumViewer / PDFSharp / iText**: tất cả vẫn qua `PrintDocument` → vẫn có cùng bug driver-specific. Bundle lib nặng (~1-30MB) không giải quyết được bug GDI.

## Hướng đi tiếp (theo đề xuất)

> **Option A ĐÃ BỊ USER TỪ CHỐI (2026-09-09)** — không dùng Browser engine default. Giữ làm phương án cuối nếu GDI không fix nổi.

### Option A: Dùng Browser engine default cho PDF (❌ USER TỪ CHỐI — tham khảo, KHÔNG làm)
- Sửa `BrowserPrintEngine.NeedsBrowserRender` luôn return `true` cho PDF format.
- Đăng ký `BrowserPrintEngine` trước `GdiPrintEngine` trong `EngineRegistry.RegisterAll`.
- Chrome/Edge headless render PDF → in qua shell printto → driver xử lý orientation per page đúng.
- **Trade-off**: chậm hơn ~2-3s/trang (Chrome render overhead), không phụ thuộc driver.
- Bundle size: 0 (Edge/Chrome đã có sẵn trên Windows 10/11).
- Code đã có sẵn trong repo (`BrowserPrintEngine.cs`), chỉ cần:
  ```csharp
  // BrowserPrintEngine.cs, dòng 174
  public static bool NeedsBrowserRender(PrintJob job) {
      if (job.Format.Equals("PDF", StringComparison.OrdinalIgnoreCase)) return true;
      // ... các case cũ
  }
  ```
  ```csharp
  // EngineRegistry.cs - thêm BrowserPrintEngine trước GdiPrintEngine
  var engines = new IPrintEngine[] {
      new OfficeComPrintEngine(),
      new LibreOfficePrintEngine(),
      new BrowserPrintEngine(),     // ← thêm dòng này
      new WatermarkPrintEngine(new BrowserPrintEngine()),
      new GdiPrintEngine(),
      new SpoolPrintEngine(),
  };
  ```

### Option B: Bundle PDFSharp rotate page (MIT ~1MB)
- Mở file PDF → `PdfPage.Rotate = 90` cho trang landscape → lưu file PDF tạm → in qua GDI.
- Trước khi rotate: dùng `Windows.Data.Pdf` rasterize trang (đã có), check `Width > Height` → nếu landscape thì rotate.
- Bundle: thêm `PDFsharp` NuGet, viết helper `RotateLandscapePages(string inputPath) → string outputPath`.
- Trade-off: file PDF tạm phải xóa sau khi in, cần thêm disk I/O.

### Option C: Gọi Win32 DEVMODE PostScript escape sequences (Cao cấp nhất)
- Dùng `Escape()` của `PrintDocument` để gửi PostScript escape sequence `SET_ORIENTATION` trực tiếp tới driver.
- Code tương tự SumatraPDF: set `dmOrientation = DMORIENT_LANDSCAPE` qua DEVMODE, driver Canon PCL hỗ trợ.
- Cần P/Invoke `SetHdevmode` + DEVMODE struct manipulation (~200 dòng code).
- Trade-off: driver-specific — chỉ work với driver hỗ trợ PostScript, không portable.

### Option D: Dùng WPF `DocumentPaginator` + `PrintDialog`
- WPF class `DocumentPaginator` xử lý rotation chuẩn qua XPS pipeline.
- Cần thêm reference `PresentationCore` (~10MB).
- Trade-off: thay đổi kiến trúc lớn.

## Recommend

**Fix GDI trong working tree** (mục 4 + 5) — user từ chối Option A. Thứ tự kiểm chứng:
1. Verify trên Canon LBP151 với file PDF có trang landscape: `Portrait` explicit → xoay đúng; `Landscape` explicit → ra tờ ngang không shrink.
2. Nếu Landscape explicit vẫn shrink trên Canon → lùi về Option C (Win32 DEVMODE) trước, Option A là phương án cuối.
3. Verify OK → commit + bump version → release.

## File liên quan

- `src/Printonator.Spool/Printing/GdiPrintEngine.cs` — engine in PDF (chứa 2 fix đã apply).
- `src/Printonator.Spool/Printing/BrowserPrintEngine.cs` — engine render qua Chrome/Edge.
- `src/Printonator.Spool/Printing/EngineRegistry.cs` — thứ tự đăng ký engine.
- `src/Printonator.Spool/Printing/CdpPrintParams.cs` — params gửi tới Chrome DevTools Protocol.
- `src/Printonator.Spool/Printing/WindowsPdfRasterizer.cs` — render PDF → PNG bằng Windows.Data.Pdf.

## Test

- File test: `D:\Downloads\Documents\TT-BG-CÔNG TY ĐIỆN LỰC LẠNG SƠN - CHI NHÁNH TỔNG CÔNG TY ĐIỆN LỰC MIỀN BẮC (1)_ver2.pdf` (6 trang, trang 3-6 landscape). Bản copy trong repo root: `TT-BG-CÔNG TY ĐIỆN LỰC LẠNG SƠN - CHI NHÁNH TỔNG CÔNG TY ĐIỆN LỰC MIỀN BẮC (1)_ver2.pdf` (untracked).
- Máy in: Canon LBP151 (PCL).
- Kiểm tra CẢ 4 chế độ Orientation trong Print Settings:
  - `AsDocument` (mặc định) → trang 3-6 in ra tờ A4 dọc, text nằm ngang đúng chiều đọc (baseline — verify trước).
  - `Portrait` explicit → NHƯ AsDocument (fix mới mục 4): landscape xoay vừa tờ dọc.
  - `Landscape` explicit → ra TỜ NGANG đúng khổ, content KHÔNG shrink (fix mới mục 5 — đây là phần rủi ro nhất, driver Canon có thể vẫn shrink).
  - `AsPrinter` → theo máy, giữ nguyên.

## Log

- `%TEMP%\printonator-office.log` — debug log của `GdiPrintEngine` (PdfPageCount, range, IN XONG, IN QUÁ LÂU).
- 2026-09-10: **Máy in ảo (PDF printer) + file PDF nguồn** — bỏ nhánh "copy thẳng file gốc" (sai: bỏ qua page range/chiều/scale, "print to PDF" ra y nguyên file) → máy ảo LUÔN đi qua `BrowserPrintEngine` render đúng cấu hình rồi lưu PDF cạnh file gốc (`PdfOutputPath` tự thêm `_printonator` cho nguồn .pdf). Thêm probe PageCount cho PDF trước render (slicing range/parity cần PageCount) + lớp bảo vệ không đè file nguồn. Test E2E mới `VirtualPdfPrinter_PdfSource_RendersNotCopies_KeepsOriginalUntouched` pass.
- 2026-09-10: fix mục 4+5 mô tả trong handoff KHÔNG còn trong code (working tree bị mất — code thực tế chưa xử lý Landscape, chưa có Portrait trong auto-rotate → Landscape/Portrait in ra tờ dọc giống nhau) → RE-APPLY lại: Portrait thêm vào auto-rotate; Landscape thử swap PaperSize ngang + Custom → **FAIL trên máy in vật lý (user test)**: driver clamp về khổ dọc. Chuyển sang cờ `DefaultPageSettings.Landscape = true` (DEVMODE orientation) → trang RA NGANG đúng (user xác nhận) NHƯNG nội dung co nhỏ + lệch góc dưới phải. Root cause (xác nhận bằng probe in thử ra Microsoft Print to PDF): khi Landscape bật, `e.PageSettings.PrintableArea` vẫn trả khổ DỌC (826×1169) trong khi `e.Graphics.VisibleClipBounds` là NGANG (1169×826) → fix: lấy vùng vẽ từ `VisibleClipBounds`. Verify probe: landscape 4 lề ~2-3% cân đối, portrait giữ nguyên. Build sạch + test pass (Core 106, Spool 5). UITests 2 fail = MÔI TRƯỜNG (không desktop tương tác) — không liên quan GDI. CẦN user test lại trên máy in vật lý.
- 2026-09-10: **Máy ảo + PDF có page range → orient** — nhánh cắt trang PDF (`CdpPrintParams.BuildForSlicedImages`) hardcode `landscape=false` + khổ tờ lấy theo trang gốc → chọn Ngang vẫn ra tờ dọc. Thử xoay ảnh 90° → SAI (user test: chọn Dọc cho trang ngang lại ra chữ xoay 90°). Fix đúng: `WindowsPdfRasterizer.FitToPaper` — dựng canvas đúng khổ tờ user chọn rồi vẽ ảnh thu nhỏ vừa khung, canh giữa, GIỮ NGUYÊN chiều đọc (giống GDI fit ảnh vào vùng in); `Theo tài liệu`/`Theo máy` giữ khổ trang gốc.
- 2026-09-10 (self-test probe — in thật ra máy ảo "Microsoft Print to PDF", PDF nguồn TRANG NGANG nền đỏ + dải xanh mép trên, đo lại file xuất): `Portrait` → trang DỌC, bbox nội dung 834x590 (aspect 1.41 = rộng → KHÔNG xoay), dải xanh vẫn trên / đỏ dưới (đúng chiều đọc); `Landscape` → trang NGANG nội dung lấp đầy; `AsDocument` → giữ khổ trang gốc (NGANG). Kèm hardening: `File.Copy` lúc lưu PDF xuất bọc try/catch → file xuất bị chương trình khác giữ lock trả lỗi mềm `SPOOLER_FAILED` + hint "đóng file rồi in lại" (trước đó exception thoát ra giết job) — test `VirtualPdfPrinter_OutputLocked_ReturnsCleanError_NoThrow` pass. Kết quả: Spool 8/8, Core 106/106, BrowserEngine (UITests) 17/17; build solution sạch 0 warning/0 error.
- App `UpdateChecker` đọc release note + SHA256 từ GitHub Release.

## Git context

- Branch: master @ `f063e16` (v0.2.5). Commit `1fc2d3e` = handoff này.
- Working tree: fix GDI (mục 4+5) trong `GdiPrintEngine.cs` + handoff đã cập nhật — CHƯA commit.
- File PDF test ngoài repo: `D:\Downloads\Documents\TT-BG-...LẠNG SƠN..._ver2.pdf`; bản copy untracked trong repo root.
- Chưa có tag v0.2.6 (đã xóa draft v0.2.6 cũ do fail).

## Note cho người tiếp nhận

1. Đọc kỹ phần "Fix ĐÃ THỬ nhưng FAIL trên Canon" — tránh tốn thời gian thử lại.
2. Bug driver Canon KHÓ fix bằng GDI .NET API thông thường. Cách chắc chắn work nhất là bypass PrintDocument (Option C). User KHÔNG muốn Option A (browser default) — tôn trọng quyết định này.
3. Fix hiện tại (mục 4 + 5) là 2 thay đổi nhỏ trong `GdiPrintEngine.cs` — build + full test đã pass. CẦN VERIFY TRÊN CANON trước khi release.
4. Sau khi verify OK: bump version 0.2.5 → 0.2.6, commit, push tag, đợi CI build installer.
