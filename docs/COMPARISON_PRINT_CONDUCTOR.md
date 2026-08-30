# Đối chiếu Printonator vs Print Conductor — Gap Analysis (cập nhật 2026-08-26)

> Nguồn tham khảo: trang hướng dẫn công khai của Print Conductor (https://www.print-conductor.com/how-to
> và các trang liên quan: document-printing-settings, page-printing-settings, common-settings,
> manage-printer-settings, print-multiple-pages-per-sheet, set-up-printer-properties, features) — đối chiếu tính năng, ngày 2026-08-26.
> Mục đích: tài liệu chuyển giao — AI khác đọc xong biết Printonator thiếu gì, làm gì tiếp theo theo thứ tự ưu tiên.
> Lưu ý: chỉ liệt kê & đối chiếu TÊN TÍNH NĂNG (thông tin công khai) — không sao chép nội dung/thiết kế/văn bản của họ.
> **Cập nhật lớn 2026-08-26**: bảng option in bổ sung đầy đủ (page range, color mode, paper source,
> scale mode, pages-per-sheet, profile, native printer dialogs, odd/even, resolution, page-size-based)
> + engine render DYNAMIC không bundle + mục "UI/UX gaps đã xử lý".

## Tóm tắt mức độ

| | Print Conductor (thương mại) | Printonator (MIT) | Ghi chú |
|---|---|---|---|
| Giá | Trả phí theo máy | Miễn phí, mã mở | Lợi thế cốt lõi |
| Office engine | Dùng MS Office bản quyền | App gốc (COM) + **LibreOffice dynamic** (soffice có sẵn trên máy) | ✅ Cả 2 engine đã code; nếu máy không có gì → shell fallback |
| PDF/ảnh/TXT render | PDFium nhúng sẵn | **Browser render dynamic** (Chrome/Edge headless CDP) + **Windows.Data.Pdf** (API có sẵn Win10/11) | ✅ KHÔNG bundle lib — máy ai cũng có |
| Automation | CLI bản cao, đóng | **MCP AI-native** ✅ + watch-folder + shell verb in file | MCP server 13 tools đã có (xem docs/MCP.md); CLI wrapper đóng gói chưa có |
| Data/privacy | License server, activation | 100% local, no telemetry | — |
| Tùy chọn in cơ bản (page range/màu/khay/scale/N-up/odd-even/res) | Đầy đủ, mức app + driver | ✅ Đầy đủ trên màn Print Settings (bảng 3) | Đợt 2026-08-26 |

## So sánh tính năng chi tiết (a. Có / b. Thiếu — cần làm)

### 1. Thêm & sắp xếp file trong danh sách
| Tính năng PC | Printonator | Ghi chú |
|---|---|---|
| Drag & drop thêm file | ✅ Có | AddFiles + PasteFromClipboard |
| Open-file dialog đa chọn | ✅ Có | — |
| Thêm từ folder + subfolder (chọn loại file) | ✅ Có | Paste folder / Ctrl+V quét đệ quy |
| Thêm từ ZIP/RAR/7ZIP | ❌ Thiếu | v2 |
| Import danh sách từ TXT/Excel/URL | ❌ Thiếu | v2 |
| FLIST (lưu/đọc danh sách + settings) | ❌ Thiếu | roadmap (docs/ARCHITECTURE.md) |
| Sort danh sách theo cột | ✅ Có | Name/Pages/Settings/Status |
| Sắp xếp thứ tự in (kéo lên/xuống) | ❌ Thiếu | Cần nút ↑↓ hoặc drag reorder |
| In chỉ các mục chọn lọc | ✅ Có | Print selected |
| Chỉnh cấu hình TỪNG file (Item settings) | ✅ **Có (mới)** | Context menu → "Cấu hình in (Item settings)…" mở Print Settings |
| Double-click mở file gốc | ✅ Có | OpenFileCommand + reload watcher + badge "↻ Reloaded" |
| Context menu Windows Explorer ("Print with Printonator") | ✅ **Có** | Shell verb qua SingleInstance (T2.8) |
| History pane (danh sách lần chạy gần nhất) | ✅ **Có** | Lịch sử in lưu JSON (`HistoryStore`, max 1000 entry) — xem `docs/ARCHITECTURE.md` |

