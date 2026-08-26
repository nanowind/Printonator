# Printonator — Concept & Abstract Design (v2)

> Bản quyền: MIT — miễn phí, mã nguồn mở, mọi người dùng.
> Mục tiêu: phần mềm in hàng loạt (bulk/batch printing) cho Windows 10/11 + **tích hợp MCP để AI in giùm**, thay thế bản quyền thương mại kiểu Print Conductor.

---

## 1. Vision

**Một câu:** Kéo thả N file → chọn máy in → bấm một nút — **hoặc nhờ AI in giùm qua MCP** — cả loạt chạy xong, biết chính xác file nào in được, file nào lỗi vì sao.

**Vấn đề đang có:**
- In từng file bằng tay: mở → Ctrl+P → chọn máy → OK, lặp lại vài chục lần, tốn thời gian và sót.
- Phần mềm in hàng loạt thương mại (Print Conductor, priPrinter, FinePrint...) trả phí theo máy, giới hạn chức năng.
- Công việc thực tế: hồ sơ thầu, hóa đơn, GCN, hợp đồng, báo cáo — hàng tá file PDF/DOCX/XLSX cần in với quy định khác nhau (số bản, 2 mặt, khổ giấy).
- **Mới:** AI (Claude Code, Hermes, assistant khác) đang dần là người soạn tài liệu — nhưng chưa có ai "in giùm". Văn phòng vẫn phải tự mở file bấm Ctrl+P.

**Góc mở:** in hàng loạt là nhu cầu có thật, khắp văn phòng VN. MIT → cộng đồng cùng phát triển. AI-native (MCP) → in được ngay từ câu lệnh nói (chat) thay vì click.

---

## 2. Người dùng & tình huống chính

| Persona | Tình huống |
|---|---|
| NV hành chính/kế toán | In 50 hóa đơn, 30 hợp đồng định kỳ |
| Đội kỹ thuật/hiệu chuẩn | In GCN hàng loạt cho khách, mỗi lô vài chục PDF |
| Văn phòng đấu thầu | Nén bộ hồ sơ (DOCX+PDF+Excel) ra giấy theo quy chuẩn |
| Người dùng AI (new) | Nói với AI: "in cho anh hồ sơ thầu trong thư mục X ra máy HP404 2 mặt" — AI gọi MCP Printonator |

**Không nhắm tới:** in công nghiệp lớn, quản lý printer fleet — phạm vi khác.

---

## 3. Tính năng (phân lớp theo milestone)

### v1.0 — Con đường chính: DROP → SELECT → IN
- Chọn file linh hoạt:
  - **Kéo & thả** nhiều file vào app HOẶC **copy-paste** (Ctrl+V) từ Explorer
  - **Multi-select:** click chọn từng file, **Ctrl+Click** chọn rời lẻ, **Shift+Click** chọn liên tiếp (range), **Ctrl+A** chọn hết
  - **Double-click** 1 file → tự mở app gốc (Word/Excel/PDF editor) để xem/sửa trực tiếp; sau khi sửa & đóng, app **tự nạp lại phiên bản mới nhất** (theo dõi file change) — in luôn đúng bản mới nhất, không in bản cũ cache
- **Bulk change (thay đổi hàng loạt theo nhóm):** chọn nhiều file → dải Bulk Action hiện ra: đổi **số bản, 2 mặt/1 mặt, khổ giấy** cho cả nhóm và **Apply** — giải quyết "10 file, 4 file in 2 mặt, 2 file in 2 bộ"
- **In trang chỉ định (page range) theo từng file:** mỗi file có ô "Pages" riêng — `2,5` (trang rời), `3-4` (khoảng), `1-2,7` (kết hợp), `All`. Ví dụ file A in trang 2,5; file B in trang 3-4 — hoàn toàn độc lập nhau
- **Section-aware (DOCX có section):** app đọc cấu trúc section từ file DOCX (document.xml → `sectPr`, page breaks) trước khi in, xây **bản đồ trang**: trang vật lý PDF ↔ (section, số trang trong section). Chọn trang theo 2 chế độ:
  - *Theo trang tài liệu* (liên tục): 1-20
  - *Theo section*: Section 1 (trang 1-2) / Section 2 (trang 1-8 = doc trang 3-10)…
  - Chọn "section 2 trang 1-3" → app tự map sang trang vật lý 3-5 — **hết cảnh nhầm "in trang 3 hóa ra là trang 1 section 2"**. Cảnh báo khi số trang người nhập không khớp section
- Chọn máy in / cấu hình từng file hoặc cả lô
- Nút "In tất cả" — tuần tự, báo rõ ✅ done / ⚠️ lỗi + lý do
- Log phiên: giờ, máy in, định dạng, trạng thái từng job

