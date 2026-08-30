## Printonator v{VER}

SHA256: {HASH}

### Mới

- **Chế độ Lite/Full**: mặc định giao diện gọn, ai cần nhiều tính năng hơn thì bật Full trong Cài đặt.
- **Quản lý cấu hình in**: lưu/đổi tên/xóa/áp dụng preset (bộ cài đặt in nhanh). Xuất/nhập preset ra file.
- **Duyệt in**: job từ AI (MCP) chờ người duyệt — thanh duyệt nổi trên đầu danh sách.
- **Hủy lô**: dừng/hủy lô in đang chạy, kể cả file đang in (dừng engine thật).
- **Khôi phục hàng đợi**: đóng app giữa chừng — mở lại thấy danh sách file + máy in như cũ.
- **Trang bìa**: in trang bìa ghi tên lô, ngày, số lượng file trước mỗi lô.
- **Gộp file thành 1 bản in**: chọn nhiều file, bật "Gộp thành 1 bản in" — in ra 1 file PDF duy nhất.
- **Dấu mờ (watermark)**: thêm chữ mờ lên từng trang (chọn chữ, độ mờ trong Cài đặt in).
- **Chọn máy in cho từng file**: mỗi file có thể chọn máy in riêng (không phải dùng chung 1 máy cho cả lô).
- **Theo dõi thư mục**: thả file vào thư mục chỉ định → app tự thêm vào hàng đợi (có thể bật tự in).
- **Lịch sử in**: tự động lưu lịch sử (1000 bản gần nhất) — file gì, in máy nào, lúc nào, kết quả.
- **Menu chuột phải**: click phải file → "In với Printonator" — mở app + in luôn.
- **Trang cuối (last)**: gõ `last` hoặc `last3` vào ô khoảng trang — in trang cuối hoặc N trang cuối.
- **CLI (dòng lệnh)**: `tools\printonator.ps1 list-printers` — in qua MCP server từ terminal.
- **5 ngôn ngữ**: Tiếng Việt, English, 中文, Русский, 日本語 — chọn lúc cài hoặc trong Cửa sổ Giới thiệu.
- **Chế độ đầy đủ (Full)**: bật trong Cửa sổ Giới thiệu để hiện các tính năng nâng cao.

### Đã sửa

- Hủy job đang in không dừng engine thật → nay dừng hẳn (job về Cancelled, không lỗi).
- Đóng app <8s sau khi mở file đôi khi lỗi rò rỉ → cancel an toàn, không crash.
- Không tìm thấy máy in → thử lại có nút Retry + timeout 15s.
- In Excel nhiều sheet: trước chỉ in 1 sheet → nay in tất cả (bỏ qua sheet trống).
- Máy in ảo (PDF/XPS): trước tạo file PDF cạnh file gốc, không lỗi "Save As".
- Giao diện: cột header dài (tiếng Nga/Trung) không bị cắt mất chữ; status pill rộng hơn.

### Cải tiến

- Làm lại phần lõi cho ổn định: tách BatchOrchestrator + FooterController khỏi MainWindow (giảm ~300 dòng).
- Mỗi bước in có CancellationToken thật — hủy nhanh, không chờ timeout engine.
- 5 ngôn ngữ giao diện (chọn lúc cài), 392 chuỗi × 5 = 1960 bản dịch.
- 139 bài kiểm tra tự động (Core 102 + Spool 4 + Mcp 6 + UI 27).
- Bản beta chưa ký số — SmartScreen có thể cảnh báo; nhấn "More info → Run anyway" nếu tin tác giả.