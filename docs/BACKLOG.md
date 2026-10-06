# Backlog

Việc còn lại của Printonator — ưu tiên cho các lần làm sau. Cập nhật khi làm xong (xóa hoặc chuyển sang "Đã làm").

## Fix nhỏ (nên làm trong lần fix lỗi kế tiếp)

- [ ] **Test UI `PrintSelected_DoesNot_Duplicate_Rows` không tin được trên máy dev** — paste clipboard không vào được ("chờ 2 dòng — thấy 0"), fail lê thê dù code không đổi (đã A/B worktree xác nhận có sẵn từ trước). Sửa cách seed file vào hàng đợi (dùng API nội bộ thay vì Ctrl+V), hoặc đánh dấu skip trên máy dev.
- [ ] **Banner lỗi trang bìa hiện màu vàng** — `ErrorCodes.SpoolerFailed` nằm trong danh sách banner vàng + nút "Thử lại". Với lỗi bìa nên cân nhắc banner đỏ (mất trang bìa là mất hẳn, retry không tự gắn lại). Quyết: giữ vàng (có retry) hay đổi đỏ.
- [ ] **Xóa key i18n mồ côi `Settings.HintAutoOrientationLock`** — tồn tại sau khi bỏ tick AutoOrientation khỏi Cài đặt in. Không ai dùng; xóa khỏi `Keys.cs` + `Strings.json` (5 ngôn ngữ).
- [ ] **Cột Settings trong MainWindow vẫn tiếng Việt ở UI EN/ZH/RU/JA** — đã có mapper `LocalizeSummary` cho TRANG BÌA (`PrintBatchOrchestrator.cs`), áp lại cho cột Settings bằng converter/rename để UI và bìa nhất quán.
- [ ] **Bulk bar (chọn nhiều file) render chuỗi tiếng Anh cứng `"1 files selected"`** — không đi qua i18n (test `MainWindowTests.MultiSelect_Shows_BulkBar` kỳ vọng `"Đã chọn 2 file"`). Đã xác nhận CÓ SẴN từ trước (A/B worktree HEAD sạch). Cần đưa qua `L10n` (thêm key cho số ít/số nhiều).
- [ ] **2 UITest fail có sẵn, đã xác minh (không phải regression)** — `MainWindowTests.App_Launches_Empty_Shows_EmptyState` (assert null ở `MainWindowTests.cs:67`) và `MainWindowTests.MultiSelect_Shows_BulkBar` (fail ở `:148` hoặc `:137/:283` tuỳ lần — clipboard paste không ổn định). Ghi chú này BỔ SUNG cho mục `PrintSelected_DoesNot_Duplicate_Rows` ở trên (cùng gốc clipboard) — gộp lại khi làm.

## Vấn đề môi trường / của user

- [ ] **File `Downloads\0.Tổng hợp công việc HC thiết bị đo lường năm 2024.xlsx` làm Excel treo khi `Workbooks.Open`** — probe bìa đã tự vệ (hết hạn 15s + kill EXCEL spawn, không để mồ côi), nhưng lúc IN THẬT job vẫn chờ timeout 60s rồi lỗi. Cần user mở file trong Excel tay để xem Excel báo gì (file corrupt? protected view?), hoặc skip file này.

## Chưa làm (đã duyệt từ trước)

- [ ] **Quảng cáo labm.io.vn** (user duyệt 2026-08-28, làm kèm fix lỗi/release kế): "phần mềm quản lý phòng thí nghiệm (thử nghiệm + đo lường, hiệu chuẩn kiểm định thiết bị đo) tại labm.io.vn, đang alpha test, doanh nghiệp đăng ký miễn phí dùng thử ngắn qua contact". Chỗ đặt: (1) `README.md` mục "## Từ tác giả" cạnh "Liên hệ"; (2) `AboutWindow.xaml` 1 dòng dưới version. Làm kèm release chung, không release riêng.

## Cải tiến cân nhắc

- [ ] **Cap 100 dòng bảng bìa** — lô >100 file chỉ liệt kê 100 dòng đầu + "… và N file nữa". Nếu user phàn nàn thì tăng hằng số hoặc bỏ cap.
- [ ] **CI chạy đủ test** — `ci.yml` hiện chỉ chạy Core (đã thêm Spool/Mcp vào `test_full.ps1` nhưng CI chưa). Thêm `Spool.Tests` + `Mcp.Tests` + gate i18n vào `ci.yml`.
- [ ] **`release.yml` truyền `-p:Version=$VER`** — hiện version lấy từ csproj, tag lệch csproj thì bìa in sai version. Một dòng fix.
- [ ] **Giới hạn đã biết của fix duplex DEVMODE** — nếu app bị kill cứng giữa `SetPrinter` và restore (hoặc driver treo quá 60s rồi process chết), `dmDuplex` của máy in kẹt ở giá trị đã sửa; chưa có bước đối soát lúc khởi động. Upgrade path: lưu giá trị gốc xuống đĩa + khôi phục khi app start.

## Từ rà soát 2026-10-06 (option chết + i18n)

- [ ] **`PaperSource` (khay giấy) là option chết** — UI nạp khay thật từ máy in, lưu vào Preset + MCP `print_files` quảng cáo tham số `paperSource`, nhưng KHÔNG engine nào đọc (0 hit trong `Printonator.Spool`). Quyết: wire vào GDI (`pd.DefaultPageSettings.PaperSource`) + bỏ/deprecate tham số MCP, HOẶC ẩn UI. Phải quyết cả 2 phía UI và MCP.
- [ ] **`ScaleMode`/`ScalePercent` (zoom/fit/fill/shrink) không tác dụng với PDF** — chỉ `CdpPrintParams` đọc, nhưng PDF luôn đi `GdiPrintEngine` (đăng ký trước, CanHandle=PDF) — engine này KHÔNG đọc ScaleMode → zoom/fit không tác dụng với PDF trên máy in vật lý. Ảnh/TXT thì có tác dụng (đi browser). Cần wire vào GDI (giữ nguyên công thức mặc định để không regression).
- [ ] **`Booklet` là option chết** — không engine nào đọc; chọn nó chỉ set `PagesPerSheet=2`; hint quảng bá "gấp sách". Hoặc hiện thực imposition thật, hoặc ẩn.
- [ ] **`WatermarkPosition` chưa có picker** — engine hỗ trợ 5 vị trí nhưng UI ép cứng `"center"`.
- [ ] **Watermark/Merge path nuốt option** — bật watermark hoặc merge thì `PageRange`/`Parity`/`Copies`/`PagesPerSheet`/`Quality` bị bỏ qua.
- [ ] **Office PowerPoint không áp `PaperSize`/`Orientation`.**
- [ ] **LibreOffice + shell fallback bỏ qua hầu hết option, không cảnh báo trong UI.**
- [ ] **Chuỗi tiếng Anh cứng chưa qua i18n** — tooltip `MainWindow.xaml:355,370` (key `Settings.PrefsButton`/`PropsButton` ĐÃ có sẵn); `MainWindow.xaml.cs:416` `Name = "🔄 Scan printers…"`; toast/banner tiếng Việt cứng trong `PrintBatchOrchestrator.cs:128,136,145,198`; state pill hiện `Queued/Converting/...` EN. Riêng cột Settings đã có mục riêng ở "Fix nhỏ".
- [ ] **Dọn key mồ côi khi ẩn option** — `Option.Booklet`, `Settings.OptionPaperSource`, `Settings.HintPaperSource`, `Option.TrayAsPrinter`, `Option.TrayUnknown` (+ `Settings.HintAutoOrientationLock` đã ghi trước đó).