### v1.x — Hành vi "in cho công"
- Job profile / preset: "Hợp đồng 2 mặt", "Nháp 1 mặt"...
- CSV import (danh sách file, máy in, số bản, lưu ý)
- Folder watch: file mới → tự in theo rule
- Gộp tất cả → 1 PDF trước khi in (1 lần đẩy vào máy in, giảm khô/tịt giữa chừng)
- Lưu danh sách job để re-run lại lô cũ
- **MCP server chạy được từ UI (kèm app window) hoặc standalone headless**

### v2 — Mở rộng
- Watch folder + rule mạnh hơn (lọc theo tên, theo loại file)
- CLI headless: `printonator --files a.pdf b.docx --printer "HP404" --duplex`
- Backup/config sync giữa máy (file, no cloud)
- i18n: tiếng Việt + tiếng Anh
- In "cá nhân hóa" qua template placeholder (nâng cao)

### Bỏ qua ở MVP
In qua mạng/fleet, scheduling ngày giờ, app Android.

---

## 4. Phần mềm viết bằng gì? (câu trả lời trực tiếp)

**Đề xuất: C# / .NET 10 (WPF hoặc WinUI 3)** — dễ bảo trì nhất cho bài này. Lý do:

| Tiêu chí | C#/.NET ✅ | Python ⚠️ | Rust ⚠️ |
|---|---|---|---|
| UI Windows native (drag&drop, list 10,000 dòng) | WPF/WinUI3 trưởng thành | không có UI native xịn (PyQt/Tkinter yếu, còn lại giao diện web) | egui/iced mới, chưa chín cho desktop phức tạp |
| Windows Print Spooler / Printer API | native / first-class (System.Drawing.Printing, P/Invoke, XOR w/ WinRT) | pywin32 đủ dùng nhưng cập nhật thủ công | cần crate WinRT (windows-rs) — tự build nhiều |
| Packaging cho Win10/11 | 1 file .exe (publish single-file, AOT) | cần đóng gói PyInstaller to đầu, dính antivirus hay bắn | 1 exe gọn, tốt — nhưng đó là điểm duy nhất nổi trội |
| Thư viện chuyển đổi file | PDFium, LibreOffice bridge qua NuGet | pypdfium2, unoconv — ok | PDFium binding qua crate, LibreOffice cũng gọi được |
| NuGet phát triển khi ai (con người + AI) | Cộng đồng huge, formatter/linter/source-gen, debugger, hot reload | Đọc dễ, viết dễ, nhưng runtime nông, sót chữa lỗi type | Mạnh cộng đồng nhưng compile-time gắt, iteration chậm hơn hẳn |
| AI (Claude/GitHub Copilot) bảo trì giùm | .NET là bậc 1 (khối mã khổng lồ, docs chuẩn, dotnet CLI) | cũng mạnh — 2 ngôn ngữ AI thạo nhất | chuẩn nhưng chậm review, ít mẫu code GUI Windows |

**Kết luận:**
- **C#/.NET là chính.** Dễ bảo trì cho cả a lẫn AI: 1 ngôn ngữ, tooling 1 cửa, native Windows, single-file publish, Tài API spooler first-class.
- **Python dùng được** cho prototype thử nghiệm nhanh (demo luồng convert-in trong 1 tuần) hoặc làm lớp script test – nhưng không khuyến nghị cho app giao diện cuối.
- **Rust làm được** (hello world in PDF + cargo 'spool') nhưng chi phí bảo trì giao diện WPF kiểu Windows sẽ đè chết lợi ích perf vốn không phải điểm nghẽn (in 50 file → I/O + spooler là bottleneck, không phải CPU).

Nếu sau này thấy cần một core in cực gọn headless **hoặc tích hợp vào thứ gì khác**, có thể tách core bằng Rust **sau** khi luồng nghiệp vụ chứng minh — đừng bắt đầu bằng Rust.

---

## 5. Kiến trúc abstract

```
┌───────────────────────────────────────────────┐
│  UI (WPF/WinUI3) — drag&drop, job list, log,  │
│  preset, nút Start/Stop + trình duyệt MCP     │
└───────────────┬───────────────┬───────────────┘
                ▼               ▼
┌───────────────────────────────────────────────┐
│  Core (C#, .NET)                                │
│  • JobQueue: tuần tự (mặc định) + parallel ≤N  │
│  • Scheduler: retry, timeout, skip-on-error    │
│  • State machine: Created→Converting→           │
│     Spooling→Done / Error(reason)              │
└───┬────────────┬────────────┬─────────────────┘
    ▼            ▼            ▼
Format Engines  Spooler       MCP Server
┌────────────┐ ┌──────────┐ ┌────────────────┐
│ PDF→PDFium │ │ Windows  │ │ mcp://printona- │
│ Office→Libre│ │ Spooler  │ │  tor — expose   │
│ office →PDF │ │ (printer │ │ list/print/     │
│ Images→WIC  │ │ handle,  │ │ status/cancel,  │
│ TXT/CSV→raw │ │ DDI)     │ │ approval queue  │
└────────────┘ └──────────┘ └────────────────┘
```

