# Backlog

Việc còn lại của Printonator — ưu tiên cho các lần làm sau. Cập nhật khi làm xong (xóa hoặc chuyển sang "Đã làm").

## Đã làm (2026-10-29 — rà soát toàn app + tối ưu)

- [x] **MCP HTTP không có xác thực** — thêm middleware Bearer token khi env `PRINTONATOR_MCP_TOKEN` set (`src/Printonator.Mcp/Program.cs`).
- [x] **Command injection qua tên máy in** — `SpoolPrintEngine.ValidPrinterName` chặn ký tự shell (giữ tiếng Việt, ngoặc, gạch nối); 13 test trong `PrinterNameValidationTests`.
- [x] **Argument injection vào LibreOffice** — chuyển sang `ProcessStartInfo.ArgumentList`.
- [x] **STA thread bị bỏ rơi giữ COM + DEVMODE kẹt** — `WaitWithTimeoutAsync` nhận `worker`, `Join(3s)` sau khi kill Office để `finally` chạy (doc.Close/app.Quit/FinalReleaseComObject/duplexRestore.Dispose).
- [x] **PrintQueue double-drain race** — `_drainRunning` chỉ nhả ở finally ngoài cùng của `DrainLoopAsync`.
- [x] **Deadlock `AllJobsCompleted`** — invoke ra ngoài lock, bọc `FireAllCompletedSafely` (nuốt + log lỗi subscriber).
- [x] **Enumeration `Jobs` dưới lock** — snapshot `ToList()` trong `Resume()`.
- [x] **`WrapError` mất loại lỗi** — map `FileNotFoundException`/`IOException`/`UnauthorizedAccessException` sang mã lỗi đúng.
- [x] **`SpoolPrintEngine` dispose process đang chạy** — `CleanupSpawnedProc` kill + wait + dispose an toàn.
- [x] **SelectedPrinter không notify** — đổi sang `DependencyProperty` + bind `SelectedItem` TwoWay.
- [x] **UI test không hermetic** — set `PRINTONATOR_LANGUAGE=vi` qua env khi launch.
- [x] **`EmptyState` không có UIA peer** — gắn AutomationId vào TextBlock tiêu đề.
- [x] **`PaperSource` (khay) là option chết** — wire vào `GdiPrintEngine` (map tên khay thân thiện → InputBin → `PaperSource` của driver).
- [x] **`ScaleMode`/`ScalePercent` không tác dụng với PDF** — `GdiPrintEngine.ZoomFactor` áp zoom khi vẽ.
- [x] **`WatermarkPosition` không có picker** — combo 5 vị trí trong Cài đặt in + 6 key i18n × 5 ngôn ngữ.
- [x] **Watermark nuốt page range** — rasterize theo `ResolveSelectedPages` thay vì toàn bộ trang.
- [x] **`Booklet` là option chết** — bỏ khỏi combo + hint (chưa engine nào imposition thật); preset cũ quy về 2 trang/tờ.
- [x] **Version skew** — Mcp/plugin.json/WiX đồng bộ 0.2.12.
- [x] **Installer thiếu Printonator.Mcp** — thêm 4 file vào `[Files]` + bước stage CI/build-setup.
- [x] **CI chỉ chạy Core** — thêm Spool + Mcp tests + i18n gate.
- [x] **Docs sai** — .NET 10→8, module không tồn tại (PDFium/Formats.*/Cli), CLI "chưa có"→đã có, số test bỏ con số cứng.

## Fix nhỏ (nên làm trong lần fix lỗi kế tiếp)