### 2. Máy in
| Tính năng PC | Printonator | Ghi chú |
|---|---|---|
| Local / Network / Virtual printer | ✅ | PrinterService qua LocalPrintServer; badge "ảo" |
| Trạng thái từng máy (available/offline/error + khổ giấy + ability) | ✅ | GetPrintQueues + GetPrintCapabilities |
| **Printer Properties / Printing Preferences (dialog NATIVE driver)** | ✅ **Có (mới)** | Nút trên từng máy (màn Printers) + trong Print Settings; chạy `printui.dll /p` và `/e` — đúng như PC |
| Print trên nhiều máy khác nhau (per-file printer) | ✅ **Có** | Combo máy in riêng TỪNG dòng (HasPerFilePrinter); mặc định "Theo máy thanh công cụ" |
| Printer load balancing | ❌ Thiếu | v2 |
| Lưu/thu hồi Printer Properties khi thoát app | ❌ Thiếu | v2 (PC có "keep/discard changes on exit") |

### 3. Cấu hình in (per-file + batch) — BẢNG CẬP NHẬT ĐẦY ĐỦ
| Tùy chọn (PC) | Printonator | Ghi chú |
|---|---|---|
| **Page range: chọn All HOẶC Pages (input)** | ✅ **Có (mới)** | Print Settings có radio "Tất cả (All)" / "Chọn trang:" + preview live trang vật lý; syntax All/1,3/2-5/1-2,7/S2:1-3 |
| Page range syntax `last/last1/last2` (từ cuối tài liệu) | ✅ **Có** | `ResolvePhysicalPages` hỗ trợ macro `last`/`lastN` (trang cuối / N trang cuối) — cần biết số trang file |
| **Chỉ in trang lẻ / trang chẵn (Print odd or even)** | ✅ **Có (mới)** | Combo All/Odd/Even — engine render lọc đúng trang; shell để nguyên |
| Bỏ trang lặp (skip repeated pages khi range trùng) | ✅ Có | ResolvePhysicalPages luôn Distinct+sorted |
| Số bản copies | ✅ | PrintConfig.Copies + Print Settings |
| **Collation: As in printer / By documents / By pages** | ✅ **Có (mới)** | Field Collation + combo trong Print Settings (engine COM truyền Collate) |
| Print List N lần | ❌ Thiếu | v2 |
| Max copies per job (chia nhỏ job nhiều bản) | ⚠️ Một phần | PrintGuard giới hạn copies/file (MCP); UI chưa có |
| **Duplex: Simplex / Long-edge / Short-edge / As in printer** | ✅ **Có (mới)** | Combo 4 mức; Word engine dùng ManualDuplexPrint |
| **Paper source (khay giấy của máy in)** | ✅ **Có (mới)** | Print Settings combo "Theo máy in" + danh sách khay từ InputBinCapability (tên thân thiện VN) |
| Paper source cho từng page range | ❌ Thiếu | v2 |
| **Color mode: As in printer / As in document / Color / Grayscale** | ✅ **Có (mới)** | Enum PrintColorMode + combo 4 mức; Excel engine sẵn PageSetup.BlackAndWhite |
| **Page orientation: As in document / As in printer / Portrait / Landscape** | ✅ **Có (mới)** | Enum mở rộng + engine bỏ ép chiều khi "theo file/máy" |
| Auto rotate trang | ❌ Thiếu | v2 |
| **Scale mode: Shrink / Fit / Original / Fill / Zoom %** | ✅ **Có (mới)** | Enum PrintScaleMode + combo 6 mức + ô Zoom% |
| **Pages per sheet (N-up): 2/4/6/9/16 + Booklet** | ✅ **Có (mới)** | Combo + field PagesPerSheet/Booklet; áp qua BrowserPrintEngine render (CDP printToPDF) |
| **Page size based (in đúng khổ từng trang file)** | ✅ **Có (mới)** | Paper "Theo tài liệu (khổ gốc)" = sentinel `AsDocument` → browser dùng `preferCSSPageSize=true` |
| **Printer resolution (High/Medium/Low/Draft)** | ✅ **Có (mới)** | Combo 5 mức → DPI rasterize 200/150/100/75 (WindowsPdfRasterizer); AsPrinter = driver quyết |
| Print as image + rasterization DPI | ⚠️ Một phần | Ảnh/PDF render qua browser/Windows PDF; TXT giữ text |
| Crop marks / Vectorize text / Alignment+offset | ❌ Thiếu | v2 |
| In password-protected PDF/DOCX | ❌ Thiếu | v2 (Windows.Data.Pdf báo lỗi mật khẩu → fallback shell) |
| Reverse order / Blank pages skip / Print job name / in mỗi N trang 1 job | ❌ Thiếu | v2 |
| **Printer profile template** (lưu/đọc profile in) | ✅ **Có (mới)** | Profile combo trong Print Settings = PresetStore JSON; MCP get/save/print_with_preset đã có; **PresetExporter** xuất/nhập file `.printonator` từ UI (Print Settings) |

