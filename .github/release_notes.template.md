## Printonator v{VER}

SHA256: {HASH}

### Mới

- **In PDF trực tiếp không cần app PDF**: máy in không có handler cho file .pdf (UserChoice lỗi, không có printto) — trước báo "Không in được", giờ in ảnh GDI thẳng vào máy in, không cần chương trình đọc PDF.
- **Engine GDI mới**: hỗ trợ in 2 mặt (LongEdge/ShortEdge), ép màu/đen trắng, in nhiều trang/tờ (N-up 2,4,6,9,16). PDF in trực tiếp qua GDI, không qua browser/shell.
- **Watch folder chặn loop**: khi máy in mặc định là máy PDF ảo (Print to PDF), app không auto-in để tránh vòng lặp (xuất PDF → watcher thấy → tự in → vô hạn). File PDF trong watch folder cũng giữ lại chờ người dùng bấm in.

### Cải tiến

- **Banner lỗi thông minh hơn**: nút hành động chỉ hiện cho đúng loại lỗi có thể retry (SpoolerFailed/PrinterNotFound), không còn hiện "Thử kết nối lại" khi lỗi file hỏng/hết giấy.
- **Popover thông báo compact + scroll**: khi in nhiều file, danh sách thông báo có thanh cuộn, item co gọn hơn.
- **DPI in PDF tăng 150→300**: rõ nét hơn khi in ra giấy.
- **Không mất chữ mép phải**: vẽ ảnh trong vùng in được của driver (trừ hard margin), không bị clip.
- **Installer tự đóng app**: không cần tắt tay trước khi cài bản mới.
- **Máy ảo xuất PDF sạch**: file PDF copy trực tiếp, không qua browser — không còn chụp UI viewer vào file xuất.

### Đã sửa

- Lỗi in PDF "không tìm thấy máy in" qua watch folder (sentinel "mặc định" không resolve được).
- Lỗi "in xong" hiện popup dù file lỗi — giờ báo "dừng do lỗi" đúng.
- Lỗi in PDF tới máy ảo (Microsoft Print to PDF) báo lỗi không xác định (File.Copy trùng file gốc).