- [ ] **Banner lỗi trang bìa hiện màu vàng** — `ErrorCodes.SpoolerFailed` nằm trong danh sách banner vàng + nút "Thử lại". Với lỗi bìa nên cân nhắc banner đỏ (mất trang bìa là mất hẳn, retry không tự gắn lại). Quyết: giữ vàng (có retry) hay đổi đỏ.
- [ ] **Xóa key i18n mồ côi `Settings.HintAutoOrientationLock`** — tồn tại sau khi bỏ tick AutoOrientation khỏi Cài đặt in. Không ai dùng; xóa khỏi `Keys.cs` + `Strings.json` (5 ngôn ngữ).
- [ ] **Cột Settings trong MainWindow vẫn tiếng Việt ở UI EN/ZH/RU/JA** — đã có mapper `LocalizeSummary` cho TRANG BÌA (`PrintBatchOrchestrator.cs`), áp lại cho cột Settings bằng converter/rename để UI và bìa nhất quán.
- [ ] **Test UI `PrintSelected_DoesNot_Duplicate_Rows` còn phụ thuộc clipboard** — paste Clipboard FileDropList không ổn định trong test-host; nên seed qua API nội bộ (test hook) thay vì Ctrl+V.
- [ ] **API version cho MCP server** — `Printonator.Mcp.csproj` giờ là 0.2.12 nhưng không đọc từ UI; cân nhắc `Directory.Build.props` làm nguồn version duy nhất.

## Vấn đề môi trường / của user

- [ ] **File `Downloads\0.Tổng hợp công việc HC thiết bị đo lường năm 2024.xlsx` làm Excel treo khi `Workbooks.Open`** — probe bìa đã tự vệ (hết hạn 15s + kill EXCEL spawn, không để mồ côi), nhưng lúc IN THẬT job vẫn chờ timeout 60s rồi lỗi. Cần user mở file trong Excel tay để xem Excel báo gì (file corrupt? protected view?), hoặc skip file này.

## Chưa làm (đã duyệt từ trước)

- [ ] **Quảng cáo labm.io.vn** (user duyệt 2026-08-28, làm kèm fix lỗi/release kế): "phần mềm quản lý phòng thí nghiệm (thử nghiệm + đo lường, hiệu chuẩn kiểm định thiết bị đo) tại labm.io.vn, đang alpha test, doanh nghiệp đăng ký miễn phí dùng thử ngắn qua contact". Chỗ đặt: (1) `README.md` mục "## Từ tác giả" cạnh "Liên hệ"; (2) `AboutWindow.xaml` 1 dòng dưới version. Làm kèm release chung, không release riêng.

## Cải tiến cân nhắc

- [ ] **Cap 100 dòng bảng bìa** — lô >100 file chỉ liệt kê 100 dòng đầu + "… và N file nữa". Nếu user phàn nàn thì tăng hằng số hoặc bỏ cap.
- [ ] **`release.yml` truyền `-p:Version=$VER`** — hiện version lấy từ csproj, tag lệch csproj thì bìa in sai version. Một dòng fix.
- [ ] **Giới hạn đã biết của fix duplex DEVMODE** — nếu app bị kill cứng giữa `SetPrinter` và restore (hoặc driver treo quá 60s rồi process chết), `dmDuplex` của máy in kẹt ở giá trị đã sửa; chưa có bước đối soát lúc khởi động. Upgrade path: lưu giá trị gốc xuống đĩa + khôi phục khi app start.

## Từ rà soát 2026-10-29 (còn tồn)

- [ ] **Watermark/Merge path còn nuốt `Copies`/`PagesPerSheet`/`Quality`** — `WatermarkPrintEngine` giờ tôn trọng page range/parity, nhưng N-up và quality vẫn bị browser render bỏ qua; `MergePrintEngine` in theo Copies của file đầu.
- [ ] **Office PowerPoint không áp `PaperSize`/`Orientation`.**
- [ ] **LibreOffice + shell fallback bỏ qua hầu hết option, không cảnh báo trong UI.**
- [ ] **Chuỗi tiếng Anh cứng chưa qua i18n** — tooltip `MainWindow.xaml:355,370` (key `Settings.PrefsButton`/`PropsButton` ĐÃ có sẵn); `MainWindow.xaml.cs:416` `Name = "🔄 Scan printers…"`; toast/banner tiếng Việt cứng trong `PrintBatchOrchestrator.cs:128,136,145,198`; state pill hiện `Queued/Converting/...` EN.
- [ ] **MCP HTTP token chỉ bảo vệ HTTP transport** — stdio (AI client spawn trực tiếp) không cần token vì cùng user; nếu cần phân quyền đa user thì phải chuyển sang HMAC per-request.
- [ ] **`LockedBy` của sheet Excel (protected view)** — file mở read-only trong Excel vẫn có thể fail `Workbooks.Open` mà không có thông báo rõ cho user.