### 4. Cover & report pages
| Tính năng PC | Printonator | Ghi chú |
|---|---|---|
| Cover page / Cover Designer / report page / estimation report | ✅ **Có (mới)** | `CoverPageRenderer` (HTML → browser → PDF → in) trong BatchOrchestrator; report page đầy đủ chưa có |

### 5. Single print job mode
| Tính năng PC | Printonator | Ghi chú |
|---|---|---|
| Gộp tất cả file → 1 PDF → 1 lần đẩy spooler | ✅ **Có (mới)** | `MergePrintEngine`: rasterize từng trang PDF/ảnh/TXT → HTML → browser printToPDF → PDF tạm → in 1 lần |

### 6. Watermark / 7. Pre-print & post-processing
| Tính năng PC | Printonator | Ghi chú |
|---|---|---|
| Watermark (text/image/barcode, macro {page}...) | ✅ **Có (mới)** | `WatermarkPrintEngine` (engine bọc, decorator): chèn chữ dấu mờ trên PDF/ảnh, có opacity; chưa có barcode/macro {page} |
| Pre-print ops (insert/rotate/resize/crop/grayscale), post-processing (move/copy/delete) | ❌ Thiếu | v2 |

### 8. Settings / Profile
| Tính năng PC | Printonator | Ghi chú |
|---|---|---|
| Export/Import settings (.ini profile) | ✅ **Có (mới)** | Preset JSON (PresetStore) + MCP + UI; **PresetExporter** xuất/nhập file `.printonator` (enum ghi theo tên, đọc số legacy) |
| Chạy với pre-saved settings từ command line | ⚠️ Một phần | Shell verb in file đã có (qua SingleInstance); CLI wrapper đóng gói chưa có |
| File extension aliases | ❌ Thiếu | v2 |
| Restore list + printer từ lần chạy trước | ✅ **Có (mới)** | `QueueStore` khôi phục hàng đợi chưa in từ lần chạy trước (JSON) |