**Các promise:**
1. **In file Office bằng chính app gốc của user — KHÔNG ép convert PDF** (giữ đúng cài đặt in trong file: page setup, margins, section, printer-specific):
   - Quét máy user đã cài gì → ưu tiên: **Microsoft Word/Excel/PowerPoint** (COM automation → PrintOut với đúng cài đặt) → nếu không có MS Office thì **LibreOffice/OpenOffice** (UNO API PrintOut) → fallback cuối mới convert PDF qua LibreOffice headless + in bằng PDFium (mất cài đặt gốc, cảnh báo user)
   - **PDF → PDFium** render trực tiếp; **ảnh → WIC**; **TXT/CSV → HTML/PDF**
2. **Print qua Windows Spooler API** — không dialog từng file, kiểm soát được setting theo doc.
3. **UI và Engine tách rời** — MCP server, CLI dùng chung Core.
4. **State machine rõ** — không bao giờ "treo im".
5. **No cloud, no telemetry** — dữ liệu in cục bộ 100%.

**Module:**
- `Printonator.Core` — queue, scheduler, engine registry (MIT)
- `Printonator.Formats.Pdf` — PDFium (Apache-2.0)
- `Printonator.Formats.Office` — **engine chọn app in gốc** (MS Office COM → LibreOffice UNO → fallback PDF) + section-map reader (parse document.xml `sectPr`)
- `Printonator.Spool` — Windows Spooler/Printer API + emulated PDF merge
- `Printonator.Mcp` — MCP server (stdlib/HTTP): list_printers, print_files, list_jobs, job_status, cancel_job, presets
- `Printonator.Cli` — headless (reuse core + mcp)
- `Printonator.UI` — WPF/WinUI, dark mode, list virtualized
- `Printonator.Tests` — unit + integration trên "Microsoft Print to PDF"

---

## 6. MCP + AI in giùm — thiết kế

**Mục đích:** bất kỳ AI client nào nói chuẩn MCP (Claude, Hermes, Copilot, assistant tự) kết nối và ra lệnh in thay người.

**Cách chạy MCP server:**
- Chế độ 1: in-process trong app (bật nút "MCP Server" trong UI, HTTP/SSE `http://localhost:3939/mcp`).
- Chế độ 2: standalone `printonator-mcp` chạy nền (stdio hoặc HTTP) — không cần mở UI.
- Đăng ký vào config MCP của client (vd `hermes config` thêm tool `mcp__printonator__...`).

**Tools (tool list mục tiêu):**
```
list_printers()            → danh sách máy in + thuộc tính (khổ, 2 mặt, color)
print_files(paths, printer, copies?, pages?, duples?, orientation?, color?, collide?)
                           → job_id(s) + ước lượng trang
list_jobs(status?)         → hàng đợi: queued / converting / spooling / done / error
job_status(job_id)         → detail: file, trạng thái, lỗi (code + tiếng người)
cancel_job(job_id)
get_presets() / save_preset(name, config) / print_with_preset(name, paths)
watch_folder(path, rule)   → bắt đầu theo dõi + auto print (v2)
```

**Đặc biệt quan trọng — an toàn khi AI được quyền in nháp:**
- **Printer allowlist** — AI chỉ in vào máy trong danh sách được duyệt (ví dụ máy ở phòng in, KHÔNG phải máy ảo hay máy chậm).
- **Approve mode (mặc định nên bật):** job từ AI vào hàng đợi "pending", nhấn OK trên UI (hoặc tool `approve_job`) mới vô máy — vì in = tiền giấy + mực thật.
- **Giới hạn trang:** config max-trang/batch (vd 200), max 10k trang/ngày — chặn AI lỗi lặp in vô hạn.
- **Audit log:** thời gian, tool, tham số, trạng thái — đủ cho việc hiếu (AI nói "in 100 bản" mà không cho máy? có log).
- Local-only: hỗ trợ khởi chạy localhost; khóa remote trừ khi cấu hình rõ.

---

## 7. Khác biệt vs Print Conductor

