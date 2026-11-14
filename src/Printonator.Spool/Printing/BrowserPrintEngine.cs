using System.IO;
using Printonator.Core;
using Printonator.Core.IO;
using Printonator.Core.Models;
using Printonator.Core.Printing;

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
            return Result<bool>.Fail(PrintErrorFactory.EngineNotFound("Edge/Chrome để render " + job.FileName));

        if (!File.Exists(job.FilePath))
            return Result<bool>.Fail(PrintErrorFactory.FileNotFound(job.FilePath));

        // Chưa probe được số trang (TXT/CSV...) → không biết trang nào là lẻ/chẵn → không lọc được.
        // Render ra PDF rồi in tiếp là MẤT luôn bộ lọc (tmpCfg.Parity = All cho shell), nên từ chối
        // TRƯỚC khi render thay vì in cả file rồi báo Done.
        if (job.Config.Parity != PageParityFilter.All && job.PageCount <= 0)
        {
            var oddEven = job.Config.Parity == PageParityFilter.Even ? "chẵn" : "lẻ";
            BrowserLog($"{job.FileName}: chưa biết số trang → không lọc được trang {oddEven}; báo lỗi.");
            return Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.UnsupportedOption,
                Category = PrintErrorCategory.Config,
                Message = $"Chưa biết file \"{job.FileName}\" có bao nhiêu trang trước khi in, nên không lọc riêng trang {oddEven} được.",
                Hint = "Nhập page range cụ thể (vd \"1,3,5\") thay cho bộ lọc lẻ/chẵn, hoặc chuyển file sang PDF rồi in lại.",
            });
        }

        // Máy in ẢO (PDF/XPS...) → luôn render rồi LƯU PDF cạnh file gốc, KHÔNG đẩy spooler
        // (shell printto tới PDF printer mở hộp "Save As" vô hình → "báo xong không ra file").
        var pdfOut = PrinterService.PdfOutputPath(job);

        // FAST-PATH: cấu hình "như default" (in toàn bộ, scale/chỉnh theo file, A4, dọc) thì
        // KHÔNG cần render browser — đi thẳng shell printto (nhanh, không mọc Chrome).
        // Chỉ render khi user đặt option mà shell không làm được: cắt trang, scale, ngang, khổ lạ.
        // (Máy ảo → không fast-path: phải render để ra file PDF.)
        if (pdfOut is null && !NeedsBrowserRender(job))
        {
            // Không có option nào cần render → shell in thẳng là ĐÚNG, không mất gì.
            BrowserLog($"{job.FileName}: fast-path — in thẳng qua shell (không có option cần render).");
            return await _fallback.PrintAsync(job, ct);
        }

        using var temp = TempDir.Create("printonator-browser");
        var tempDir = temp.FullPath; // keep existing variable name to minimize changes
        {
            var url = new Uri(job.FilePath).AbsoluteUri;
            var isPdf = job.Format.Equals("PDF", StringComparison.OrdinalIgnoreCase);
            bool ok; string? base64; string? err;

            // Chọn trang MỘT LẦN, bằng bản CHECKED: range sai / lọc lẻ-chẵn hết trang → LỖI TRƯỚC khi
            // render. Bản lenient trả null = "in cả file" im lặng (range "1-20" trên file 10 trang vẫn
            // in đủ 10 trang, job Done).
            var sel = CdpPrintParams.ResolveSelectedPagesChecked(job);
            if (!sel.IsSuccess) return FailSelection(job, sel.Error!);

            if (isPdf && sel.Value is { Length: > 0 } selPages)
            {
                // PDF + range cụ thể → SLICING bằng PDF renderer CÓ SẴN của Windows (Windows.Data.Pdf):
                // render đúng các trang chọn → HTML ảnh (page-break) → headless printToPDF đúng khổ gốc.
                // (PDF viewer browser TỪ CHỐI pageRanges CDP — đã verify; Windows API không cần bundle gì.)
                var rendered = await WindowsPdfRasterizer.RenderPagesAsync(
                    job.FilePath, selPages, ct, CdpPrintParams.DpiFor(job.Config.Quality));
                if (rendered.IsSuccess && rendered.Value is { Count: > 0 } imgs)
                {
                    // Chiều tờ theo user: Ngang → tờ NGANG, Dọc → tờ DỌC, còn lại (Theo tài liệu /
                    // Theo máy) → theo khổ trang gốc. KHÔNG xoay nội dung: trang lệch chiều tờ được
                    // thu nhỏ vừa khổ, canh giữa (giống GDI fit ảnh vào vùng in) — chữ giữ đúng chiều đọc.
                    bool? targetLandscape = job.Config.Orientation switch
                    {
                        PrintOrientation.Landscape => true,
                        PrintOrientation.Portrait => false,
                        _ => null,
                    };
                    var pages = imgs
                        .Select(p => targetLandscape is { } tl ? WindowsPdfRasterizer.FitToPaper(p, tl) : p)
                        .ToList();
                    var htmlPath = Path.Combine(tempDir, "slice.html");
                    await File.WriteAllTextAsync(htmlPath,
                        WindowsPdfRasterizer.BuildHtml(pages, job.Config.ColorMode == PrintColorMode.Grayscale), ct);
                    var first = pages[0];
                    (ok, base64, err) = await DevToolsPrintClient.PrintPdfAsync(
                        b.Path, new Uri(htmlPath).AbsoluteUri,
                        CdpPrintParams.BuildForSlicedImages(
                            first.WidthDip / 96.0, first.HeightDip / 96.0,
                            targetLandscape ?? first.WidthDip > first.HeightDip),
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
                // B&W: không chèn được CSS vào PDF gốc → từ đây màu do dmColor của driver quyết.
                if (job.Config.ColorMode == PrintColorMode.Grayscale)
                    BrowserLog($"{job.FileName}: cần B&W nhưng render PDF gốc — không chèn được CSS lọc xám " +
                               "vào PDF, từ đây màu phụ thuộc driver (driver bỏ qua dmColor → ra màu).");
                (ok, base64, err) = await DevToolsPrintClient.PrintPdfAsync(
                    b.Path, url, CdpPrintParams.Build(job.Config, null), Path.Combine(tempDir, "profile"), ct);
            }
            else if (job.Config.ColorMode == PrintColorMode.Grayscale)
            {
                // Grayscale: đưa file vào HTML bọc để chèn được CSS lọc xám (ảnh/TXT không có HTML
                // nên trước đây in màu trên driver bỏ qua dmColor).
                // KHÔNG @page{margin:0}: nó đè lên margin CDP (CdpPrintParams.Build) → B&W in tràn lề
                // còn màu thì không, và có thể lọt vùng không in được của máy. Margin chỉ do CDP quyết.
                // KHÔNG ép page-break-after: ảnh cao hơn khung trang bị cắt sang trang 2 (1080x1920 → 2 trang).
                var body = await MergePrintEngine.BuildFileHtmlAsync(job, ct);
                // Ảnh: cột chiều cao html→body→div là 100% để img max-height:100% có nghĩa → ảnh
                // (kể cả 1080x1920, 800x4000) vừa ĐÚNG 1 trang, không tràn. !important để thắng style
                // inline width/height:100% của BuildFileHtmlAsync (dùng chung với đường gộp lô — không sửa).
                // TXT/CSV: <pre> chảy tự nhiên nhiều trang, chỉ bỏ page-break thừa ở cuối.
                var fmt = (job.Format ?? "").ToUpperInvariant();
                var fit = fmt is "TXT" or "CSV"
                    ? "<style>html,body{margin:0}div{page-break-after:auto!important;break-after:auto!important}</style>"
                    : "<style>html,body{height:100%;margin:0}" +
                      "div{height:100%;page-break-after:auto!important;break-after:auto!important}" +
                      "img{width:auto!important;height:auto!important;max-width:100%!important;" +
                      "max-height:100%!important;object-fit:contain;display:block;margin:0 auto}</style>";
                var htmlPath = Path.Combine(tempDir, "grayscale.html");
                await File.WriteAllTextAsync(htmlPath,
                    "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
                    BrowserPrintPipeline.GrayscaleFilterCss(job.Config) + fit + "</head><body>" +
                    body + "</body></html>", ct);
                // pageRanges = null: HTML bọc không cùng phân trang với file gốc — range tính cho file
                // gốc không áp lên nó.
                (ok, base64, err) = await DevToolsPrintClient.PrintPdfAsync(
                    b.Path, new Uri(htmlPath).AbsoluteUri, CdpPrintParams.Build(job.Config, null),
                    Path.Combine(tempDir, "profile"), ct);
            }
            else
            {
                // Non-PDF: pageRanges CDP hoạt động (đã verify) — cắt trang khi có range.
                // Bản CHECKED: range sai → LỖI, không build params không có pageRanges (in cả file im lặng).
                var ranges = CdpPrintParams.BuildPageRangesChecked(job);
                if (!ranges.IsSuccess) return FailSelection(job, ranges.Error!);
                (ok, base64, err) = await DevToolsPrintClient.PrintPdfAsync(
                    b.Path, url, CdpPrintParams.Build(job.Config, ranges.Value),
                    Path.Combine(tempDir, "profile"), ct);
            }

            if (!ok || string.IsNullOrEmpty(base64))
            {
                // Render lỗi: quyết định theo loại cấu hình, KHÔNG theo việc "đã tới nhánh này".
                // "Khổ theo tài liệu" là MẶC ĐỊNH của app cho PDF/Office → nhánh render là đường thường,
                // không phải ngoại lệ. Cấu hình chỉ có option shell làm được (toàn bộ, dọc, khổ tài liệu/A4,
                // scale theo file, chất lượng/theo máy) → rớt về shell vẫn in đúng nội dung, chỉ mất phần
                // "dùng đúng khổ gốc từng trang". Còn cấu hình có option shell KHÔNG làm được (cắt trang,
                // scale, chất lượng, 1/2 mặt, chiều ngang, khổ lạ) → in bằng shell là in sai → chặn job.
                if (RequestsRenderOnlyOptions(job))
                {
                    BrowserLog($"{job.FileName}: render lỗi ({err ?? "không rõ"}) → KHÔNG rớt về shell vì sẽ bỏ option user chọn; báo lỗi.");
                    return Result<bool>.Fail(new PrintError
                    {
                        Code = ErrorCodes.EngineFailed,
                        Category = PrintErrorCategory.App,
                        Message = $"Không dựng được bản in cho \"{job.FileName}\" bằng trình duyệt (Edge/Chrome).",
                        Hint = "Cài hoặc cập nhật Microsoft Edge / Google Chrome, hoặc bỏ các tuỳ chọn cần dựng bản in " +
                               "(cắt trang, khổ giấy, chiều giấy, tỉ lệ, chất lượng, 1/2 mặt) rồi in lại.",
                        Detail = err,
                    });
                }

                // ponytail: fallback shell chỉ đúng cho cấu hình mà shell tôn trọng được — khổ theo tài liệu
                // vẫn có thể ra khổ driver (A4) thay vì khổ gốc từng trang. Nâng cấp khi cần khổ gốc tuyệt đối
                // mà không có browser: thêm raster fallback (WindowsPdfRasterizer → GDI) tôn trọng option.
                BrowserLog($"{job.FileName}: render lỗi ({err ?? "không rõ"}) → in bằng shell ở cấu hình mặc định của driver "
                           + "(có thể lệch khổ gốc từng trang).");
                return await _fallback.PrintAsync(job, ct);
            }

            var outPdf = Path.Combine(tempDir, "out.pdf");
            await File.WriteAllBytesAsync(outPdf, Convert.FromBase64String(base64), ct);

            // Máy in ảo → lưu thẳng PDF ra cạnh file gốc (đúng ý "xuất PDF"), không đụng spooler.
            if (pdfOut is not null)
            {
                try
                {
                    File.Copy(outPdf, pdfOut, overwrite: true);
                }
                catch (Exception ex)
                {
                    // File xuất đang bị chương trình khác giữ (viewer đang mở, hoặc lần in trước chưa nhả)
                    // → trả lỗi RÕ, không để exception thoát ra giết cả job.
                    return Result<bool>.Fail(new PrintError
                    {
                        Code = ErrorCodes.SpoolerFailed,
                        Category = PrintErrorCategory.Printer,
                        Message = $"Không lưu được PDF ra \"{pdfOut}\".",
                        Hint = "File PDF đang được mở ở chương trình khác — đóng lại rồi in lại.",
                        Detail = ex.Message,
                    });
                }
                if (job.PageCount <= 0) job.PageCount = ResolveCount(job);
                return Result<bool>.Ok(true);
            }

            // In PDF tạm N bản qua shell printto (in đúng máy đã chọn; bỏ qua page range vì đã cắt trong PDF)
            var copies = Math.Max(job.Config.Copies, 1);
            // QUAN TRỌNG: bản sao config PHẢI giữ duplex/color/paper của job gốc để GdiPrintEngine
            // (trong chuỗi engine khi in PDF tạm) ép đúng 1/2 mặt. (Bug cũ: chỉ PrinterName+Copies
            // → in theo driver, user chọn 1 mặt vẫn ra 2 mặt.)
            var tmpCfg = new PrintConfig { PrinterName = job.Config.PrinterName };
            job.Config.CopyInto(tmpCfg);   // giữ duplex/color/paper của job gốc
            tmpCfg.Copies = 1;             // SAU CopyInto — nó GHI ĐÈ Copies; vòng lặp dưới tự nhân bản (N×N nếu để nguyên)
            // PDF đã cắt sẵn theo lẻ/chẵn rồi — shell không được lọc lại (và không lọc được).
            tmpCfg.Parity = PageParityFilter.All;
            var tmp = new PrintJob
            {
                FilePath = outPdf,
                FileName = Path.GetFileName(job.FilePath) + " (render)",
                Format = "PDF",
                Config = tmpCfg,
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
    /// chất lượng rasterize, ép chiều ngang, khổ giấy khác A4, chế độ B&W. Còn lại ("mặc định / theo máy") → shell thẳng.
    /// </summary>
    public static bool NeedsBrowserRender(PrintJob job)
    {
        if (job?.Config is null) return false;
        var cfg = job.Config;

        // B&W: đường shell chỉ đặt được dmColor của DEVMODE — driver bỏ qua dmColor là in RA MÀU.
        // Ảnh ở cấu hình mặc định (A4/dọc/theo file) không có option nào khác kích hoạt render, nên
        // thiếu dòng này thì nhánh grayscale bên dưới KHÔNG BAO GIỜ chạy với luồng in ảnh thường.
        // Đổi lại: tốn thêm 1 lần render browser (chậm hơn) để bảo đảm xám không phụ thuộc driver.
        if (cfg.ColorMode == PrintColorMode.Grayscale) return true;

        // Cắt trang hoặc lọc lẻ/chẵn → cần render. Dùng bản CHECKED: range SAI ("1-20" trên file TXT
        // 10 trang) cũng trả true — CỐ Ý đẩy sang đường render, nơi ResolveSelectedPagesChecked sinh
        // PrintError cho user thấy. Trả false ở đây = fast-path shell printto, mà shell không có khái
        // niệm page range → IN CẢ FILE im lặng (đúng cái lỗi đang phải bịt).
        var sel = CdpPrintParams.ResolveSelectedPagesChecked(job);
        if (!sel.IsSuccess || sel.Value is { Length: > 0 }) return true;

        // Khổ giấy "theo tài liệu" → cần render để dùng đúng khổ gốc từng trang
        if (cfg.PaperSize == PaperCatalog.AsDocument) return true;

        // Scale khác mặc định
        if (cfg.ScaleMode is not (PrintScaleMode.AsDocument or PrintScaleMode.Original))
            return true;

        // Chất lượng khác driver → cần rasterize đúng DPI
        if (cfg.Quality != PrintQuality.AsPrinter)
            return true;

        // 2 mặt: Simplex/LongEdge/ShortEdge → cần render (GDI ép đúng 1/2 mặt qua ảnh).
        // Shell printto in theo driver — user chọn 1 mặt vẫn ra 2 mặt nếu driver đang 2 mặt.
        if (cfg.DuplexMode != PrintDuplexMode.AsPrinter)
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

    /// <summary>Cấu hình có option mà shell printto KHÔNG làm được → rớt về shell sẽ in sai.
    /// Tách khỏi NeedsBrowserRender: hàm đó còn trả true cho "khổ theo tài liệu" (mặc định của app),
    /// mà shell in đúng khổ driver cũng chỉ là giảm chất lượng — không đáng chặn job.</summary>
    private static bool RequestsRenderOnlyOptions(PrintJob job)
    {
        if (job?.Config is null) return false;
        var cfg = job.Config;

        // Lọc lẻ/chẵn: shell printto không lọc được (chỉ cắt được qua danh sách trang đã resolve)
        if (cfg.Parity != PageParityFilter.All) return true;

        // Page range cụ thể → phải cắt trang, shell printto in cả file. Bản CHECKED: range SAI cũng
        // trả true — CỐ Ý đẩy sang đường render để user thấy PrintError thay vì in cả file im lặng.
        var sel = CdpPrintParams.ResolveSelectedPagesChecked(job);
        if (!sel.IsSuccess || sel.Value is { Length: > 0 }) return true;

        // Scale khác mặc định
        if (cfg.ScaleMode is not (PrintScaleMode.AsDocument or PrintScaleMode.Original))
            return true;

        // Chất lượng khác driver → cần rasterize đúng DPI
        if (cfg.Quality != PrintQuality.AsPrinter)
            return true;

        // 2 mặt: driver in theo cài đặt riêng — shell không ép được 1/2 mặt
        if (cfg.DuplexMode != PrintDuplexMode.AsPrinter)
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

    /// <summary>Lỗi chọn trang (range sai / lọc lẻ-chẵn hết trang) → Fail nguyên lỗi + log đường đi.
    /// Trước đây các nhánh này in CẢ file rồi báo Done.</summary>
    private static Result<bool> FailSelection(PrintJob job, PrintError error)
    {
        BrowserLog($"{job.FileName}: range='{job.Config.PageRange}' parity={job.Config.Parity} " +
                   $"→ KHÔNG in được: code={error.Code} msg='{error.Message}'; báo lỗi (không in cả file).");
        return Result<bool>.Fail(error);
    }

    /// <summary>Ghi log debug vào %TEMP%\printonator-office.log — engine browser đi đường nào, rớt vì sao.</summary>
    private static void BrowserLog(string msg)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "printonator-office.log"),
                $"{DateTimeOffset.Now:O} browser {msg}\n");
        }
        catch { }
    }
}