### 9. Khác
| Tính năng PC | Printonator | Ghi chú |
|---|---|---|
| Office engine (Word/Excel/PPT qua app gốc) | ✅ Có | `OfficeComPrintEngine` — giữ page setup/section; fallback shell |
| **LibreOffice khi không MS Office** | ✅ **Có (mới)** | `LibreOfficeLocator` dò `soffice` TRÊN MÁY user (registry/path/env) → `LibreOfficePrintEngine` (`soffice --headless --pt`) — KHÔNG bundle lib, app nhẹ |
| **Browser render cho PDF/ảnh/TXT** | ✅ **Có (mới)** | Chrome/Edge headless (máy ai cũng có) → CDP printToPDF áp THẬT page range (non-PDF), scale, khổ giấy, landscape; FAST-PATH in thẳng khi cấu hình như default. **PDF page SLICING** dùng Windows.Data.Pdf (API có sẵn Win10/11): render trang chọn → PNG → dựng HTML → printToPDF đúng khổ gốc |
| Print multiple pages per sheet | ✅ Có | Combo N-up (áp qua engine render) |
| Batch print email (.msg/.eml) | ❌ Thiếu | v2 |
| CAD (DWG/DXF) | ❌ Thiếu | v2 (DefaultPaperFor đã biết A3 cho bản vẽ) |
| HEIC photos | ⚠️ | WIC nếu codec cài |
| Validate digital signatures (PDF) | ❌ Thiếu | v2 |
| Log files | ⚠️ Một phần | Audit log MCP (JSON) có; `HistoryStore` lưu lịch sử in; UI chưa có log phiên file đầy đủ |
| Scheduled printing | ❌ Thiếu | ngoài MVP |
| Watch folder (in tự động file mới trong thư mục) | ✅ **Có (mới)** | `WatchFolderService` (FileSystemWatcher debounce 2s) + `WatchFolderWindow` quản lý, auto-print tùy chọn |
| Silent deploy | ✅ | Inno /VERYSILENT |
| Customize interface / preview pane | ❌ | roadmap theme |
| Time delay between print jobs | ❌ Thiếu | v2 |
| Print job name / "Start print after every N pages" | ❌ Thiếu | v2 |

## Khoảng trống quan trọng nhất (giá trị/effort) — cập nhật 2026-08-30

### ✅ Đã xong (các đợt 2026-08-26..30)
1. **Màn duyệt `AwaitingApproval` in-process trong UI** — nút Approve all / Reject all trên MainWindow (ApproveAll_Click).
2. **Cover page** — `CoverPageRenderer` in trước lô (BatchOrchestrator).
3. **Single print job mode (gộp PDF)** — `MergePrintEngine`.
4. **Per-file printer** — combo máy in riêng từng dòng.
5. **Page range macro `last`** — `ResolvePhysicalPages` hỗ trợ `last`/`lastN`.
6. **Shell context menu** "In với Printonator" — shell verb qua SingleInstance.
7. **Watermark** — `WatermarkPrintEngine` (decorator).
8. **Watch folder** — `WatchFolderService` + `WatchFolderWindow`.
9. **History pane** — `HistoryStore` (lịch sử in JSON, max 1000).
10. **Preset export/import file** — `PresetExporter` (`.printonator`).
11. **Restore hàng đợi** — `QueueStore`.

### Còn lại
- **Report page đầy đủ** (mới chỉ có cover) — value cao văn phòng VN.
- **CLI chạy preset** (CLI wrapper đóng gói).
- **Post-processing** (insert/rotate/resize/crop/grayscale, move/copy/delete).
- **Email (.msg/.eml)** · **CAD (DWG/DXF)** · **HEIC** · **Validate digital signatures (PDF)**.
- **UI theme** · **Reverse order/blank skip/job name** · **Import TXT/Excel/URL** · **Archives ZIP/RAR/7Z**.
- **Time delay between print jobs** · **File extension aliases**.

## UI/UX gaps — đã xử lý đợt 2026-08-26 (theo yêu cầu user)

Trước: option in cũ gói trong 1 BulkBar hàng ngang chật (copies/duplex/chiều/paper) + dialog `PaperSetupDialog`
5 trường — thiếu page range có chọn All/Ranges, màu, khay, scale, N-up, profile; không có dialog native driver.

