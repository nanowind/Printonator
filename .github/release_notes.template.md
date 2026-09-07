## Printonator v{VER}

SHA256: {HASH}

### Đã sửa

- Lỗi in PDF chỉ ra 1 trang dù file có nhiều trang (đặc biệt với PDF tạo từ Word/Excel) — giờ đếm đúng số trang và in đủ.
- Lỗi chọn in 1 mặt nhưng máy in vẫn ra 2 mặt — giờ ép đúng chế độ 1 mặt/2 mặt theo cài đặt bạn chọn.
- Lỗi bỏ tick "Xóa file đã in" trong popup "In xong" nhưng bấm nút X (đóng cửa sổ) vẫn xóa file — giờ chỉ xóa khi bạn bấm OK.
- Lỗi bấm Tạm dừng/Tiếp tục liên tục làm lô in kẹt "Đang xử lý" mãi không in tiếp — giờ in vẫn chạy bình thường dù bạn bấm nhanh nhiều lần.
- Lỗi máy in bị treo (kẹt giấy, driver treo) làm file kẹt "Đang xử lý" vô thời hạn — giờ tự dừng sau 2 phút và báo rõ để bạn kiểm tra máy in rồi in lại.

### Cải tiến

- Làm lại phần chọn engine in để ổn định hơn: nếu một engine lỗi, app tự động thử engine khác thay vì báo lỗi dừng.
