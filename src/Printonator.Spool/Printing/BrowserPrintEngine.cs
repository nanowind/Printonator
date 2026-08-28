using System.IO;
using Printonator.Core;
using Printonator.Core.Models;

namespace Printonator.Spool.Printing;

/// <summary>
/// ENGINE RENDER bằng TRÌNH DUYỆT có sẵn trên máy user (Edge — có sẵn Windows 10/11; Chrome):
/// file PDF/ảnh/TXT → headless browser render (CDP Page.printToPDF) với ĐÚNG page range, scale,
/// khổ giấy, chiều → PDF tạm → in qua "printto" tới máy đã chọn (N bản).
/// Đây là thứ shell printto thuần KHÔNG làm được (shell bỏ qua page range/scale/khổ giấy).
/// KHÔNG bundle gì — máy ai cũng có browser. Không có browser → CanHandle=false → rớt xuống SpoolPrintEngine.
/// Giới hạn: N-up/booklet/tray phụ thuộc driver (mở Printing Preferences để chỉnh).
/// </summary>
public sealed class BrowserPrintEngine : IPrintEngine
{
    /// <summary>
    /// PID của các headless browser do engine này spawn (để dọn mồ côi đúng khi đóng app mà không
    /// đụng browser THẬT của user). Thread-safe — chạy từ nhiều job song song.
    /// </summary>
    public static System.Collections.Concurrent.ConcurrentBag<int> SpawnedBrowserPids { get; } = new();

    private static readonly string[] BrowserFormats =
        ["PDF", "PNG", "JPG", "JPEG", "BMP", "GIF", "TIF", "TIFF", "WEBP", "ICO", "JFIF", "TXT", "CSV"];

    private readonly Func<(string Name, string Path)?> _browserResolver;
    private readonly SpoolPrintEngine _fallback = new();

    public BrowserPrintEngine(Func<(string Name, string Path)?>? browserResolver = null)
        => _browserResolver = browserResolver ?? new BrowserLocator().ResolveBrowser;

    public bool CanHandle(string format)
    {
        if (!BrowserFormats.Contains(format.ToUpperInvariant())) return false;
        try { return _browserResolver() is { } b && !string.IsNullOrEmpty(b.Path); } catch { return false; }
    }

