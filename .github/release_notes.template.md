## Printonator v{VER}

SHA256: {HASH}

Bản này sửa các lỗi in PDF: chọn chiều giấy (ngang/dọc) giờ ra đúng chiều, và in PDF qua máy in ảo (Microsoft Print to PDF) ra đúng theo cài đặt bạn chọn.

### Đã sửa

- Chọn in khổ Ngang mà trang vẫn ra khổ Dọc — giờ in đúng chiều ngang như thiết kế.
- Chọn khổ Dọc cho file có trang ngang thì nội dung bị co nhỏ, lệch xuống góc dưới phải — giờ thu vừa trang, canh giữa, chữ đọc bình thường.
- In ra PDF (Microsoft Print to PDF): chọn in trang nào, khổ nào, chiều nào đều được tôn trọng — trước đây chỉ sao chép y nguyên file gốc, bỏ qua mọi cài đặt.
- In ra PDF mà file kết quả đang mở ở chương trình khác thì báo lỗi rõ ràng thay vì dừng giữa chừng.

### Cải tiến

- Làm lại phần lõi in PDF cho ổn định hơn: tự nhận biết khổ giấy thực tế khi in, giảm lỗi lệch trang.