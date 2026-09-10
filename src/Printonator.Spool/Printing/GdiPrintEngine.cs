using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.IO;
using Printonator.Core;
using Printonator.Core.Models;
using Printonator.Core.Printing;

namespace Printonator.Spool.Printing;

/// <summary>
/// ENGINE in PDF bằng ẢNH GDI — KHÔNG cần shell printto / app đọc PDF trên máy.
/// PDF → Windows.Data.Pdf rasterize từng trang cần in (đúng page-range/parity/DPI theo chất lượng)
/// → in ảnh thẳng vào SPOOLER máy đã chọn qua System.Drawing.Printing (GDI).
/// Giải quyết lỗi "không in được gì, cứ báo lỗi PDF" trên máy KHÔNG có print handler cho .pdf
/// (UserChoice = ProgID không có shell\printto → Process.Start(verb=printto) ném 1155 → SPOOLER_FAILED).
/// Máy in ẢO (PDF/XPS...) không in GDI (spooler PDF in ảnh → không ra file đúng) → đẩy BROWSER render
/// (BrowserPrintEngine) xử lý xuất PDF cạnh file gốc như cũ.
/// KHÔNG bundle lib — Windows.Data.Pdf có sẵn Windows 10/11 + System.Drawing.Common đi kèm.
/// </summary>
public sealed class GdiPrintEngine : IPrintEngine
{
    private static readonly string[] SupportedFormats = ["PDF"];

    /// <summary>Deadline cho PrintDocument.Print() (blocking, không nhận token) — driver máy in
    /// treo (spooler treo) không được kẹt job "Converting mãi". Hết giờ → trả EngineTimeout, drain thoát.
    /// Driver máy in vật lý chậm với file lớn (3-5s/trang cho ảnh 300dpi) — đặt 600s đủ cho ~100 trang.</summary>
    internal const int GdiPrintTimeoutSeconds = 600;

    private readonly IPrintEngine _browserInner;
    private readonly WatermarkPrintEngine _watermarkEngine;

    public GdiPrintEngine(IPrintEngine? browserInner = null)
    {
        // Inner cho máy ảo: BrowserPrintEngine (không watermark) xuất PDF cạnh file gốc.
        // _watermarkEngine: giữ hành vi dấu mờ PDF hiện có (overlay → printToPDF → in PDF tạm).
        _browserInner = browserInner ?? new BrowserPrintEngine();
        _watermarkEngine = new WatermarkPrintEngine(_browserInner);
    }

    public bool CanHandle(string format)
        => SupportedFormats.Contains((format ?? "").ToUpperInvariant());

