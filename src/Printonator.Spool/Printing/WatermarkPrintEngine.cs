using System.Globalization;
using System.IO;
using Printonator.Core;
using Printonator.Core.Models;

namespace Printonator.Spool.Printing;

/// <summary>
/// Engine bọc (decorator) chèn DẤU MỜ (watermark): PDF → rasterize từng trang → HTML có overlay chữ
/// mờ → headless browser printToPDF → PDF tạm → in qua SpoolPrintEngine. Ảnh → HTML ảnh kèm overlay.
/// Không có WatermarkText → chuyển thẳng xuống engine trong (_inner). Render/in watermark lỗi →
/// rớt MỀM về in file gốc qua _inner (in được vẫn hơn lỗi, đúng tinh thần engine render).
/// </summary>
public sealed class WatermarkPrintEngine : IPrintEngine
{
    private static readonly string[] SupportedFormats =
        ["PDF", "PNG", "JPG", "JPEG", "BMP", "GIF", "TIF", "TIFF", "WEBP", "ICO"];

    private readonly IPrintEngine _inner;

    public WatermarkPrintEngine(IPrintEngine inner) => _inner = inner;

    public bool CanHandle(string format) => _inner.CanHandle(format);

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

        // Không có dấu mờ → in nguyên bản qua engine trong (không tốn render/browser).
        if (string.IsNullOrWhiteSpace(job.Config?.WatermarkText))
            return await _inner.PrintAsync(job, ct);

        if (string.IsNullOrEmpty(job.FilePath))
            return Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.FileNotFound,
                Category = PrintErrorCategory.System,
                Message = "File rỗng — không in được.",
                Hint = "Kiểm tra lại file cần in.",
            });

        if (!File.Exists(job.FilePath))
            return Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.FileNotFound,
                Category = PrintErrorCategory.System,
                Message = $"File không tồn tại: {job.FilePath}",
                Hint = "File bị xóa hoặc di chuyển — kiểm tra lại đường dẫn.",
            });

        var fmt = (job.Format ?? "").ToUpperInvariant();
        if (!SupportedFormats.Contains(fmt))
            return await _inner.PrintAsync(job, ct); // định dạng khác — trao cho engine trong

        var printer = job.Config.PrinterName;
        if (string.IsNullOrWhiteSpace(printer))
            return Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.PrinterNotFound,
                Category = PrintErrorCategory.Config,
                Message = "Chưa chọn máy in.",
                Hint = "Chọn máy in ở thanh công cụ rồi in lại.",
            });

        var tempDir = Path.Combine(Path.GetTempPath(), $"printonator-watermark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var html = await BuildWatermarkHtmlAsync(job, fmt, ct);
            if (string.IsNullOrEmpty(html))
                return await _inner.PrintAsync(job, ct);

            var browser = new BrowserLocator().ResolveBrowser();
            if (browser is not { } b)
                return Result<bool>.Fail(new PrintError
                {
                    Code = ErrorCodes.EngineNotFound,
                    Category = PrintErrorCategory.App,
                    Message = "Không tìm thấy Edge/Chrome để in dấu mờ.",
                    Hint = "Máy cần có Microsoft Edge hoặc Google Chrome (mặc định Windows 10/11 có sẵn).",
                });

            var htmlPath = Path.Combine(tempDir, "wm.html");
            await File.WriteAllTextAsync(htmlPath, html, ct);

            var (ok, base64, err) = await DevToolsPrintClient.PrintPdfAsync(
                b.Path,
                new Uri(htmlPath).AbsoluteUri,
                CdpPrintParams.Build(job.Config, null),
                Path.Combine(tempDir, "profile"),
                ct);

            if (!ok || string.IsNullOrEmpty(base64))
                return await _inner.PrintAsync(job, ct); // render fail → rớt mềm in file gốc

            var outPdf = Path.Combine(tempDir, "out.pdf");
            await File.WriteAllBytesAsync(outPdf, Convert.FromBase64String(base64), ct);

            var wmJob = new PrintJob
            {
                FilePath = outPdf,
                FileName = job.FileName + " (dấu mờ)",
                Format = "PDF",
                Config = new PrintConfig
                {
                    PrinterName = printer,
                    Copies = Math.Max(job.Config.Copies, 1),
                },
            };
            return await new SpoolPrintEngine().PrintAsync(wmJob, ct);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// <summary>Dựng HTML chèn dấu mờ: PDF → ảnh từng trang, ảnh → data-URI; kèm overlay chữ mờ.</summary>
    internal static async Task<string> BuildWatermarkHtmlAsync(PrintJob job, string fmt, CancellationToken ct)
    {
        var (top, left) = PositionFor(job.Config.WatermarkPosition);
        var opacity = Math.Clamp(job.Config.WatermarkOpacity, 0.05, 1.0);
        var text = System.Net.WebUtility.HtmlEncode(job.Config.WatermarkText!);

        // CSS overlay: top/left tính theo % + translate(-50%,-50%) — căn đúng tâm điểm vị trí chọn
        var overlay = $"<div style=\"position:absolute;top:{top};left:{left};transform:translate(-50%,-50%);" +
                      $"opacity:{opacity.ToString(CultureInfo.InvariantCulture)};font-size:48px;color:red;pointer-events:none\">" +
                      text + "</div>";

        var sb = new System.Text.StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head>" +
                  "<body style=\"margin:0;position:relative;\">");
        sb.Append(overlay);

        if (fmt == "PDF")
        {
            var pages = job.PageCount > 0 ? Enumerable.Range(1, job.PageCount).ToArray() : [1];
            var rendered = await WindowsPdfRasterizer.RenderPagesAsync(job.FilePath, pages, ct);
            if (!rendered.IsSuccess || rendered.Value is not { Count: > 0 } imgs)
                return ""; // không rasterize được → caller rớt mềm về _inner

            sb.Append(WindowsPdfRasterizer.BuildHtml(imgs));
        }
        else
        {
            var bytes = await File.ReadAllBytesAsync(job.FilePath, ct);
            var data = Convert.ToBase64String(bytes);
            sb.Append("<img style=\"width:100%;height:100%\" src=\"data:image/png;base64,").Append(data).Append("\">");
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }

    /// <summary>Tọa độ (top%, left%) cho từng vị trí watermark — center = giữa, góc = lệch 10%.</summary>
    internal static (string Top, string Left) PositionFor(string? position)
        => (position ?? "").Trim().ToLowerInvariant() switch
        {
            "top-left" => ("10%", "10%"),
            "top-right" => ("10%", "90%"),
            "bottom-left" => ("90%", "10%"),
            "bottom-right" => ("90%", "90%"),
            _ => ("50%", "50%"), // center / không biết → chính giữa
        };
}