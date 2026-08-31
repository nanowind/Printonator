## Printonator v{VER}

SHA256: {HASH}

### Mới

- **Theo dõi thư mục — chế độ máy in dùng chung (Printing Server)**: cài Printonator trên máy chung phòng ban, chọn 1 thư mục dùng chung qua LAN. Ai ném file vào → tự động in ngay vào máy in mặc định Windows, cấu hình theo file (không ép A4/dọc/2 mặt). Có thông báo "Đã in" kèm tên file trên app — biết ai in gì.

### Cải tiến

- **Nhắc máy in khởi động**: thay badge tự ẩn 6s (lúc hiện lúc ẩn) bằng hint có nút ✕ — bạn chủ động đóng khi thấy ổn.
- **Ký số bản phát hành (minisign)**: từ bản này installer được ký bằng chữ ký số, public key nhúng trong app — xác thực được bản cập nhật chính chủ. Hết cảnh báo SmartScreen (hoặc giảm nhiều).
- 397 bài kiểm tra tự động (Core 103 + Spool 4 + Mcp 6 + UI 24/27).

### Đã sửa

- Lỗi CI format verify (whitespace) — CI xanh trở lại.