## Từ fix "grayscale không override được máy in" (2026-11-14)

- [ ] **PDF in toàn bộ qua browser render không có đường ép xám** — `BrowserPrintEngine` nhánh full-PDF/non-PDF (`:105-117`) render thẳng file nguồn, không có HTML để chèn `filter: grayscale(1)` như các nhánh slice/watermark/merge. Trên driver bỏ qua `dmColor`, PDF in toàn bộ kèm option (scale/khổ/chiều/quality) vẫn ra màu. Upgrade path: bọc file nguồn trong HTML wrapper có CSS, hoặc chuyển sang `WindowsPdfRasterizer` + CSS.
- [ ] **`dmColor` chưa được xác minh trên máy in thật** — readback chỉ so DEVMODE lưu trong spooler, không chứng minh job đã in xám (Word/Excel/PPT/printto tự dựng DEVMODE riêng từ driver). Cần 1 tờ in thử có thanh đỏ 100% để chốt. Cùng lớp với mục duplex DEVMODE ở trên.
- [ ] **`SetPrinter` level 2 khi user không phải admin chưa kiểm** — duplex đã chạy production nhưng chưa có bằng chứng trên tài khoản standard user. Nếu fail, `PRINTER_INFO_9` (per-user) là đường thay thế.
- [ ] **CSS `filter: grayscale(1)` trong Chromium print pipeline chưa verify** — nếu Chromium bỏ filter khi in, các đường render (ảnh/TXT/watermark/merge/slice) quay lại phụ thuộc driver. Kiểm bằng 1 lệnh in thử; nếu fail thì chuyển sang chuyển đổi pixel thật.

## Từ đợt rà soát option bị bỏ qua im lặng (2026-11-14, đợt 2)

- [ ] **`PaperSource` (chọn khay) chỉ có tác dụng ở đường PDF→GDI** — `PrintConfig.PaperSource` lưu TÊN THÂN THIỆN ("Khay cassette", "Nạp tay (Manual)" — `PrinterService.FriendlyTrayName:242-251`), không phải mã `DC_BINS` của driver. Muốn ép qua DEVMODE `dmDefaultSource@88` (bit `DM_DEFAULTSOURCE=0x200`, LƯU Ý không phải `DM_DEFAULT_SOURCE`/`0x20` — tên đó không tồn tại, `0x20` là `DM_POSITION`) thì chỉ ra được loại khay chung (Auto=7/Cassette=14/Manual=4/Tractor=8), không ra "Khay 2". Upgrade path: đọc `DeviceCapabilities(DC_BINS)` để lấy mã khay thật, lưu mã đó trong config thay vì tên.
- [ ] **`ScaleMode`/`ScalePercent` bỏ qua ở Office/LibreOffice/shell** — không vá bằng `dmScale` được: `Zoom` đã scale trong engine (GdiPrintEngine.ZoomFactor, CdpPrintParams.ScaleFor) nên ghi `dmScale` nữa là scale HAI lần; `ShrinkToPrintable`/`FitToPrintable`/`Fill` không biểu diễn được bằng một phần trăm tuyến tính; và `dmScale=0` không có định nghĩa chính thức (không được ghi 0). Upgrade path: Word dùng `PrintZoomPaperWidth/Height` trong `PrintOut`, Excel dùng `PageSetup.Zoom`.
- [ ] **`PagesPerSheet` (N-up) chỉ có ở GDI** — Office/LibreOffice/shell/browser bỏ qua. DEVMODE `dmNup` nằm trong driver private data, ngoài phần công khai. Upgrade path: Word/Excel in qua `Application.PrintOut` với nhiều trang/tờ không có API; phải raster hóa hoặc chấp nhận giới hạn.
- [ ] **`WatermarkText` bị bỏ im lặng với DOCX/XLSX/PPTX** — `OfficeComPrintEngine` đứng trước trong registry nên `WatermarkPrintEngine` không bao giờ nhận file Office; Word COM không có API watermark. Hiện chỉ ghi nhận, chưa báo lỗi. Upgrade path: chặn sớm kèm `UNSUPPORTED_OPTION` để người dùng biết.
- [ ] **`Quality` qua DEVMODE có thể là no-op trên một số driver** — đã gate bằng `dmFields & DM_PRINTQUALITY` và ghi log khi driver không khai báo, nhưng chưa kiểm trên máy thật. Upgrade path: đọc `DeviceCapabilities(DC_ENUMRESOLUTIONS)` để chọn DPI thật.
- [ ] **`Copies` bị bỏ qua trên LibreOffice** — `soffice --pt` không có tham số số bản, 4 bản chỉ ra 1. Upgrade path: lặp lệnh N lần như SpoolPrintEngine đang làm.
- [ ] **Xuất PDF (máy ảo) qua browser vẫn giữ màu khi chọn Grayscale** — `BrowserPrintEngine` nhánh `pdfOut is not null` copy nguyên `out.pdf`. `GdiPrintEngine.ToGrayscalePng` đã có sẵn để dùng, nhưng đường browser phải raster→đổi xám→`PdfImageWriter.Write` chứ không copy file được.