    public async Task<Result<bool>> PrintAsync(PrintJob job, CancellationToken ct)
    {
        if (job is null)
            return Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.FileNotFound,
                Category = PrintErrorCategory.System,
                Message = "Job rỗng — không in được.",
                Hint = "Kiểm tra lại file cần in.",
            });

        if (job.Config is null)
            return Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.FileNotFound,
                Category = PrintErrorCategory.System,
                Message = "Job thiếu cấu hình in — không in được.",
                Hint = "Kiểm tra lại file cần in.",
            });

        // Có dấu mờ → giữ nguyên đường watermark hiện có (không regress hành vi Phase 2).
        if (!string.IsNullOrWhiteSpace(job.Config.WatermarkText))
            return await _watermarkEngine.PrintAsync(job, ct);

        var printer = job.Config.PrinterName;
        if (DefaultPrinter.IsDefault(printer))
            printer = DefaultPrinter.GetWindowsDefaultPrinterName(); // resolve máy default Windows NGAY (báo lỗi rõ nếu rỗng)

        if (string.IsNullOrWhiteSpace(printer))
        {
            GdiLog($"GdiPrintEngine: sentinel='{job.Config.PrinterName}' resolve mặc định = NULL → PRINTER_NOT_FOUND");
            return Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.PrinterNotFound,
                Category = PrintErrorCategory.Config,
                Message = "Không tìm thấy máy in mặc định.",
                Hint = "Chọn máy in cụ thể ở thanh công cụ.",
            });
        }
        GdiLog($"GdiPrintEngine: job='{job.FileName}' sentinel='{job.Config.PrinterName}' resolve='{printer}' virtual={PrinterService.IsVirtualPrinter(printer)}");

        // Ghi máy in đã resolve vào Config — các fallback (browserInner/shell) về sau không còn thấy
        // sentinel "mặc định" (log cũ: printer='' ở rớt fallback).
        job.Config.PrinterName = printer;

        if (!File.Exists(job.FilePath))
            return Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.FileNotFound,
                Category = PrintErrorCategory.System,
                Message = $"File không tồn tại: {job.FilePath}",
                Hint = "File bị xóa hoặc di chuyển — kiểm tra lại đường dẫn.",
            });

        // Máy in ẢO (PDF/XPS/OneNote...) → không in GDI (spooler PDF-in-ảnh không ra file đúng).
        if (PrinterService.IsVirtualPrinter(printer))
        {
            // PDF source → RENDER TRỰC TIẾP KHÔNG BROWSER: Windows.Data.Pdf rasterize từng trang →
            // PdfImageWriter ghi file PDF MỚI (tài liệu mới — chữ ký số/metadata gốc bị BỎ, đúng bản
            // chất "in ra"). Không spawn Chrome/Edge → không có UI browser, không có process dư.
            // KHÔNG copy thẳng file gốc: copy giữ nguyên chữ ký số (lỗ hổng bảo mật — đã chốt 2026-09-10).
            if (job.Format.Equals("PDF", StringComparison.OrdinalIgnoreCase))
                return await ExportPdfDirectAsync(job, printer, ct);

            // Ảnh/TXT → browser render (cần chuyển sang PDF — không có cách native; BrowserPrintEngine
            // xuất PDF cạnh file gốc cho máy ảo).
            GdiLog($"GdiPrintEngine: máy ảo '{printer}' ({job.Format}) → browser render (xuất PDF cạnh file)");
            return await _browserInner.PrintAsync(job, ct);
        }

        // Đếm số trang PDF TRƯỚC — ResolveSelectedPages cần PageCount để lọc range/parity đúng.
        // Dùng PdfPageCountReliableAsync: Windows.Data.Pdf đếm THIẾU trang với PDF linearized/tạo
        // từ Word/Excel (bug v0.2.4: file 3 trang in ra 1 trang) → fallback parser "/Type /Page".
        if (job.PageCount <= 0)
        {
            var n = await WindowsPdfRasterizer.PdfPageCountReliableAsync(job.FilePath, ct);
            GdiLog($"GdiPrintEngine: PdfPageCount='{job.FileName}' → {n}");
            if (n > 0) job.PageCount = n;
        }
        GdiLog($"GdiPrintEngine: PageCount={job.PageCount} range='{job.Config.PageRange}' parity={job.Config.Parity}");

        // CdpPrintParams.ResolveSelectedPages trả null khi "All + không lọc lẻ/chẵn" — KHÔNG phải lỗi mà
        // là "in TẤT CẢ trang". GDI engine cần chuyển thành danh sách đầy đủ (không có pageRanges CDP).
        int[]? pages;
        try { pages = CdpPrintParams.ResolveSelectedPages(job); }
        catch { pages = null; }

        // Tham số: là All (không range cụ thể) mà không Lẻ/Chẵn → in hết trang (không lọc)
        var isAll = string.IsNullOrWhiteSpace(job.Config.PageRange)
                    || job.Config.PageRange.Equals("All", StringComparison.OrdinalIgnoreCase);
        if (pages is null && isAll && job.Config.Parity == PageParityFilter.All && job.PageCount > 0)
        {
            pages = Enumerable.Range(1, job.PageCount).ToArray();
        }

        if (pages is not { Length: > 0 })
        {
            GdiLog($"GdiPrintEngine: Không có trang nào để in (pages null/empty) → rớt browserInner");
            var fallback = await _browserInner.PrintAsync(job, ct);
            GdiLog($"GdiPrintEngine: browserInner trả {fallback.IsSuccess} code={fallback.Error?.Code} msg='{fallback.Error?.Message}'");
            return fallback;
        }
        GdiLog($"GdiPrintEngine: ResolveSelectedPages OK → {pages.Length} trang");

        // DPI render cho GDI: 300dpi mặc định (AsPrinter/Medium) — PDF text/vector cần 300 để in nét.
        // CdpPrintParams.DpiFor chỉ trả 150 cho AsPrinter (tối ưu browser) → GDI cần DPI cao hơn.
        var renderDpi = DpiForGdi(job.Config.Quality);
        GdiLog($"GdiPrintEngine: renderDpi={renderDpi}");
        var rendered = await WindowsPdfRasterizer.RenderPagesAsync(job.FilePath, pages, ct, renderDpi);
        if (!rendered.IsSuccess || rendered.Value is not { Count: > 0 } imgs)
        {
            GdiLog($"GdiPrintEngine: RenderPagesAsync thất bại → rớt browserInner, err={rendered.IsSuccess}");
            var fallback = await _browserInner.PrintAsync(job, ct);
            GdiLog($"GdiPrintEngine: browserInner trả {fallback.IsSuccess} code={fallback.Error?.Code}");
            return fallback;
        }
        GdiLog($"GdiPrintEngine: RenderPages OK → {imgs.Count} ảnh");

        // In ảnh từng trang N bản qua GDI thẳng vào spooler máy đã chọn. Lỗi lúc in ảnh → trả lỗi RÕ.
        //
        // TIMEOUT CỨNG (race fix): PrintDocument.Print() là blocking sync KHÔNG nhận CancellationToken —
        // nếu driver máy in treo (spooler treo), pd.Print() treo vô thời hạn → job "Converting mãi",
        // Cancel/Pause không thoát được. Wrap WaitAsync(timeout): hết thời gian → drain thoát (job Error
        // EngineTimeout) dù thread in ảnh vẫn nằm đó (không giết được, nhưng queue KHÔNG kẹt).
        var printTask = Task.Run(() => PrintImagesToPrinter(job, printer, imgs, ct), ct);
        Result<bool> r;
        try
        {
            r = await printTask.WaitAsync(TimeSpan.FromSeconds(GdiPrintTimeoutSeconds), ct);
        }
        catch (TimeoutException)
        {
            GdiLog($"GdiPrintEngine: IN QUÁ LÂU '{job.FileName}' → '{printer}' (>{GdiPrintTimeoutSeconds}s) → EngineTimeout");
            return Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.EngineTimeout,
                Category = PrintErrorCategory.System,
                Message = $"In {job.FileName} tới \"{printer}\" quá lâu ({GdiPrintTimeoutSeconds}s) — đã hủy.",
                Hint = "Máy in có thể bị treo/kẹt giấy. Kiểm tra hàng đợi máy in rồi in lại.",
            });
        }
        catch (OperationCanceledException) { throw; }   // cancel (per-job token) → OCE lan tới DrainLoopAsync
        if (!r.IsSuccess)
        {
            GdiLog($"GdiPrintEngine: IN LỖI '{job.FileName}' → '{printer}': code={r.Error!.Code} msg='{r.Error.Message}' hint='{r.Error.Hint}'");
            return r;
        }

        GdiLog($"GdiPrintEngine: IN XONG '{job.FileName}' → '{printer}' ({imgs.Count} trang)");
        if (job.PageCount <= 0) job.PageCount = pages.Length;
        return Result<bool>.Ok(true);
    }

    /// <summary>
    /// PDF → máy ảo: xuất PDF MỚI trực tiếp (KHÔNG browser). Windows.Data.Pdf rasterize đúng
    /// page-range/parity → PdfImageWriter ghi tài liệu PDF MỚI — chữ ký số/metadata gốc bị BỎ
    /// (an toàn bảo mật; không kế thừa chứng thư file gốc).
    /// </summary>
    private static async Task<Result<bool>> ExportPdfDirectAsync(PrintJob job, string printer, CancellationToken ct)
    {
        // Probe PageCount trước — ResolveSelectedPages cần nó để lọc range/parity.
        if (job.PageCount <= 0)
        {
            var n = await WindowsPdfRasterizer.PdfPageCountReliableAsync(job.FilePath, ct);
            GdiLog($"GdiPrintEngine: PdfPageCount='{job.FileName}' → {n}");
            if (n > 0) job.PageCount = n;
        }

        var outPdf = PrinterService.PdfOutputPath(job);
        if (outPdf is not null && outPdf.Equals(job.FilePath, StringComparison.OrdinalIgnoreCase))
            return Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.SpoolerFailed,
                Category = PrintErrorCategory.Printer,
                Message = $"Không xuất được PDF cho \"{job.FileName}\" (trùng file gốc).",
                Hint = "Đổi tên file PDF nguồn hoặc chọn máy in giấy.",
            });
        outPdf ??= Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(job.FilePath) + "_printonator.pdf");

        // Trang cần in (range/parity) — null + All + không lẻ/chẵn → in hết.
        int[]? pages;
        try { pages = CdpPrintParams.ResolveSelectedPages(job); }
        catch { pages = null; }
        var isAll = string.IsNullOrWhiteSpace(job.Config.PageRange)
                    || job.Config.PageRange.Equals("All", StringComparison.OrdinalIgnoreCase);
        if (pages is null && isAll && job.Config.Parity == PageParityFilter.All && job.PageCount > 0)
            pages = Enumerable.Range(1, job.PageCount).ToArray();

        if (pages is not { Length: > 0 })
            return Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.FileCorrupted,
                Category = PrintErrorCategory.App,
                Message = $"Không xác định được trang nào để in từ \"{job.FileName}\".",
                Hint = "Kiểm tra file PDF còn đọc được (không khóa/mật khẩu).",
            });

        // Render 300 DPI (sắc nét cho văn bản — cao hơn DpiFor browser ~150).
        var imgs = await WindowsPdfRasterizer.RenderPagesAsync(job.FilePath, pages, ct, 300);
        if (!imgs.IsSuccess || imgs.Value is not { Count: > 0 } list)
            return Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.FileCorrupted,
                Category = PrintErrorCategory.App,
                Message = $"Không đọc được nội dung \"{job.FileName}\" để xuất PDF.",
                Hint = "File PDF có thể bị hỏng hoặc bị mật khẩu. Thử mở trong Edge/Adobe xem được không.",
            });

        try
        {
            PdfImageWriter.Write(outPdf, list);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.SpoolerFailed,
                Category = PrintErrorCategory.Printer,
                Message = $"Không lưu được PDF ra \"{outPdf}\".",
                Hint = "File PDF đang được mở ở chương trình khác — đóng lại rồi in lại.",
                Detail = ex.Message,
            });
        }

        GdiLog($"GdiPrintEngine: PDF '{job.FileName}' → máy ảo '{printer}' → PDF MỚI trực tiếp (không browser): {outPdf} ({list.Count} trang)");
        if (job.PageCount <= 0) job.PageCount = list.Count;
        return Result<bool>.Ok(true);
    }

    /// <summary>
    /// Đẩy từng ảnh trang đã render vào spooler máy in vật lý qua PrintDocument (GDI).
    /// Khổ giấy = khổ GỐC của trang PDF (in đúng cỡ file, không bị co/dãn theo khổ default máy),
    /// chiều ngang/dọc theo trang gốc. Ảnh đã render đúng DPI theo chất lượng → GDI chỉ đặt khung giấy
    /// + vẽ ảnh khít lề (fit — không méo, có lề trắng nếu driver hard-margin lệch tỷ lệ).
    /// PrintDocument dùng DeviceName = tên máy in nội bộ Windows — không qua shell, không cần print handler.
    /// </summary>
    internal static Result<bool> PrintImagesToPrinter(
        PrintJob job, string printer, IReadOnlyList<RenderedPdfPage> images, CancellationToken ct)
    {
        try
        {
            using var pd = new PrintDocument();
            pd.PrinterSettings.PrinterName = printer;

            if (!pd.PrinterSettings.IsValid)
                return Result<bool>.Fail(new PrintError
                {
                    Code = ErrorCodes.PrinterNotFound,
                    Category = PrintErrorCategory.Printer,
                    Message = $"Windows không nhận máy in \"{printer}\" khi in ảnh.",
                    Hint = "Kiểm tra máy in đã chọn còn tồn tại (Printer settings → Scan printers).",
                });

            // AUTO-ROTATE pattern (tham khảo SumatraPDF Print.cpp:628-639 advanced.autoRotate):
            // Paper size LUÔN = khổ dọc (theo user config). Trang PDF landscape → xoay ảnh 90°
            // trước khi vẽ → text nằm ngang đúng chiều đọc khi xoay giấy (giống Excel landscape ra
            // A4 dọc — chuẩn in thực tế).
            //
            // Áp dụng khi user chọn AsDocument (mặc định) / AsPrinter / Portrait explicit — driver
            // chỉ cần xử lý paper size dọc quen thuộc, rotate ảnh lo phần orientation.
            // KHÔNG áp dụng khi Landscape explicit (giữ orientation user chọn).
            //
            // Trước đây code set PaperSize theo orientation ảnh đầu → driver máy in vật lý bỏ qua
            // paper size ngang (set trong DefaultPageSettings) → in dọc + shrink. Fix: paper size cố
            // định theo user config + rotate ảnh nếu cần.
            var nUp = Math.Max(job.Config.PagesPerSheet, 1);
            var userOrientation = job.Config.Orientation;
            var configPaper = job.Config.PaperSize;
            var asDoc = string.IsNullOrWhiteSpace(configPaper) || configPaper.Equals(PaperCatalog.AsDocument, StringComparison.OrdinalIgnoreCase);

            // Xác định paper size (mm) từ config. AsDocument → A4 dọc. User chọn khổ → đúng khổ đó.
            int mmW, mmH;
            PaperKind sourceKind;
            if (!asDoc)
            {
                var dims = PaperCatalog.Dimensions(configPaper);
                if (dims is { } d)
                {
                    mmW = d.W; mmH = d.H;
                    sourceKind = d.W == 210 && d.H == 297 ? PaperKind.A4
                               : d.W == 297 && d.H == 420 ? PaperKind.A3
                               : d.W == 148 && d.H == 210 ? PaperKind.A5
                               : d.W == 216 && d.H == 279 ? PaperKind.Letter
                               : d.W == 216 && d.H == 356 ? PaperKind.Legal
                               : PaperKind.Custom;
                }
                else
                {
                    mmW = 210; mmH = 297; sourceKind = PaperKind.A4;
                }
            }
            else
            {
                mmW = 210; mmH = 297; sourceKind = PaperKind.A4;
            }

            // Landscape explicit: ép cả batch ra TỜ NGANG bằng cờ orientation (DEVMODE
            // dmOrientation=DMORIENT_LANDSCAPE) — paper vẫn là khổ chuẩn (A4 dọc), GDI/driver tự xoay,
            // PrintableArea trong PrintPage event sẽ là chiều NGANG.
            // KHÔNG dùng PaperSize ngang (swap mmW/mmH + PaperKind.Custom): driver máy in vật lý
            // CLAMP custom paper về khổ dọc → in y hệt Portrait (đã test trên máy thật 2026-09-10).
            var isLandscapeExplicit = userOrientation == PrintOrientation.Landscape;

            PaperSize? sheet;
            if (nUp > 1)
            {
                // N-up: paper size theo user config — auto-rotate không áp dụng (N-up hiếm với PDF).
                sheet = asDoc ? FindSupportedPaper(pd.PrinterSettings, PaperKind.A4, 827, 1169)
                              : PaperSizeFromName(pd.PrinterSettings, configPaper);
                sheet ??= FindSupportedPaper(pd.PrinterSettings, PaperKind.A4, 827, 1169);
                // N-up + Landscape: cờ orientation ở DefaultPageSettings lo phần xoay tờ,
                // PrintableArea sẽ là chiều ngang → vẽ grid vào area đó là đủ (không swap paper).
                if (sheet is null)
                    return Result<bool>.Fail(new PrintError
                    {
                        Code = ErrorCodes.PrinterNotFound,
                        Category = PrintErrorCategory.Printer,
                        Message = $"Máy in \"{printer}\" không nhận khổ giấy \"{(asDoc ? "A4" : configPaper)}\" khi in nhiều trang/tờ.",
                        Hint = "Chọn khổ giấy máy in hỗ trợ trong Print settings.",
                    });
            }
            else
            {
                // In thường: paper size luôn dọc theo user config (auto-rotate mode cho AsDocument/AsPrinter/Portrait).
                var paperW = (int)Math.Round(mmW / 25.4 * 100);
                var paperH = (int)Math.Round(mmH / 25.4 * 100);
                sheet = FindSupportedPaper(pd.PrinterSettings, sourceKind, paperW, paperH)
                    ?? new PaperSize(sourceKind == PaperKind.Custom ? "Custom" : sourceKind.ToString(), paperW, paperH);
            }
            pd.DefaultPageSettings.PaperSize = sheet;
            // Landscape explicit → cờ orientation ở DefaultPageSettings (DEVMODE dmOrientation).
            // Đây là cơ chế chuẩn (Edge/Foxit cũng dùng); set ở đây TRƯỚC Print() để driver nhận
            // dmOrientation=LANDSCAPE từ đầu job — KHÔNG set trong PrintPage event (quá muộn).
            // (Thử swap PaperSize ngang đã FAIL trên máy in vật lý — driver clamp về khổ dọc.)
            pd.DefaultPageSettings.Landscape = isLandscapeExplicit;
            if (isLandscapeExplicit && !pd.PrinterSettings.DefaultPageSettings.Landscape)
                pd.PrinterSettings.DefaultPageSettings.Landscape = true; // đồng bộ phòng driver đọc từ đây
            GdiLog($"GdiPrintEngine: orientation={userOrientation} paper='{sheet.PaperName ?? sheet.Kind.ToString()}' {sheet.Width}x{sheet.Height}(1/100in) landscape={pd.DefaultPageSettings.Landscape}");
            pd.PrinterSettings.Copies = 1; // tự bơm N bản collate-by-document bên dưới — tránh driver nhân đôi

            // ===== 2 mặt (duplex) =====
            if (job.Config.DuplexMode == PrintDuplexMode.LongEdge)
                pd.PrinterSettings.Duplex = Duplex.Vertical;
            else if (job.Config.DuplexMode == PrintDuplexMode.ShortEdge)
                pd.PrinterSettings.Duplex = Duplex.Horizontal;
            else if (job.Config.DuplexMode == PrintDuplexMode.Simplex)
                pd.PrinterSettings.Duplex = Duplex.Simplex;
            // AsPrinter → giữ mặc định driver (không set)

            // ===== Màu (ép đen trắng / ép màu) — driver quyết khi AsPrinter/AsDocument =====
            if (job.Config.ColorMode == PrintColorMode.Grayscale)
                pd.PrinterSettings.DefaultPageSettings.Color = false;
            else if (job.Config.ColorMode == PrintColorMode.Color)
                pd.PrinterSettings.DefaultPageSettings.Color = true;

            // Tự bơm N bản collate-by-document (hết mảng trang lại lặp) — KHÔNG set PrinterSettings.Copies
            // (driver tự nhân Copies → in N×N). Collation ByPages/AsPrinter bỏ qua cho PDF (driver quyết).
            var copies = Math.Max(job.Config.Copies, 1);
            var totalPages = images.Count * copies;
            var pumped = 0;
            var anyPageFailed = false;

            // Grid N-up: 2→[2×1], 4→[2×2], 6→[3×2], 9→[3×3], 16→[4×4]
            var nUpCols = nUp > 1 ? (int)Math.Ceiling(Math.Sqrt(nUp)) : 1;
            var nUpRows = nUp > 1 ? (int)Math.Ceiling(nUp / (double)nUpCols) : 1;

            pd.PrintPage += (_, e) =>
            {
                ct.ThrowIfCancellationRequested();
                // Vùng GIẤY driver cho vẽ. Graphics origin = (0,0) = GÓC TRÁI-TRÊN của vùng
                // printable (đã trừ hard margin nội bộ). Vẽ (x,y) trong e.Graphics là tọa độ
                // tương đối printable — KHÔNG cộng HardMarginX/Y.
                //
                // DÙNG e.Graphics.VisibleClipBounds (KHÔNG dùng e.PageSettings.PrintableArea):
                // khi cờ Landscape bật, PrintableArea vẫn trả khổ DỌC (826x1169) trong khi
                // graphics space đã xoay sang NGANG (1169x826) → vẽ theo PrintableArea bị co
                // nhỏ + lệch góc dưới phải (đã xác nhận bằng probe in thử, 2026-09-10).
                // VisibleClipBounds phản ánh đúng vùng vẽ thực của graphics cả dọc lẫn ngang.
                //
                // Lỗi cũ: code cộng `px + safe` → double offset → ảnh bị lệch góc dưới phải trên 1 số driver.
                var vb = e.Graphics is null ? RectangleF.Empty : e.Graphics.VisibleClipBounds;
                var pw = vb.Width;
                var ph = vb.Height;
                // Lề an toàn nhỏ (0.1 inch) — ảnh không dính sát mép cắt được của driver.
                var safe = 10f; // 1/100 inch
                var areaW = Math.Max(pw - 2 * safe, 1);
                var areaH = Math.Max(ph - 2 * safe, 1);

                // N-up: 1 tờ = grid (cols×rows) ô — vẽ tối đa nUp ảnh/tờ. Bình thường: 1 ảnh/tờ.
                var perSheet = nUp > 1 ? nUp : 1;
                var sheetStart = pumped; // ảnh đầu của tờ này
                for (var c = 0; c < perSheet; c++)
                {
                    var imgIdx = sheetStart + c;
                    if (imgIdx >= totalPages) break;
                    var img = images[imgIdx % images.Count];
                    Bitmap? bmp = null;
                    bool disposeBmp = false;
                    try
                    {
                        bmp = LoadImage(img.Png);
                        if (bmp is null) { anyPageFailed = true; continue; }
                        // Auto-rotate khi paper là khổ DỌC: AsDocument / AsPrinter / Portrait explicit.
                        // - AsDocument: file PDF có trang landscape → rotate để fit paper size dọc đã set
                        //   (SumatraPDF Print.cpp:628-639 advanced.autoRotate pattern).
                        // - Portrait explicit: landscape rotate 90° vừa tờ dọc (giống AsDocument).
                        // - Landscape explicit: paper đã là tờ NGANG → KHÔNG rotate, giữ đúng thiết kế.
                        var imgLandscape = img.WidthDip > img.HeightDip;
                        var isAutoMode = userOrientation == PrintOrientation.AsDocument
                                      || userOrientation == PrintOrientation.AsPrinter
                                      || userOrientation == PrintOrientation.Portrait;
                        if (isAutoMode && imgLandscape)
                        {
                            bmp.RotateFlip(System.Drawing.RotateFlipType.Rotate90FlipNone);
                        }
                        disposeBmp = true;
                        try
                        {
                            if (e.Graphics is null) break;
                            if (nUp > 1)
                            {
                                // Vẽ vào ô grid (có lề nhẹ giữa các ô — 4% chiều mỗi chiều).
                                var col = c % nUpCols;
                                var row = c / nUpCols;
                                var cw = areaW / (double)nUpCols;
                                var ch = areaH / (double)nUpRows;
                                var cell = new RectangleF(
                                    (float)(safe + col * cw), (float)(safe + row * ch),
                                    (float)cw, (float)ch);
                                var scale = Math.Min(cell.Width * 0.92 / bmp.Width, cell.Height * 0.92 / bmp.Height);
                                var dw = (float)(bmp.Width * scale);
                                var dh = (float)(bmp.Height * scale);
                                e.Graphics.DrawImage(bmp,
                                    cell.X + (cell.Width - dw) / 2, cell.Y + (cell.Height - dh) / 2, dw, dh);
                            }
                            else
                            {
                                // Vẽ ảnh fit trong vùng printable (đã trừ safe margin). Graphics origin
                                // đã là góc printable — KHÔNG cộng HardMargin (sửa bug lệch góc dưới).
                                var scale = Math.Min(areaW / (double)bmp.Width, areaH / (double)bmp.Height);
                                var dw = (float)(bmp.Width * scale);
                                var dh = (float)(bmp.Height * scale);
                                e.Graphics.DrawImage(bmp,
                                    safe + (areaW - dw) / 2, safe + (areaH - dh) / 2, dw, dh);
                            }
                        }
                        catch { anyPageFailed = true; }
                    }
                    finally
                    {
                        if (disposeBmp) bmp?.Dispose();
                    }
                }

                pumped += perSheet; // mỗi tờ tiêu thụ perSheet ảnh
                e.HasMorePages = pumped < totalPages;
            };

            pd.PrintController = new StandardPrintController(); // không popup "Đang in..." chờ user
            pd.Print();

            // In xong nhưng chưa bơm được ảnh nào (page trống/có lỗi ở trang đầu) → báo lỗi rõ.
            if (anyPageFailed)
                return Result<bool>.Fail(new PrintError
                {
                    Code = ErrorCodes.FileCorrupted,
                    Category = PrintErrorCategory.App,
                    Message = $"Một số trang của {job.FileName} không in được (ảnh hỏng).",
                    Hint = "File PDF có thể bị hỏng hoặc bị mật khẩu. Thử mở trong Edge/Adobe xem được không.",
                });

            if (pumped == 0)
                return Result<bool>.Fail(new PrintError
                {
                    Code = ErrorCodes.FileCorrupted,
                    Category = PrintErrorCategory.App,
                    Message = $"Không in được trang nào từ {job.FileName}.",
                    Hint = "File PDF có thể bị hỏng hoặc bị mật khẩu. Thử mở trong Edge/Adobe xem được không.",
                });

            return Result<bool>.Ok(true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.SpoolerFailed,
                Category = PrintErrorCategory.Printer,
                Message = $"Lỗi khi in {job.FileName} tới \"{printer}\".",
                Hint = "Máy in có thể đang offline hoặc driver lỗi. Kiểm tra hàng đợi máy in (devices and printers).",
                Detail = ex.ToString(),
            });
        }
    }

    /// <summary>
    /// Khổ giấy GDI cho 1 trang PDF (kích thước DIPs — 1/96 inch). Trả khổ DỌC (chiều nhỏ trước):
    /// (PaperKind chuẩn nếu khớp A4/A3/Letter/Legal/A5/B5, ngược lại Custom, đơn vị 1/100 inch).
    /// </summary>
    internal static (PaperKind Kind, int W100, int H100) PaperSizeFor(double widthDip, double heightDip)
    {
        var inchesW = Math.Max(widthDip, 1) / 96.0;
        var inchesH = Math.Max(heightDip, 1) / 96.0;
        // Portrait: W ≤ H. Landscape: khổ DỌC = (H, W) — GDI Landscape xoay giấy.
        var (pw, ph) = inchesW <= inchesH ? (inchesW, inchesH) : (inchesH, inchesW);

        // 1/100 inch gần đúng kích thước chuẩn (mm → inch). Dung sai ±2mm.
        static bool Near(double w, double h, int mmW, int mmH)
            => Math.Abs(w - mmW / 25.4) < 0.08 && Math.Abs(h - mmH / 25.4) < 0.08;

        var kind = PaperKind.Custom;
        if (Near(pw, ph, 210, 297)) kind = PaperKind.A4;
        else if (Near(pw, ph, 297, 420)) kind = PaperKind.A3;
        else if (Near(pw, ph, 148, 210)) kind = PaperKind.A5;
        else if (Near(pw, ph, 216, 279)) kind = PaperKind.Letter;
        else if (Near(pw, ph, 216, 356)) kind = PaperKind.Legal;
        else if (Near(pw, ph, 176, 250)) kind = PaperKind.B5;

        return (kind, (int)Math.Round(pw * 100), (int)Math.Round(ph * 100));
    }

    /// <summary>PNG bytes → Bitmap (dispose sau khi vẽ). null nếu ảnh hỏng.</summary>
    private static Bitmap? LoadImage(byte[] png)
    {
        try { using var ms = new MemoryStream(png); return new Bitmap(ms); }
        catch { return null; }
    }

    /// <summary>DPI render cho GDI in PDF — cao hơn CdpPrintParams.DpiFor (vốn tối ưu browser ~150).
    /// PDF chứa text/vector → 300dpi mặc định cho nét; High 300, Medium/Low/Draft giảm dần.</summary>
    private static int DpiForGdi(PrintQuality quality) => quality switch
    {
        PrintQuality.High => 400,
        PrintQuality.Medium => 300,
        PrintQuality.Low => 200,
        PrintQuality.Draft => 150,
        _ => 300, // AsPrinter — mặc định 300 cho sắc nét
    };

    /// <summary>Tìm khổ giấy chuẩn (PaperKind) máy in HỖ TRỢ — ưu tiên PaperKind khớp + kích thước sát.
    /// Trả null → caller dùng khổ custom.</summary>
    private static PaperSize? FindSupportedPaper(PrinterSettings settings, PaperKind kind, int w100, int h100)
    {
        try
        {
            foreach (PaperSize ps in settings.PaperSizes)
            {
                if (kind != PaperKind.Custom && ps.Kind == kind)
                    return ps; // khổ chuẩn đúng (A4...) driver nhận chắc chắn
            }
            // Không có kind chuẩn → tìm khổ gần (dung sai 3mm) phòng driver đặt tên khác.
            foreach (PaperSize ps in settings.PaperSizes)
            {
                if (Math.Abs(ps.Width - w100) <= 12 && Math.Abs(ps.Height - h100) <= 12)
                    return ps;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Tìm khổ giấy THEO TÊN (không phải PaperKind) — dùng cho N-up khi user chọn khổ cụ thể (A4/A3...).
    /// Trả null → không có khổ đó trên máy in.</summary>
    private static PaperSize? PaperSizeFromName(PrinterSettings settings, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            foreach (PaperSize ps in settings.PaperSizes)
            {
                if (ps.PaperName != null
                    && ps.PaperName.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                    return ps;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Ghi log debug vào %TEMP%\printonator-office.log — engine GDI chạy đường nào, resolve máy gì.</summary>
    private static void GdiLog(string msg)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "printonator-office.log"),
                $"{DateTimeOffset.Now:O} {msg}\n");
        }
        catch { }
    }
}
