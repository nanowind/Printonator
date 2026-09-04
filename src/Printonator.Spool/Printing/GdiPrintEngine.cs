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
        // PDF → copy thẳng file (giữ nguyên, không render — tránh chụp UI browser vào file xuất).
        // Định dạng khác (ảnh/TXT...) → browser render ra PDF cạnh file gốc.
        if (PrinterService.IsVirtualPrinter(printer))
        {
            if (job.Format.Equals("PDF", StringComparison.OrdinalIgnoreCase))
            {
                // Máy ảo + file đã là PDF → chỉ cần copy sang đường xuất (PdfOutputPath).
                var outPdf = PrinterService.PdfOutputPath(job);
                if (outPdf is null)
                    return Result<bool>.Fail(new PrintError
                    {
                        Code = ErrorCodes.SpoolerFailed,
                        Category = PrintErrorCategory.Printer,
                        Message = $"Không xuất được PDF cho \"{job.FileName}\".",
                        Hint = "Kiểm tra đường dẫn lưu PDF.",
                    });
                if (outPdf.Equals(job.FilePath, StringComparison.OrdinalIgnoreCase))
                    return Result<bool>.Fail(new PrintError
                    {
                        Code = ErrorCodes.SpoolerFailed,
                        Category = PrintErrorCategory.Printer,
                        Message = $"Không xuất được PDF cho \"{job.FileName}\" (trùng file gốc).",
                        Hint = "Đổi tên file PDF nguồn hoặc chọn máy in giấy.",
                    });
                try
                {
                    System.IO.File.Copy(job.FilePath, outPdf, overwrite: true);
                }
                catch (Exception ex)
                {
                    GdiLog($"GdiPrintEngine: COPY PDF LỖI '{job.FilePath}' → '{outPdf}': {ex.Message}");
                    return Result<bool>.Fail(new PrintError
                    {
                        Code = ErrorCodes.SpoolerFailed,
                        Category = PrintErrorCategory.Printer,
                        Message = $"Không lưu được PDF ra \"{outPdf}\".",
                        Hint = ex.Message,
                    });
                }
                if (job.PageCount <= 0)
                {
                    var n = await WindowsPdfRasterizer.PdfPageCountAsync(job.FilePath, ct);
                    if (n > 0) job.PageCount = n;
                }
                GdiLog($"GdiPrintEngine: máy ảo '{printer}' + PDF → copy thẳng ra '{outPdf}'");
                return Result<bool>.Ok(true);
            }
            GdiLog($"GdiPrintEngine: máy ảo '{printer}' ({job.Format}) → browser render (xuất PDF cạnh file)");
            return await _browserInner.PrintAsync(job, ct);
        }

        // Đếm số trang PDF TRƯỚC — ResolveSelectedPages cần PageCount để lọc range/parity đúng.
        if (job.PageCount <= 0)
        {
            var n = await WindowsPdfRasterizer.PdfPageCountAsync(job.FilePath, ct);
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
        var r = await Task.Run(() => PrintImagesToPrinter(job, printer, imgs, ct), ct);
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

            // Khổ giấy theo trang GỐC PDF: PaperSize LUÔN là khổ DỌC của tờ giấy (1/100 inch) + Landscape
            // riêng — tránh xoay 2 lần (GDI xoay theo cờ Landscape). Ưu tiên khổ CHUẨN của CHÍNH máy in
            // (PrinterSettings.PaperSizes khớp PaperKind) → driver chọn đúng khay/cài đặt; không có → custom.
            var nUp = Math.Max(job.Config.PagesPerSheet, 1);
            var landscape = nUp > 1
                ? false // N-up: luôn in khổ giấy DỌC cấu hình (grid trang trên 1 tờ)
                : images[0].WidthDip > images[0].HeightDip;
            // Khổ giấy: bình thường = khổ gốc trang PDF. N-up → khổ giấy user đặt (PaperSize) hoặc A4 mặc định.
            var paper = job.Config.PaperSize;
            var asDoc = string.IsNullOrWhiteSpace(paper) || paper.Equals(PaperCatalog.AsDocument, StringComparison.OrdinalIgnoreCase);
            PaperSize? sheet;
            if (nUp > 1)
            {
                sheet = asDoc ? FindSupportedPaper(pd.PrinterSettings, PaperKind.A4, 827, 1169)
                              : PaperSizeFromName(pd.PrinterSettings, paper);
                sheet ??= FindSupportedPaper(pd.PrinterSettings, PaperKind.A4, 827, 1169);
                if (sheet is null)
                    return Result<bool>.Fail(new PrintError
                    {
                        Code = ErrorCodes.PrinterNotFound,
                        Category = PrintErrorCategory.Printer,
                        Message = $"Máy in \"{printer}\" không nhận khổ giấy \"{(asDoc ? "A4" : paper)}\" khi in nhiều trang/tờ.",
                        Hint = "Chọn khổ giấy máy in hỗ trợ trong Print settings.",
                    });
            }
            else
            {
                var (kind, w100, h100) = PaperSizeFor(images[0].WidthDip, images[0].HeightDip);
                sheet = FindSupportedPaper(pd.PrinterSettings, kind, w100, h100)
                    ?? new PaperSize(kind == PaperKind.Custom ? "Custom" : kind.ToString(), w100, h100);
            }
            pd.DefaultPageSettings.PaperSize = sheet;
            pd.DefaultPageSettings.Landscape = landscape;
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
                // Vùng GIẤY driver cho vẽ (printable area = PageBounds trừ hard margins). Vẽ ảnh trong
                // vùng này, KHÔNG phủ kín PageBounds — ảnh bị hard-margin cắt mép phải/dưới (lỗi mất chữ).
                // Graphics origin = (0,0) góc giấy; hard margin là khoảng driver không cho in được.
                var page = e.PageSettings;
                var px = page.HardMarginX;            // margin trái (đơn vị 1/100 inch)
                var py = page.HardMarginY;            // margin trên
                var pw = page.PrintableArea.Width;    // vùng in được
                var ph = page.PrintableArea.Height;
                // Lề an toàn nhỏ (0.1 inch) — ảnh không dính sát mép cắt được của driver.
                var safe = 10f; // 1/100 inch
                var areaX = px + safe;
                var areaY = py + safe;
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
                    using var bmp = LoadImage(img.Png);
                    if (bmp is null) { anyPageFailed = true; continue; }
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
                                (float)(areaX + col * cw), (float)(areaY + row * ch),
                                (float)cw, (float)ch);
                            var scale = Math.Min(cell.Width * 0.92 / bmp.Width, cell.Height * 0.92 / bmp.Height);
                            var dw = (float)(bmp.Width * scale);
                            var dh = (float)(bmp.Height * scale);
                            e.Graphics.DrawImage(bmp,
                                cell.X + (cell.Width - dw) / 2, cell.Y + (cell.Height - dh) / 2, dw, dh);
                        }
                        else
                        {
                            // Vẽ ảnh fit trong vùng in được (đã trừ hard margin) — không bị cắt mép.
                            var scale = Math.Min(areaW / (double)bmp.Width, areaH / (double)bmp.Height);
                            var dw = (float)(bmp.Width * scale);
                            var dh = (float)(bmp.Height * scale);
                            e.Graphics.DrawImage(bmp,
                                areaX + (areaW - dw) / 2, areaY + (areaH - dh) / 2, dw, dh);
                        }
                    }
                    catch { anyPageFailed = true; }
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