| | Print Conductor | Printonator |
|---|---|---|
| Giá | trả phí / license theo máy | MIT miễn phí, mã mở |
| Office | phụ thuộc MS Office bản quyền | LibreOffice — không cần bản quyền |
| Automation | CLI bản cao, đóng | CLI + CSV + watch-folder + **MCP AI-native** |
| Data | license server, activation | 100% local, no telemetry |

Điểm "AI-native" là thứ Print Conductor không có: AI đang thành người soạn tài liệu — mình cho chính nó cái "nút in".

---

## 8b. Notification & Auto-update an toàn

**Notification trong app (toast + trung tâm thông báo):**
- **Toast (góc phải dưới):** hiện 4-6 giây cho sự kiện ngắn — "Đã thêm 3 file", "In xong 9/12", "Máy in offline"
- **Trung tâm thông báo (bell icon góc trên):** lịch sử đầy đủ — lỗi, cảnh báo, bản cập nhật, sự kiện bảo mật. Mỗi thông báo có mức: info / success / warning / error / security
- **Thông báo bản mới:** khi có phiên bản mới → toast + badge dot trên bell "Versions mới" → click mở trang Auto-update

**Auto-update — thiết kế chống mã độc (critical với mã nguồn mở):**
Vì mã MIT ai cũng clone được, kẻ xấu có thể build bản chèn mã độc phát tán. Không mua code-signing cert (tốn tiền, không cần). Thay vào đó dùng cơ chế **không tốn phí nhưng xác thực được**:
1. **Chỉ cập nhật từ nguồn chính thức:** GitHub Releases của repo chính chủ, qua HTTPS — so sánh `version` + `build id`
2. **Xác thực bằng Minisign / Sigstore (miễn phí):** nhà phát hành ký manifest bằng khóa riêng (minisign — public key nhúng sẵn trong app source, ai build từ source chính chủ cũng có public key). Bản phát hành không ký đúng public key → từ chối
3. **Checksum SHA-256:** so sánh với giá trị công bố trong manifest; lệch → từ chối & cảnh báo
4. **Pin/bảo vệ:** auto-update MẶC ĐỊNH TẮT, bật thủ công; có nút "Kiểm tra cập nhật"
5. **Phát hiện bản clone:** mỗi bản phát hành có `signature.json` (minisign sig + checksum); nếu app chạy từ bản không có signature hợp lệ hoặc bị sửa → hiện cảnh báo đỏ không tắt được: "This build is NOT trusted — download the official version"
6. **Cảnh báo khi cài:** xác nhận trước khi áp bản mới, ghi log

```json
// update manifest (từ GitHub releases)
{ "version": "1.2.0", "url": "...", "sha256": "abc...", "minisign_sig": "MII...", "releaseNotes": "..." }
```

---

## 8. Lộ trình kỹ thuật

1. **Tuần 1:** Repo MIT, skeleton Core + UI file-list + MCP server lộ `list_printers` (chứng minh app → AI kết nối được)
2. **Tuần 2:** PDFium + Spool → in PDF thật, job log, retry; MCP `print_files` + `approve` flow
3. **Tuần 3:** LibreOffice bridge → đủ định dạng; per-job config (bản, duplex, range); MCP presets + quota/allowlist
4. **Tuần 4:** CSV import, gộp PDF, watch-folder, CLI hoàn thiện, test trên máy + máy ảo Win10/11 + kết nối thử từ Hermes/Claude
5. **Sau:** i18n, packaging, GitHub Actions CI (build + publish), docs cho MCP đăng ký client

## 9. Nguồn gốc & rủi ro

| Rủi ro | Mức | Giải pháp |
|---|---|---|
| Gọi app gốc in Office (COM/UNO) treo nếu app đang mở file khác | Cao | in tuần tự, timeout + retry; nếu app bận → cạnh tranh bằng instance mới (COM `/x` flag / UNO socket) hoặc fallback PDF kèm cảnh báo |
| Driver máy cũ nghịch job lạ | Cao | bọc lỗi spool, retry có delay, khuyến nghị test máy ảo trước |
| MCP AI in sai/linh tinh (tikỳ máy, trang) | Cao | approve mode + allowlist + quota + audit — đời hơn thiếu |
| PDF page-range khác nhau trong lô | Trung | unit test PDF đa trang, từng case lạ |
| SPPrinter khác biệt Win10/11 | Trung | CI 2 version Windows value, fallback |
| Cộng đồng mã mở ít người dùng | Thấp | MIT + docs Việt/Anh + MCP chính là feature hút dev AI |

---

*Tiếp theo: nếu a đồng ý hướng (C#/.NET + MCP kèm guard an toàn), mình chốt thiết kế kỹ (spool flow, schema job, list tool của MCP server, selection PDFium binding) rồi scaffold tuần 1.*