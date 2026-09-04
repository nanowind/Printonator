<img src="printonatorLogo.png" width="64" align="left" alt="Printonator" />

# Printonator

**In cả loạt tài liệu bằng một nút bấm.**

Kéo thả N file vào (PDF, Word, Excel, PowerPoint, ảnh, TXT...), chọn máy in, bấm in một phát — Printonator lo phần còn lại. Chạy 100% trên máy bạn, dữ liệu không bao giờ rời máy.

Windows 10/11 · Miễn phí · Mã nguồn mở (MIT)

## Printonator để làm gì

In ấn tưởng đơn giản mà ngốn thời gian kinh khủng: mở từng file, bấm Ctrl+P, chọn máy in, chọn khổ giấy, in... rồi lặp lại vài chục lần. Hồ sơ thầu, hóa đơn, hợp đồng — mỗi loại lại cần in khác nhau về số bản, hai mặt, khổ A4 hay A3.

Printonator gom toàn bộ về một màn hình:

- Thêm file hàng loạt bằng kéo thả, dán từ clipboard, hoặc nút Add files.
- Cấu hình in áp cho cả lô hoặc riêng từng file: số bản, 2 mặt, khổ giấy, màu, khay, khoảng trang...
- Bấm in một phát — app tự chọn cách in phù hợp nhất với phần mềm máy bạn đang có.
- Nối với AI qua MCP để AI in giúp, kèm guardrail an toàn.
- Giao diện 5 ngôn ngữ (Việt / English / 中文 / Русский / 日本語) — đổi trong cửa sổ Thông tin.

## Cài đặt

Tải installer mới nhất từ [GitHub Releases](https://github.com/nanowind/Printonator/releases) rồi chạy. Setup tự cài .NET 8 Desktop Runtime nếu máy chưa có, không cần làm gì thêm.

> **Bản beta chưa ký số** — SmartScreen có thể cảnh báo; nhấn "More info → Run anyway" nếu tin tác giả.

## Hướng dẫn sử dụng

Xem hướng dẫn đầy đủ kèm hình minh họa: **[docs/USER_GUIDE.md](docs/USER_GUIDE.md)** — các bước cơ bản (kéo thả, chọn máy, cấu hình in), preset, theo dõi thư mục (máy in dùng chung), duyệt in từ AI, lịch sử in, chế độ Lite/Full, 5 ngôn ngữ.

![Giao diện chính](docs/screenshots/main_full.png)

## Cách dùng

1. Kéo thả file vào cửa sổ (hoặc Ctrl+V, hoặc nút Add files).
2. Chọn máy in ở phía trên.
3. Muốn chỉnh gì thì mở **Cấu hình in**: số bản, 2 mặt, khổ giấy, màu, khay, khoảng trang, preset...
4. Bấm **Print**. Cột Status cho biết từng file in xong hay lỗi; xong cả lô là có popup "Đã in xong".

Mấy điểm hay nhớ:

- **Chọn trang kiểu Word section**: gõ `2,5`, `3-4`, `S2:1-3`, kể cả `last` (trang cuối) — có preview trước khi in, khỏi lo nhầm trang.
- **Lưu preset**: bộ cấu hình hay dùng (ví dụ "Hợp đồng 2 mặt") lưu lại để dùng tiếp, xuất/nhập ra file để mang sang máy khác.
- **Mở file xem/sửa**: double-click vào dòng, đóng file xong app tự nạp bản mới nhất.
- **In lại file đã in**: chọn lại rồi in, không cần thêm lại.
- **In từng file vào máy riêng**: mỗi dòng có combo chọn máy in.
- **Gộp thành 1 bản in**: chọn nhiều file để in chung 1 lần đẩy xuống máy.
- **Trang bìa**: in một trang bìa (tên lô, ngày, số file) trước lô.
- **Dấu mờ (watermark)**: chèn chữ mờ vào bản in, chỉnh độ đậm nhạt.
- **Theo dõi thư mục (máy in dùng chung)**: cài trên máy chung phòng ban, chọn 1 thư mục qua LAN — ai ném file vào là tự in ngay vào máy in mặc định, cấu hình theo file, có thông báo "Đã in" kèm tên file.
- **Lịch sử in**: app nhớ các lần in gần đây; lần sau mở lại còn cả hàng đợi chưa in.

## In bằng gì trên máy

Printonator không nhét thư viện in vào installer. Nó dùng chính phần mềm có sẵn trên máy bạn, theo thứ tự ưu tiên:

1. **MS Office** (Word/Excel/PowerPoint) nếu có — giữ đúng định dạng trang và section.
2. **LibreOffice** nếu không có MS Office.
3. **Chrome/Edge headless** cho PDF, ảnh, TXT — áp được khoảng trang, scale, khổ giấy.
4. **Shell in mặc định** — phương án cuối cùng.

Nhờ vậy app nhẹ, và bản in ra gần giống hệt những gì bạn thấy khi mở file.

## AI in giùm (MCP)

Printonator kèm MCP server để Claude hoặc assistant tương thích in giúp bạn. Mặc định an toàn: AI phải được bạn duyệt trước khi in, giới hạn số trang, chỉ in vào máy được cho phép, có nhật ký đầy đủ.

Ví dụ cho AI in vào máy "Microsoft Print to PDF":

```powershell
$env:PRINTONATOR_REQUIRE_APPROVE="false"
$env:PRINTONATOR_ALLOWED_PRINTERS="Microsoft Print to PDF"
dotnet run --project src/Printonator.Mcp
```

Hướng dẫn đầy đủ: [docs/MCP.md](docs/MCP.md).

## Build từ mã nguồn

Cần .NET 8 SDK:

```bash
dotnet build Printonator.sln                # build toàn bộ
dotnet test Printonator.sln                 # Core 103 + Spool 4 + Mcp 6 + UI 27 = 140 tests
dotnet run --project src/Printonator.UI     # chạy app
dotnet run --project src/Printonator.Mcp    # chạy MCP server
```

Cấu trúc: `Printonator.Core` (hàng đợi, state machine, preset, guard an toàn, lịch sử) → `Printonator.Spool` (máy in + engine: Office COM, LibreOffice, Chrome/Edge headless, watermark, gộp file, trang bìa, shell) → `Printonator.UI` (WPF, 5 ngôn ngữ, Lite/Full mode) và `Printonator.Mcp` (13 tools). Chi tiết ở [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) và [docs/COMPARISON_PRINT_CONDUCTOR.md](docs/COMPARISON_PRINT_CONDUCTOR.md).

## Từ tác giả

Ngoài Printonator, tôi còn làm **phần mềm quản lý phòng thí nghiệm** — thử nghiệm, đo lường, hiệu chuẩn, kiểm định thiết bị đo — tại **[labm.io.vn](https://labm.io.vn)**. Hiện đang **alpha test**: doanh nghiệp đăng ký dùng thử miễn phí thời gian ngắn qua contact trên web.

## Liên hệ

Email: phucnguyenqlcn@gmail.com

## Giấy phép

MIT — miễn phí, mã nguồn mở. Xem [LICENSE](LICENSE).