## Từ đợt UI theo khả năng máy in + nút "Lấy theo máy in" (2026-11-14, đợt 4)

- [ ] **Nhãn nhóm vẫn nói "Giấy & màu" khi dòng Màu bị ẩn** — `Settings.GroupPaperColor` ở `Strings.json:296`; cùng gốc với `Settings.HintParity` (`:346`) vẫn nhắc "in 2 mặt trước" khi dòng 2 mặt bị ẩn. Sửa cùng lượt: đổi nhãn theo trạng thái, hoặc trung tính hoá chữ.
- [ ] **MCP không phân biệt "driver báo không" với "chưa đọc được"** — `list_printers`/`pick_printer` vẫn export `color:false`/`duplex:false` cho `CapabilitySupport.Unknown` (`PrintTools.cs:45-46,255-256`). AI caller không biết là chưa đọc được. Cần quyết backward-compat trước khi đổi payload shape; `PickBestPrinter` (`:290-291`) đang đúng ngữ nghĩa nên giữ nguyên.
- [ ] **Mapping capability trong `Describe` chưa có unit test** — rỗng/chứa `Unknown` → `Unknown`, chứa giá trị hỗ trợ → `Supported`, else `NotSupported` (`PrinterService.cs:153-167`). Chỉ được phủ gián tiếp ở tầng Core. Sửa thứ tự nhánh `Unknown` sẽ hồi quy âm thầm thành "ẩn nhầm option" mà không test nào bắt. Cần seam `PrintQueue` để dựng `PrintCapabilities` giả.
- [ ] **`ReadPrinterDefaults` không có test** — cần máy in thật hoặc seam `PrintQueue`. 4 mapper `ColorFromDm`/`DuplexFromDm`/`OrientationFromDm`/`PaperNameFromDm` đang `private`; có sẵn `InternalsVisibleTo` nên đổi sang `internal` là test pattern byte-offset hiện có phủ được mapping ngược (`PrinterDialogs.cs:461,521-563`).
- [ ] **Nút "Lấy theo máy in" im lặng khi driver chỉ set `dmPaperSize`** — `PaperNameFromDm` trả null nếu width/length = 0, combo giấy vẫn đứng "theo máy in", user tưởng nút hỏng (`PrinterDialogs.cs:548`). Cần map `dmPaperSize` → tên khổ trong `PaperCatalog` cho cả ID lạ.
- [ ] **Nút thứ 3 trong hàng máy in có thể bị clip ở min width 720px** với tên máy in dài — thấy rõ ở tiếng Nga (`PrintSettingsWindow.xaml:230-231`). Cần reflow hàng hoặc rút nhãn; chưa sửa vì phải kiểm 5 ngôn ngữ.
