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