    public async Task<Result<bool>> PrintAsync(PrintJob job, CancellationToken ct)
    {
        var browser = GetBrowser();
        if (browser is not { } b)
            return Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.EngineNotFound,
                Category = PrintErrorCategory.App,
                Message = $"Không tìm thấy Edge/Chrome để render {job.FileName}.",
                Hint = "Máy cần có Microsoft Edge hoặc Google Chrome (mặc định Windows 10/11 có sẵn).",
            });

        if (!File.Exists(job.FilePath))
            return Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.FileNotFound,
                Category = PrintErrorCategory.System,
                Message = $"File không tồn tại: {job.FilePath}",
                Hint = "File bị xóa hoặc di chuyển — kiểm tra lại đường dẫn.",
            });

        // Máy in ẢO (PDF/XPS...) → luôn render rồi LƯU PDF cạnh file gốc, KHÔNG đẩy spooler
        // (shell printto tới PDF printer mở hộp "Save As" vô hình → "báo xong không ra file").
        var pdfOut = PrinterService.PdfOutputPath(job);

        // FAST-PATH: cấu hình "như default" (in toàn bộ, scale/chỉnh theo file, A4, dọc) thì
        // KHÔNG cần render browser — đi thẳng shell printto (nhanh, không mọc Chrome).
        // Chỉ render khi user đặt option mà shell không làm được: cắt trang, scale, ngang, khổ lạ.
        // (Máy ảo → không fast-path: phải render để ra file PDF.)
        if (pdfOut is null && !NeedsBrowserRender(job))
            return await _fallback.PrintAsync(job, ct);

        var tempDir = Path.Combine(Path.GetTempPath(), $"printonator-browser-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var url = new Uri(job.FilePath).AbsoluteUri;
            var isPdf = job.Format.Equals("PDF", StringComparison.OrdinalIgnoreCase);
            bool ok; string? base64; string? err;

            if (isPdf && CdpPrintParams.ResolveSelectedPages(job) is { Length: > 0 } sel)
            {
                // PDF + range cụ thể → SLICING bằng PDF renderer CÓ SẴN của Windows (Windows.Data.Pdf):
                // render đúng các trang chọn → HTML ảnh (page-break) → headless printToPDF đúng khổ gốc.
                // (PDF viewer browser TỪ CHỐI pageRanges CDP — đã verify; Windows API không cần bundle gì.)
                var rendered = await WindowsPdfRasterizer.RenderPagesAsync(
                    job.FilePath, sel, ct, CdpPrintParams.DpiFor(job.Config.Quality));
                if (rendered.IsSuccess && rendered.Value is { Count: > 0 } imgs)
                {
                    var htmlPath = Path.Combine(tempDir, "slice.html");
                    await File.WriteAllTextAsync(htmlPath, WindowsPdfRasterizer.BuildHtml(imgs), ct);
                    var first = imgs[0];
                    (ok, base64, err) = await DevToolsPrintClient.PrintPdfAsync(
                        b.Path, new Uri(htmlPath).AbsoluteUri,
                        CdpPrintParams.BuildForSlicedImages(first.WidthDip / 96.0, first.HeightDip / 96.0),
                        Path.Combine(tempDir, "profile"), ct);
                }
                else
                {
                    // Không rasterize được (khóa/hỏng) → in cả file qua browser (giữ scale/khổ giấy)
                    (ok, base64, err) = await DevToolsPrintClient.PrintPdfAsync(
                        b.Path, url, CdpPrintParams.Build(job.Config, null), Path.Combine(tempDir, "profile"), ct);
                }
            }
            else if (isPdf)
            {
                // PDF in toàn bộ → printToPDF (giữ scale/khổ giấy/chiều — không có pageRanges)
                (ok, base64, err) = await DevToolsPrintClient.PrintPdfAsync(
                    b.Path, url, CdpPrintParams.Build(job.Config, null), Path.Combine(tempDir, "profile"), ct);
            }
            else
            {
                // Non-PDF: pageRanges CDP hoạt động (đã verify) — cắt trang khi có range
                (ok, base64, err) = await DevToolsPrintClient.PrintPdfAsync(
                    b.Path, url, CdpPrintParams.Build(job.Config, CdpPrintParams.BuildPageRanges(job)),
                    Path.Combine(tempDir, "profile"), ct);
            }

            if (!ok || string.IsNullOrEmpty(base64))
            {
                // Rớt MỀM về in shell (file gốc, KHÔNG có options) — in được vẫn hơn lỗi.
                // Vd một số bản Edge 151+ không chạy headless/CDP trên máy user.
                return await _fallback.PrintAsync(job, ct);
            }

            var outPdf = Path.Combine(tempDir, "out.pdf");
            await File.WriteAllBytesAsync(outPdf, Convert.FromBase64String(base64), ct);

            // Máy in ảo → lưu thẳng PDF ra cạnh file gốc (đúng ý "xuất PDF"), không đụng spooler.
            if (pdfOut is not null)
            {
                File.Copy(outPdf, pdfOut, overwrite: true);
                if (job.PageCount <= 0) job.PageCount = ResolveCount(job);
                return Result<bool>.Ok(true);
            }

            // In PDF tạm N bản qua shell printto (in đúng máy đã chọn; bỏ qua page range vì đã cắt trong PDF)
            var copies = Math.Max(job.Config.Copies, 1);
            var tmp = new PrintJob
            {
                FilePath = outPdf,
                FileName = Path.GetFileName(job.FilePath) + " (render)",
                Format = "PDF",
                Config = new PrintConfig { PrinterName = job.Config.PrinterName, Copies = 1 },
                PageCount = 1,
            };

            for (var i = 0; i < copies; i++)
            {
                var r = await _fallback.PrintAsync(tmp, ct);
                if (!r.IsSuccess)
                    return Result<bool>.Fail(new PrintError
                    {
                        Code = r.Error!.Code,
                        Category = r.Error.Category,
                        Message = r.Error.Message,
                        Hint = r.Error.Hint,
                        Detail = $"[browser render {b.Name}] {r.Error.Detail}",
                    });
            }

            // Số trang thực tế đã probe (để state/progress chính xác)
            if (job.PageCount <= 0) job.PageCount = ResolveCount(job);
            return Result<bool>.Ok(true);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            }
            catch { }
        }
    }

    private static int ResolveCount(PrintJob job)
    {
        try
        {
            var r = job.ResolvePhysicalPages();
            return r.IsSuccess && r.Value is { Length: > 0 } ? r.Value.Length : 1;
        }
        catch { return 1; }
    }

    /// <summary>
    /// Có cần render browser hay không? Chỉ render khi user đặt option mà shell printto bỏ qua:
    /// cắt trang / lọc lẻ-chẵn (PDF slice hoặc pageRanges CDP), khổ giấy "theo tài liệu", scale,
    /// chất lượng rasterize, ép chiều ngang, khổ giấy khác A4. Còn lại ("mặc định / theo máy") → shell thẳng.
    /// </summary>
    public static bool NeedsBrowserRender(PrintJob job)
    {
        if (job?.Config is null) return false;
        var cfg = job.Config;

        // Cắt trang hoặc lọc lẻ/chẵn (ResolveSelectedPages khác null khi cần lọc)
        if (CdpPrintParams.ResolveSelectedPages(job) is { Length: > 0 }) return true;

        // Khổ giấy "theo tài liệu" → cần render để dùng đúng khổ gốc từng trang
        if (cfg.PaperSize == PaperCatalog.AsDocument) return true;

        // Scale khác mặc định
        if (cfg.ScaleMode is not (PrintScaleMode.AsDocument or PrintScaleMode.Original))
            return true;

        // Chất lượng khác driver → cần rasterize đúng DPI
        if (cfg.Quality != PrintQuality.AsPrinter)
            return true;

        // Ép chiều ngang (dọc = theo file, không cần render)
        if (cfg.Orientation == PrintOrientation.Landscape)
            return true;

        // Khổ giấy cụ thể khác A4 (A4 = mặc định app → in thẳng)
        var paper = (cfg.PaperSize ?? "").Trim();
        if (paper.Length > 0 && !paper.Equals("A4", StringComparison.OrdinalIgnoreCase)
            && !paper.Equals(PaperCatalog.AsDocument, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private (string Name, string Path)? GetBrowser()
    {
        try { return _browserResolver(); } catch { return null; }
    }
}