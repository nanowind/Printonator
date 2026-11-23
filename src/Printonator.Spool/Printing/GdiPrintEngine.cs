using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.IO;
using System.Printing;
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
    // Ảnh vào thẳng GDI: máy nào không có verb shell\printto cho .jpg (UserChoice = PhotoViewer, chỉ có
    // "open") thì đường cũ render browser → shell printto ném "No application is associated" → job chết.
    // GDI không cần handler nào — PrintDocument đi thẳng spooler.
    // WEBP bị LOẠI: GDI+ (System.Drawing) không có codec WebP (đo 2026-11-23: `new Bitmap` ném
    // ArgumentException). Để trong danh sách thì mọi file .webp mất một lượt decode hỏng rồi mới rớt
    // browser — đi thẳng BrowserPrintEngine (Chrome/Edge giải mã được) là đúng đường.
    private static readonly string[] SupportedFormats =
        ["PDF", .. FileFormatRegistry.ImageFormats.Where(f => f != "WEBP")];

    /// <summary>Đổi ảnh sang XÁM ngay lúc VẼ (DrawImage + ImageAttributes) — không cấp bitmap mới
    /// mỗi trang (WindowsPdfRasterizer giữ tới PrintGuard.MaxPagesPerBatch ảnh trong RAM).
    /// Trọng số luminance Rec.601 0.299/0.587/0.114 cho cả 3 kênh ra → R=G=B; alpha giữ nguyên.
    /// Đây là CHẮC CHẮN xám, không phụ thuộc driver có tôn trọng dmColor hay không.</summary>
    /// <remarks>
    /// KHÔNG dùng chung 1 instance toàn process: ImageAttributes (GDI+) KHÔNG thread-safe — 2 thread
    /// cùng vẽ → "Object is currently in use elsewhere", và có ca ra TRANG TRẮNG không exception.
    /// Đường in chạy Task.Run + WaitAsync (bỏ rơi thread khi timeout) nên race là CÓ THẬT.
    /// Mỗi phạm vi dùng tự tạo instance và Dispose: <see cref="PrintImagesToPrinter"/> (cả batch),
    /// <see cref="ToGrayscalePng"/> (1 trang).
    /// </remarks>
    internal static ImageAttributes CreateGrayscaleAttributes()
    {
        var luma = new[] { 0.299f, 0.587f, 0.114f };
        // GDI+ nhan pixel la VECTOR HANG: [R G B A 1] x M -> R' = R*M[0][0] + G*M[1][0] + B*M[2][0].
        // Vi vay trong so luminance phai nam o COT (moi cot hang so), khong phai moi hang.
        // Xep sai (moi hang = luma) -> R' = .299R, G' = .587R, B' = .114R -> anh van co mau, khong xam.
        var matrix = new ColorMatrix(new float[][]
        {
            new[] { luma[0], luma[0], luma[0], 0f, 0f }, // R' = .299R + .587G + .114B
            new[] { luma[1], luma[1], luma[1], 0f, 0f }, // G' = cung luminance
            new[] { luma[2], luma[2], luma[2], 0f, 0f }, // B' = cung luminance
            new[] { 0f, 0f, 0f, 1f, 0f },                // alpha giu nguyen
            new[] { 0f, 0f, 0f, 0f, 1f },
        });
        var attrs = new ImageAttributes();
        attrs.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
        return attrs;
    }

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

        // Ảnh (trừ máy ảo ở trên): đọc thẳng file → MỘT trang GDI, KHÔNG qua shell printto.
        // Không phụ thuộc app nào đăng ký verb printto cho .jpg/.png — đó là lỗi thật 2026-11-23
        // (PhotoViewer chỉ có "open" → Process.Start(verb=printto) ném → SPOOLER_FAILED).
        if (!job.Format.Equals("PDF", StringComparison.OrdinalIgnoreCase))
            return await PrintSingleImageAsync(job, printer, ct);

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

        // Dùng bản CHECKED: Ok(null) = "All + không lọc lẻ/chẵn" (in TẤT CẢ trang — GDI cần danh sách
        // đầy đủ vì không có pageRanges CDP); Fail = range sai/lọc hết sạch → DỪNG, KHÔNG rớt browserInner
        // (rớt xuống đó là in cả file im lặng: range "1-20" trên file 10 trang vẫn ra đủ 10 trang, Done).
        int[]? pages;
        try
        {
            var sel = CdpPrintParams.ResolveSelectedPagesChecked(job);
            if (!sel.IsSuccess)
            {
                GdiLog($"GdiPrintEngine: range '{job.Config.PageRange}' không in được → code={sel.Error!.Code} msg='{sel.Error.Message}'");
                return Result<bool>.Fail(sel.Error!);
            }
            pages = sel.Value;
        }
        catch (Exception ex)
        {
            // Bộ resolve CHECKED ném lỗi = KHÔNG biết chọn trang nào → rơi về "in hết" là đúng kiểu in
            // sai im lặng mà bản checked vừa chặn. Dừng job với lỗi thật thay vì null.
            GdiLog($"GdiPrintEngine: ResolveSelectedPagesChecked ném lỗi range='{job.Config.PageRange}' → dừng: {ex}");
            return Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.EngineFailed,
                Category = PrintErrorCategory.App,
                Message = $"Không xác định được trang cần in của \"{job.FileName}\" do lỗi khi đọc cài đặt trang.",
                Hint = "Kiểm tra lại ô \"Trang in\" (ví dụ 1-3,5) rồi in lại — hoặc để trống để in toàn bộ tài liệu.",
                Detail = ex.Message,
            });
        }

        // Ok(null) mà biết số trang → in hết (chỉ còn đúng trường hợp All + không lọc lẻ/chẵn).
        pages ??= job.PageCount > 0 ? Enumerable.Range(1, job.PageCount).ToArray() : null;

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
                Hint = "Kiểm tra hàng đợi máy in trước — nếu job đã in rồi thì đừng in lại; còn treo/kẹt giấy thì xử lý rồi in lại.",
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

        // Trang cần in (range/parity) — bản CHECKED: Ok(null) = in hết (chưa biết số trang hoặc
        // All + không lẻ/chẵn); Fail = range sai → báo lỗi, KHÔNG xuất cả file.
        int[]? pages;
        try
        {
            var sel = CdpPrintParams.ResolveSelectedPagesChecked(job);
            if (!sel.IsSuccess) return Result<bool>.Fail(sel.Error!);
            pages = sel.Value;
        }
        catch (Exception ex)
        {
            // Như PrintAsync: lỗi trong resolve checked KHÔNG được rơi về "xuất cả file".
            GdiLog($"GdiPrintEngine: ResolveSelectedPagesChecked ném lỗi range='{job.Config.PageRange}' (export PDF) → dừng: {ex}");
            return Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.EngineFailed,
                Category = PrintErrorCategory.App,
                Message = $"Không xác định được trang cần xuất của \"{job.FileName}\" do lỗi khi đọc cài đặt trang.",
                Hint = "Kiểm tra lại ô \"Trang in\" (ví dụ 1-3,5) rồi xuất lại — hoặc để trống để xuất toàn bộ tài liệu.",
                Detail = ex.Message,
            });
        }
        pages ??= job.PageCount > 0 ? Enumerable.Range(1, job.PageCount).ToArray() : null;

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

        // Đen trắng: ép xám NGAY Ở ĐÂY — PdfImageWriter chỉ encode PNG→JPEG, không biết ColorMode.
        // Thứ tự trang / số trang / WidthDip-HeightDip giữ nguyên, chỉ bytes PNG đổi.
        // AsPrinter / AsDocument / Color → list đi thẳng (byte-identical).
        // Đổi TẠI CHỖ khi được (rasterizer trả List): bytes màu cũ được GC thu hồi dần → peak RAM
        // không nhân đôi (cả batch ảnh đã nằm trong RAM từ bước render).
        IReadOnlyList<RenderedPdfPage> pageList = list;
        if (job.Config.ColorMode == PrintColorMode.Grayscale)
        {
            string? grayErr = null;
            var grayPage = 0;
            if (list is List<RenderedPdfPage> mutable)
            {
                for (var i = 0; i < mutable.Count; i++)
                {
                    if (ToGrayscalePng(mutable[i].Png, out var err) is { } g)
                        mutable[i] = mutable[i] with { Png = g };
                    else { grayErr = err; grayPage = i + 1; break; }
                }
            }
            else
            {
                var gray = new List<RenderedPdfPage>(list.Count);
                for (var i = 0; i < list.Count; i++)
                {
                    if (ToGrayscalePng(list[i].Png, out var err) is { } g)
                        gray.Add(list[i] with { Png = g });
                    else { grayErr = err; grayPage = i + 1; break; }
                }
                pageList = gray;
            }

            // Chuyển xám hỏng KHÔNG được rơi về bytes màu: user chọn "Đen trắng" mà nhận PDF màu
            // là in sai im lặng → DỪNG xuất bản, báo lỗi đầy đủ (code + message + hint) cho job/UI/MCP.
            if (grayErr is not null)
            {
                GdiLog($"GdiPrintEngine: chuyển xám trang {grayPage} '{job.FileName}' thất bại → dừng xuất PDF: {grayErr}");
                return Result<bool>.Fail(new PrintError
                {
                    Code = ErrorCodes.EngineFailed,
                    Category = PrintErrorCategory.App,
                    Message = $"Không xuất được PDF cho \"{job.FileName}\": chuyển trang {grayPage} sang đen trắng thất bại nên đã dừng, để không tạo ra file còn màu.",
                    Hint = "In thẳng ra máy in giấy thay vì máy in ảo PDF, hoặc bỏ chọn \"Đen trắng\" rồi xuất lại.",
                    Detail = $"Trang {grayPage}: {grayErr}",
                });
            }
        }

        try
        {
            PdfImageWriter.Write(outPdf, pageList);
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

    /// <summary>Tên khay thân thiện (PrinterService.FriendlyTrayName) → InputBin của driver.</summary>
    private static InputBin? TrayBinFromFriendlyName(string friendly) => friendly switch
    {
        _ when friendly.Contains("Tự chọn", StringComparison.OrdinalIgnoreCase) => InputBin.AutoSelect,
        _ when friendly.Contains("Nạp tự động", StringComparison.OrdinalIgnoreCase) => InputBin.AutoSheetFeeder,
        _ when friendly.Contains("cassette", StringComparison.OrdinalIgnoreCase) => InputBin.Cassette,
        _ when friendly.Contains("Tractor", StringComparison.OrdinalIgnoreCase) => InputBin.Tractor,
        _ when friendly.Contains("Nạp tay", StringComparison.OrdinalIgnoreCase) => InputBin.Manual,
        _ => null,
    };

    /// <summary>Từ khoá khớp tên khay trong driver (PaperSource.SourceName) theo InputBin.</summary>
    private static string TrayMatchKey(InputBin bin) => bin switch
    {
        InputBin.AutoSelect => "Auto",
        InputBin.AutoSheetFeeder => "Auto",
        InputBin.Cassette => "Cassette",
        InputBin.Tractor => "Tractor",
        InputBin.Manual => "Manual",
        _ => "",
    };

    /// <summary>Hệ số phóng/thu theo ScaleMode config (GDI trước đây bỏ qua option này hoàn toàn).
    /// Zoom → ScalePercent/100; các mode khác → 1.0 (ảnh vốn đã fit khung, khác biệt thật chỉ có ở Zoom).</summary>
    private static double ZoomFactor(PrintConfig config)
    {
        if (config.ScaleMode != PrintScaleMode.Zoom) return 1.0;
        var pct = Math.Clamp(config.ScalePercent, 10, 400);
        return pct / 100.0;
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
            // Ép xám lúc vẽ — instance RIÊNG cho lần in này, Dispose khi thoát (kể cả lỗi): GDI+
            // ImageAttributes KHÔNG thread-safe, còn thread in bị bỏ rơi lúc timeout/cancel vẫn đang vẽ.
            // ponytail: chỉ phủ đường GDI/raster này (PDF và PDF→PDF export); Office/LibreOffice/shell
            // đã ép qua DEVMODE lease riêng.
            using var gray = job.Config.ColorMode == PrintColorMode.Grayscale ? CreateGrayscaleAttributes() : null;

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
            pd.PrinterSettings.Copies = 1; // tự bơm N bản (ByDocuments/ByPages) bên dưới — tránh driver nhân đôi

            // ===== 2 mặt (duplex) =====
            if (job.Config.DuplexMode == PrintDuplexMode.LongEdge)
                pd.PrinterSettings.Duplex = Duplex.Vertical;
            else if (job.Config.DuplexMode == PrintDuplexMode.ShortEdge)
                pd.PrinterSettings.Duplex = Duplex.Horizontal;
            else if (job.Config.DuplexMode == PrintDuplexMode.Simplex)
                pd.PrinterSettings.Duplex = Duplex.Simplex;
            // AsPrinter → giữ mặc định driver (không set)

            // ===== Khay giấy (PaperSource) — trước đây option này CHẾT với GDI =====
            // Tên khay lưu trong config là tên thân thiện (PrinterService.FriendlyTrayName) → map ngược
            // sang InputBin của driver. Khay không khớp → giữ auto (không fail: tên khay khác nhau giữa
            // máy in, fail ở đây sẽ chặn oan job vốn in được bằng khay mặc định).
            if (!string.IsNullOrWhiteSpace(job.Config.PaperSource))
            {
                var trayBin = TrayBinFromFriendlyName(job.Config.PaperSource);
                if (trayBin is { } bin)
                {
                    var src = pd.PrinterSettings.PaperSources
                        .Cast<PaperSource>().FirstOrDefault(s => s.SourceName.Contains(TrayMatchKey(bin), StringComparison.OrdinalIgnoreCase));
                    if (src is not null) pd.DefaultPageSettings.PaperSource = src;
                    else GdiLog($"GdiPrintEngine: khay '{job.Config.PaperSource}' không có trên '{printer}' → dùng khay mặc định");
                }
            }

            // ===== Màu (ép đen trắng / ép màu) — driver quyết khi AsPrinter/AsDocument =====
            // Đảm bảo XÁM thật = ép màu ở DRAW TIME (gray bên dưới). Cờ driver dưới đây chỉ là
            // HỖ TRỢ: .NET chỉ chuyển nó thành dmColor khi driver khai DM_COLOR, nhiều driver bỏ qua.
            if (job.Config.ColorMode == PrintColorMode.Grayscale)
                pd.PrinterSettings.DefaultPageSettings.Color = false;
            else if (job.Config.ColorMode == PrintColorMode.Color)
                pd.PrinterSettings.DefaultPageSettings.Color = true;

            // Tự bơm N bản (hết mảng trang lại lặp) — KHÔNG set PrinterSettings.Copies
            // (driver tự nhân Copies → in N×N).
            var copies = Math.Max(job.Config.Copies, 1);
            // Collation: ByPages = gom theo TRANG (1,1,1 rồi 2,2,2...) — ByDocuments/AsPrinter = gom từng bộ (1,2,3).
            // N-up: 1 tờ chứa nUp trang KHÁC nhau → "theo trang" vô nghĩa, giữ theo tài liệu.
            var byPages = job.Config.Collation == PrintCollation.ByPages && nUp == 1;
            var totalPages = images.Count * copies;
            var pumped = 0;
            var anyPageFailed = false;

            // Grid N-up: 2→[2×1], 4→[2×2], 6→[3×2], 9→[3×3], 16→[4×4] — công thức dùng CHUNG với
            // preview (PaperCatalog.NUpGrid) để hai bên không lệch nhau như trước.
            var (nUpCols, nUpRows) = PaperCatalog.NUpGrid(nUp);

            // Ép xám ngay lúc vẽ (null = giữ nguyên màu) — `gray` tạo 1 lần cho cả file in này ở đầu method.
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
                    // ByPages: ảnh thứ i = trang (i / copies) — mọi bản của 1 trang in liền nhau.
                    // ByDocuments: ảnh thứ i = trang (i % images.Count) — hết bộ trang này mới lặp bộ sau.
                    var img = byPages
                        ? images[(imgIdx / copies) % images.Count]
                        : images[imgIdx % images.Count];
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
                                var dx = cell.X + (cell.Width - dw) / 2;
                                var dy = cell.Y + (cell.Height - dh) / 2;
                                if (gray is null)
                                {
                                    e.Graphics.DrawImage(bmp, dx, dy, dw, dh);
                                }
                                else
                                {
                                    // Cùng toạ độ float như nhánh trên (RectF để không làm tròn số lẻ).
                                    e.Graphics.DrawImage(bmp,
                                        new[] { new PointF(dx, dy), new PointF(dx + dw, dy), new PointF(dx, dy + dh) },
                                        new RectangleF(0f, 0f, bmp.Width, bmp.Height), GraphicsUnit.Pixel, gray);
                                }
                            }
                            else
                            {
                                // Vẽ ảnh fit trong vùng printable (đã trừ safe margin). Graphics origin
                                // đã là góc printable — KHÔNG cộng HardMargin (sửa bug lệch góc dưới).
                                var scale = Math.Min(areaW / (double)bmp.Width, areaH / (double)bmp.Height)
                                            * ZoomFactor(job.Config);
                                var dw = (float)(bmp.Width * scale);
                                var dh = (float)(bmp.Height * scale);
                                var dx = safe + (areaW - dw) / 2;
                                var dy = safe + (areaH - dh) / 2;
                                if (gray is null)
                                {
                                    e.Graphics.DrawImage(bmp, dx, dy, dw, dh);
                                }
                                else
                                {
                                    e.Graphics.DrawImage(bmp,
                                        new[] { new PointF(dx, dy), new PointF(dx + dw, dy), new PointF(dx, dy + dh) },
                                        new RectangleF(0f, 0f, bmp.Width, bmp.Height), GraphicsUnit.Pixel, gray);
                                }
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
        else if (Near(pw, ph, 182, 257)) kind = PaperKind.B5;   // B5 JIS — khớp PaperCatalog

        return (kind, (int)Math.Round(pw * 100), (int)Math.Round(ph * 100));
    }

    /// <summary>
    /// In MỘT file ảnh qua GDI (không shell): ảnh là 1 trang nên page-range/parity/copies/N-up
    /// được QUY VỀ "in 1 trang" hoặc báo lỗi RÕ — không im lặng in sai.
    /// </summary>
    private async Task<Result<bool>> PrintSingleImageAsync(PrintJob job, string printer, CancellationToken ct)
    {
        // Trước đây ảnh rớt xuống shell printto; giờ GDI nhận. Range/parity của preset cũ (đặt cho
        // PDF nhiều trang) áp lên ảnh 1 trang: "2" hay "trang chẵn" không có gì để in → phải báo lỗi,
        // KHÔNG in trang 1 trong im lặng (đúng hợp đồng ResolveSelectedPagesChecked).
        job.PageCount = 1;
        var sel = CdpPrintParams.ResolveSelectedPagesChecked(job);
        if (!sel.IsSuccess)
        {
            GdiLog($"GdiPrintEngine: ảnh '{job.FileName}' range='{job.Config.PageRange}' parity={job.Config.Parity} → {sel.Error!.Code}");
            return Result<bool>.Fail(sel.Error!);
        }
        if (sel.Value is null)
        {
            // Ảnh luôn 1 trang nên resolver chỉ trả Fail hoặc [1] — không có nhánh "in tất cả".
            GdiLog($"GdiPrintEngine: ảnh '{job.FileName}' resolver trả null (bất thường) → coi như 1 trang");
        }

        var imgPage = LoadImagePage(job, out var imgErr);
        if (imgPage is null)
        {
            // GDI+ không có codec cho một số định dạng (WEBP) — KHÔNG chặn: rớt về browser
            // (Chrome/Edge giải mã được). Chặn ở đây = format trước đây in được nay hỏng.
            GdiLog($"GdiPrintEngine: không giải mã được ảnh '{job.FileName}' ({imgErr}) → rớt browser render");
            var fallback = await _browserInner.PrintAsync(job, ct);
            GdiLog($"GdiPrintEngine: browserInner trả {fallback.IsSuccess} code={fallback.Error?.Code}");
            return fallback;
        }

        // N-up áp cho ảnh 1 trang = ảnh co vào 1 ô của lưới, còn lại trắng — vô nghĩa và lệch preview
        // (preview khai PagesPerSheetHonoured=false cho ảnh). In đúng 1 tờ đầy đủ.
        var nUpBackup = job.Config.PagesPerSheet;
        job.Config.PagesPerSheet = 1;
        try
        {
            // Ảnh NGANG (chụp máy ảnh/điện thoại) trên tờ dọc: auto-rotate của đường PDF xoay 90° —
            // đúng cho trang tài liệu nằm ngang, SAI cho ảnh (người xem phải nghiêng đầu).
            // Đánh dấu trang là "dọc" để auto-rotate không chạy; GDI vẫn vẽ nguyên bitmap ĐỘ PHÂN GIẢI GỐC,
            // thu vừa khổ + canh giữa (nhánh fit ở PrintImagesToPrinter). KHÔNG rasterize lại (padding
            // ảnh sẽ hạ độ phân giải: canvas bị transpose → rộng = chiều cao ảnh).
            if (job.Config.Orientation != PrintOrientation.Landscape && imgPage.WidthDip > imgPage.HeightDip)
                imgPage = imgPage with { WidthDip = imgPage.HeightDip, HeightDip = imgPage.WidthDip };
            GdiLog($"GdiPrintEngine: ảnh '{job.FileName}' → GDI trực tiếp ({imgPage.WidthDip:0}x{imgPage.HeightDip:0} px) '{printer}'");

            var printTask = Task.Run(() => PrintImagesToPrinter(job, printer, [imgPage], ct), ct);
            Result<bool> r;
            try
            {
                r = await printTask.WaitAsync(TimeSpan.FromSeconds(GdiPrintTimeoutSeconds), ct);
            }
            catch (TimeoutException)
            {
                GdiLog($"GdiPrintEngine: IN ẢNH QUÁ LÂU '{job.FileName}' → '{printer}' (>{GdiPrintTimeoutSeconds}s) → EngineTimeout");
                return Result<bool>.Fail(new PrintError
                {
                    Code = ErrorCodes.EngineTimeout,
                    Category = PrintErrorCategory.System,
                    Message = $"In ảnh {job.FileName} tới \"{printer}\" quá lâu ({GdiPrintTimeoutSeconds}s) — đã hủy.",
                    Hint = "Kiểm tra hàng đợi máy in trước — nếu job đã in rồi thì đừng in lại; còn treo/kẹt giấy thì xử lý rồi in lại.",
                });
            }

            if (!r.IsSuccess)
                GdiLog($"GdiPrintEngine: IN ẢNH LỖI '{job.FileName}' → '{printer}': code={r.Error!.Code} msg='{r.Error.Message}'");
            return r;
        }
        finally
        {
            job.Config.PagesPerSheet = nUpBackup;
        }
    }

    /// <summary>PNG bytes → Bitmap (dispose sau khi vẽ). null nếu ảnh hỏng.</summary>
    private static Bitmap? LoadImage(byte[] png)
    {
        try { using var ms = new MemoryStream(png); return new Bitmap(ms); }
        catch { return null; }
    }

    /// <summary>File ảnh → một trang GDI (giữ nguyên bytes, <see cref="LoadImage"/> giải mã lúc vẽ).
    /// WidthDip/HeightDip chỉ dùng để phát hiện trang NGANG (tỷ lệ pixel == tỷ lệ dip) nên dùng
    /// thẳng kích thước pixel; DPI thật của ảnh không đổi cách vẽ (ảnh luôn được fit vào vùng in).
    /// Không đọc lại kích thước bằng cách decode bitmap (đường vẽ decode rồi) — đọc header: PNG 24 byte,
    /// còn lại để GDI+ probe. Decode hai lần tốn gấp đôi thời gian trên ảnh lớn.
    /// ponytail: TIFF nhiều khung chỉ in khung đầu — nâng cấp khi có user in TIFF nhiều trang.</summary>
    private static RenderedPdfPage? LoadImagePage(PrintJob job, out string? error)
    {
        error = null;
        try
        {
            var bytes = File.ReadAllBytes(job.FilePath);
            if (!TryReadImageSize(bytes, out var w, out var h))
            {
                // Định dạng không đọc được từ header → decode một lần để lấy kích thước; CHỈ ảnh lạ
                // mới chịu chi phí này, và thất bại ở đây = không in được ảnh (rớt browser).
                using var bmp = LoadImage(bytes);
                if (bmp is null)
                {
                    error = "Không giải mã được ảnh.";
                    return null;
                }
                w = bmp.Width;
                h = bmp.Height;
            }
            return new RenderedPdfPage(bytes, w, h);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Đọc kích thước ảnh từ header (không decode pixel): PNG + JPEG đủ cho 99% ảnh in.
    /// Định dạng khác → false (caller tự decode). Chỉ cần TỶ LỆ ngang/dọc đúng nên không đọc DPI.</summary>
    internal static bool TryReadImageSize(byte[] b, out int w, out int h)
    {
        w = h = 0;
        if (b.Length > 24
            && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)   // PNG: IHDR ngay sau sig
        {
            w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
            h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
            return w > 0 && h > 0;
        }
        if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)   // JPEG: quét marker tới SOFn
        {
            var i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF) { i++; continue; }
                var marker = b[i + 1];
                if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7
                    or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
                {
                    h = (b[i + 5] << 8) | b[i + 6];
                    w = (b[i + 7] << 8) | b[i + 8];
                    return w > 0 && h > 0;
                }
                if (marker is 0xD8 or 0xD9 or 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
                i += 2 + ((b[i + 2] << 8) | b[i + 3]);   // bỏ qua segment
            }
        }
        return false;
    }

    /// <summary>PNG → PNG XÁM (dùng cùng ma trận với đường in GDI — <see cref="CreateGrayscaleAttributes"/>).
    /// Lỗi bất kỳ → null + <paramref name="error"/> (lý do): caller DỪNG việc xuất PDF và báo lỗi cho job.
    /// Không bao giờ trả lại ảnh màu — user chọn "Đen trắng" mà nhận file màu là in sai im lặng.
    /// ponytail: đổi cả trang thành bitmap → chữ mất vector, PDF xuất ra nặng hơn.
    /// Nâng cấp khi: có user kêu PDF xám xuất ra mờ — lúc đó mới cần đường vector/không raster.</summary>
    internal static byte[]? ToGrayscalePng(byte[] png, out string? error)
    {
        error = null;
        try
        {
            using var src = LoadImage(png);
            if (src is null)
            {
                error = "không đọc được ảnh trang";
                return null;
            }
            using var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            // ImageAttributes RIÊNG cho lần gọi này (GDI+ không thread-safe — không dùng chung static).
            using var gray = CreateGrayscaleAttributes();
            using (var g = Graphics.FromImage(dst))
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height),
                    0, 0, src.Width, src.Height, GraphicsUnit.Pixel, gray);
            }
            using var ms = new MemoryStream();
            dst.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            GdiLog($"GdiPrintEngine: chuyển xám 1 trang PDF thất bại: {ex.Message}");
            error = ex.Message;
            return null;
        }
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
