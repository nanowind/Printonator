# Handoff: Fix PDF mixed orientation trên GDI

## Bối cảnh

App Printonator in PDF qua `GdiPrintEngine` (`src/Printonator.Spool/Printing/GdiPrintEngine.cs`) — dùng `System.Drawing.Printing.PrintDocument` + `Windows.Data.Pdf` rasterize.

**Vấn đề đã xác nhận trên máy in Canon LBP151 (driver PCL)**:
- File PDF có trang landscape (vd báo cáo có bảng ngang) bị in sai orientation.
- In qua **Edge/Foxit/Adobe Reader** thì đúng — chúng gửi PostScript/XPS trực tiếp tới driver, driver xử lý orientation per page từ PostScript escape sequences.
- In qua `GdiPrintEngine` (PrintDocument) thì driver Canon **BỎ QUA** cờ `page.Landscape` khi set trong `PrintPage` event, và set `PaperSize` per page cũng bị bỏ qua. Driver chỉ tôn trọng paper size set ở `DefaultPageSettings` (trước `Print()`).
- Hệ quả: trang landscape bị in ra tờ portrait, content bị scale nhỏ hoặc shrink lệch.

## Trạng thái hiện tại (commit `f063e16` + 2 fix working tree)

### Fix đã apply (verified work trên máy Canon):

1. **Auto-rotate pattern** (tham khảo `SumatraPDF/Print.cpp:628-639 advanced.autoRotate`):
   - `PaperSize` LUÔN = khổ dọc theo user config (A4/A3/A5/Letter/Legal tùy chọn trong Settings).
   - Trang PDF landscape → `Bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone)` trước khi vẽ.
   - Áp dụng khi `Orientation = AsDocument | AsPrinter | Portrait explicit`.
   - Kết quả: trang landscape in ra tờ portrait, text nằm ngang đúng chiều đọc khi user xoay giấy 90° (chuẩn in Excel landscape ra A4 dọc).

2. **Fix offset Graphics origin**:
   - Bug cũ: code cộng `HardMarginX + safe` → double offset → ảnh bị lệch góc dưới phải trên driver Canon.
   - Fix: bỏ cộng HardMargin — Graphics origin đã ở góc printable area, chỉ cộng `safe` margin.

3. **Timeout 120s → 600s**:
   - Driver Canon chậm với file lớn (3-5s/trang cho ảnh 300dpi).
   - File 57 trang bị timeout 120s → EngineTimeout. Tăng 600s đủ ~100 trang.

### Fix ĐÃ THỬ nhưng FAIL trên Canon (KHÔNG apply vào code):

- **Set `page.Landscape = true` trong `PrintPage` event** — driver Canon bỏ qua.
- **Set `PaperSize` per page trong `PrintPage` event** — driver Canon bỏ qua.
- **Multi-job approach** (mỗi trang = 1 `PrintDocument` riêng) — vẫn fail vì cùng lý do driver cụ thể.
- **DrawImage với dw/dh đã đảo** (theo `page.Landscape`) — ảnh bị scale theo printable area vật lý chưa xoay → text nhỏ + lệch.

### Chưa giải quyết:

- **`Orientation = Landscape explicit`**: in ra landscape paper nhưng ảnh bị thu nhỏ + lệch vị trí trên driver Canon. Nguyên nhân gốc: driver cụ thể không tương thích với `PrintDocument` rotation handling.
- **PdfiumViewer / PDFSharp / iText**: tất cả vẫn qua `PrintDocument` → vẫn có cùng bug driver-specific. Bundle lib nặng (~1-30MB) không giải quyết được bug GDI.

## Hướng đi tiếp (theo đề xuất)

### Option A: Dùng Browser engine default cho PDF (Đơn giản nhất, đã verify work)
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

**Option A** (Browser engine default) — đơn giản, fix đúng gốc, không bundle lib mới, đã verify work trên Edge.

## File liên quan

- `src/Printonator.Spool/Printing/GdiPrintEngine.cs` — engine in PDF (chứa 2 fix đã apply).
- `src/Printonator.Spool/Printing/BrowserPrintEngine.cs` — engine render qua Chrome/Edge.
- `src/Printonator.Spool/Printing/EngineRegistry.cs` — thứ tự đăng ký engine.
- `src/Printonator.Spool/Printing/CdpPrintParams.cs` — params gửi tới Chrome DevTools Protocol.
- `src/Printonator.Spool/Printing/WindowsPdfRasterizer.cs` — render PDF → PNG bằng Windows.Data.Pdf.

## Test

- File test: `D:\Downloads\Documents\TT-BG-CÔNG TY ĐIỆN LỰC LẠNG SƠN - CHI NHÁNH TỔNG CÔNG TY ĐIỆN LỰC MIỀN BẮC (1)_ver2.pdf` (6 trang, trang 3-6 landscape).
- Máy in: Canon LBP151 (PCL).
- Để mặc định `Orientation = AsDocument` → in → trang 3-6 in ra tờ A4 dọc, text nằm ngang đúng chiều đọc.

## Log

- `%TEMP%\printonator-office.log` — debug log của `GdiPrintEngine` (PdfPageCount, range, IN XONG, IN QUÁ LÂU).
- App `UpdateChecker` đọc release note + SHA256 từ GitHub Release.

## Git context

- Branch: master @ `f063e16` (v0.2.5).
- Working tree: 2 fix trong `GdiPrintEngine.cs` chưa commit.
- File PDF test ngoài repo: `D:\Downloads\Documents\TT-BG-...LẠNG SƠN..._ver2.pdf`.
- Chưa có tag v0.2.6 (đã xóa draft v0.2.6 cũ do fail).

## Note cho người tiếp nhận

1. Đọc kỹ phần "Fix ĐÃ THỬ nhưng FAIL trên Canon" — tránh tốn thời gian thử lại.
2. Bug driver Canon KHÔNG thể fix bằng GDI .NET API. Cách duy nhất chắc chắn work là bypass PrintDocument (Option A/C).
3. Nếu chọn Option A: chỉ cần 2 dòng code change (đã ghi rõ ở trên), không bundle lib mới.
4. Sau khi fix: bump version 0.2.5 → 0.2.6, commit, push tag, đợi CI build installer.
