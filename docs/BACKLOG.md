# Backlog

Việc còn lại của Printonator — ưu tiên cho các lần làm sau. Cập nhật khi làm xong (xóa hoặc chuyển sang "Đã làm").

## Fix nhỏ (nên làm trong lần fix lỗi kế tiếp)

- [ ] **Test UI `PrintSelected_DoesNot_Duplicate_Rows` không tin được trên máy dev** — paste clipboard không vào được ("chờ 2 dòng — thấy 0"), fail lê thê dù code không đổi (đã A/B worktree xác nhận có sẵn từ trước). Sửa cách seed file vào hàng đợi (dùng API nội bộ thay vì Ctrl+V), hoặc đánh dấu skip trên máy dev.
- [ ] **Banner lỗi trang bìa hiện màu vàng** — `ErrorCodes.SpoolerFailed` nằm trong danh sách banner vàng + nút "Thử lại". Với lỗi bìa nên cân nhắc banner đỏ (mất trang bìa là mất hẳn, retry không tự gắn lại). Quyết: giữ vàng (có retry) hay đổi đỏ.
- [ ] **Xóa key i18n mồ côi `Settings.HintAutoOrientationLock`** — tồn tại sau khi bỏ tick AutoOrientation khỏi Cài đặt in. Không ai dùng; xóa khỏi `Keys.cs` + `Strings.json` (5 ngôn ngữ).
- [ ] **Cột Settings trong MainWindow vẫn tiếng Việt ở UI EN/ZH/RU/JA** — đã có mapper `LocalizeSummary` cho TRANG BÌA (`PrintBatchOrchestrator.cs`), áp lại cho cột Settings bằng converter/rename để UI và bìa nhất quán.

## Vấn đề môi trường / của user

- [ ] **File `Downloads\0.Tổng hợp công việc HC thiết bị đo lường năm 2024.xlsx` làm Excel treo khi `Workbooks.Open`** — probe bìa đã tự vệ (hết hạn 15s + kill EXCEL spawn, không để mồ côi), nhưng lúc IN THẬT job vẫn chờ timeout 60s rồi lỗi. Cần user mở file trong Excel tay để xem Excel báo gì (file corrupt? protected view?), hoặc skip file này.

## Chưa làm (đã duyệt từ trước)

- [ ] **Quảng cáo labm.io.vn** (user duyệt 2026-08-28, làm kèm fix lỗi/release kế): "phần mềm quản lý phòng thí nghiệm (thử nghiệm + đo lường, hiệu chuẩn kiểm định thiết bị đo) tại labm.io.vn, đang alpha test, doanh nghiệp đăng ký miễn phí dùng thử ngắn qua contact". Chỗ đặt: (1) `README.md` mục "## Từ tác giả" cạnh "Liên hệ"; (2) `AboutWindow.xaml` 1 dòng dưới version. Làm kèm release chung, không release riêng.

## Cải tiến cân nhắc

- [ ] **Cap 100 dòng bảng bìa** — lô >100 file chỉ liệt kê 100 dòng đầu + "… và N file nữa". Nếu user phàn nàn thì tăng hằng số hoặc bỏ cap.
- [ ] **CI chạy đủ test** — `ci.yml` hiện chỉ chạy Core (đã thêm Spool/Mcp vào `test_full.ps1` nhưng CI chưa). Thêm `Spool.Tests` + `Mcp.Tests` + gate i18n vào `ci.yml`.
- [ ] **`release.yml` truyền `-p:Version=$VER`** — hiện version lấy từ csproj, tag lệch csproj thì bìa in sai version. Một dòng fix.