Đã làm:
1. **Bảng `PrintSettingsWindow` mới (2 cột, tận dụng không gian):**
   - Page range: radio **Tất cả (All)** / **Chọn trang:** + preview live trang vật lý + hiển thị section DOCX.
   - **Chỉ in trang lẻ/chẵn** (All/Odd/Even) — tiết kiệm mực, in 2 mặt trước.
   - **Color mode** 4 mức: As in printer / As in document / Color / Grayscale.
   - **Paper source**: "Theo máy in" + danh sách khay từ máy in (tên tiếng Việt).
   - **Duplex** 4 mức: As in printer / 1 mặt / 2 mặt lật cạnh dài / ngắn.
   - **Collation** 3 mức.
   - **Khổ giấy**: "Theo máy in" / **"Theo tài liệu (khổ gốc từng trang)"** / danh sách khổ máy (kèm mm).
   - **Orientation** 4 mức (thêm As in document / As in printer).
   - **Scale mode** 6 mức: As in document / Shrink / Fit / Original / Fill / Zoom% (ô %).
   - **Pages per sheet** 7 mức: 1 / 2 / 4 / 6 / 9 / 16 / Booklet.
   - **Printer resolution** 5 mức: As in printer / High / Medium / Low / Draft (→ DPI rasterize 200/150/100/75).
   - **Profile (printer template)**: combo nạp preset + Lưu/Xóa profile — lưu toàn bộ option.
   - **Nút native driver**: Printing Preferences… và Printer Properties… (printui.dll /e và /p) cho máy đang chọn.
   - **Chỉ dẫn dưới MỖI option** (hint text 1 dòng) + nhóm "Trang & bản in" / "Giấy & chất lượng" — dễ dùng hơn.
2. **BulkBar tinh gọn**: chỉ còn count + summary cấu hình file đầu + 1 nút "Cấu hình in…" mở bảng mới.
3. **Context menu file**: thêm "Cấu hình in (Item settings)…" — chỉnh từng file/nhóm (đúng mô hình Item settings của PC).
4. **Màn Printers**: mỗi máy có nút Printing Preferences / Printer Properties (dialog gốc hãng máy).
5. **Model** mở rộng tương ứng (màu/khay/scale/N-up/collation/parity/quality/profile/khổ gốc) — MCP `print_files`
   nhận thêm colorMode/paperSource/scaleMode/pagesPerSheet/parity/quality; Preset lưu đủ trường (profile template).

## Điểm Printonator vượt Print Conductor (giữ và quảng bá)
- **MCP AI-native** ✅ — PC không có: 13 tools (list_printers, pick_printer, print_files, print_with_preset, presets, approve/reject, list_jobs, job_status, cancel_job, get_guard_config, get_error_reference) + PrintGuard (allowlist, quota, approve mặc định, audit) — fail-closed.
- **Không phụ thuộc bản quyền + app NHẸ**: engine dynamic theo máy — MS Office nếu có, LibreOffice nếu có,
  Chrome/Edge (máy ai cũng có) render đúng page range/scale/khổ giấy cho PDF/ảnh/TXT, ngược lại shell/handler mặc định.
  Không bundle thư viện nặng (PDFium/LibreOffice) vào installer.
- **MIT, no telemetry** — 100% local.
- **Section-aware page range (S2:1-3)** — PC không nêu.

## Roadmap đề xuất (cập nhật 2026-08-30)
```
Đã xong: Màn MCP/Safety UI + approve in-process → Cover page → Single-job merge → Per-file printer → macro "last"
          → Shell context menu → Watermark → Watch folder → History → Preset export/import (.printonator)
Tiếp theo: Report page đầy đủ → CLI wrapper → Post-processing → Email/CAD/HEIC/signature → Theme
          → Reverse/blank/job-name → Import TXT/Excel/URL → Archives → Time delay → Aliases
```