using System.Globalization;
using System.IO;
using Printonator.Core;
using Printonator.Core.Models;

namespace Printonator.Spool.Printing;

/// <summary>
/// Trang bìa in trước lô (CoverPage): dựng HTML 1 trang → headless browser (CDP printToPDF)
/// → PDF tạm → in qua SpoolPrintEngine tới máy đã chọn. Không nuốt lỗi — render/in hỏng
/// trả PrintError rõ ràng để queue dừng-đúng-chỗ (không đốt giấy phần sau lô).
/// </summary>
public static class CoverPageRenderer
{
    /// <summary>Dựng HTML trang bìa (flexbox căn giữa, không emoji — in sạch, dễ đọc).</summary>
    public static string BuildHtml(string batchName, int fileCount, int totalSheets, DateTime date, string? printerName)
    {
        var printer = string.IsNullOrWhiteSpace(printerName) ? "Máy in mặc định" : printerName;
        var dateText = date.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

        var sb = new System.Text.StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>");
        sb.Append("html,body{height:100%;margin:0;font-family:'Segoe UI',Arial,sans-serif;}");
        sb.Append("body{display:flex;align-items:center;justify-content:center;text-align:center;}");
        sb.Append("h1{font-size:44px;margin:0 0 20px;} p{font-size:20px;margin:8px 0;color:#333;}</style></head><body>");
        sb.Append("<div style=\"width:80%\">");
        sb.Append("<h1>").Append(Esc(batchName)).Append("</h1>");
        sb.Append("<p>").Append(fileCount).Append(" file · ").Append(totalSheets).Append(" trang</p>");
        sb.Append("<p>Máy in: ").Append(Esc(printer)).Append("</p>");
        sb.Append("<p>").Append(Esc(dateText)).Append("</p>");
        sb.Append("</div></body></html>");
        return sb.ToString();

        static string Esc(string? s) => System.Net.WebUtility.HtmlEncode(s) ?? "";
    }

    /// <summary>Render HTML trang bìa thành PDF base64 qua headless browser (dọn tempdir sau).</summary>
    public static async Task<(bool Ok, string? Base64Pdf)> RenderCoverAsync(
        string html, PrintConfig cfg, CancellationToken ct)
    {
        var browser = new BrowserLocator().ResolveBrowser();
        if (browser is not { } b)
            return (false, null);

        var tempDir = Path.Combine(Path.GetTempPath(), $"printonator-cover-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var htmlPath = Path.Combine(tempDir, "cover.html");
            await File.WriteAllTextAsync(htmlPath, html, ct);
            var (ok, base64, _) = await DevToolsPrintClient.PrintPdfAsync(
                b.Path,
                new Uri(htmlPath).AbsoluteUri,
                CdpPrintParams.Build(cfg, null),
                Path.Combine(tempDir, "profile"),
                ct);
            return (ok, base64);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// <summary>Ghi PDF trang bìa tạm + in qua SpoolPrintEngine tới máy đã chọn (dọn tempdir sau).</summary>
    public static async Task<Result<bool>> PrintCoverAsync(string base64Pdf, string printerName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(base64Pdf))
            return Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.SpoolerFailed,
                Category = PrintErrorCategory.App,
                Message = "Trang bìa rỗng — không in được.",
                Hint = "Kiểm tra lại cấu hình in trang bìa.",
            });

        var tempDir = Path.Combine(Path.GetTempPath(), $"printonator-cover-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var outPdf = Path.Combine(tempDir, "out.pdf");
            await File.WriteAllBytesAsync(outPdf, Convert.FromBase64String(base64Pdf), ct);

            var coverJob = new PrintJob
            {
                FilePath = outPdf,
                FileName = "Trang bìa",
                Format = "PDF",
                Config = new PrintConfig { PrinterName = printerName, Copies = 1 },
            };
            return await new SpoolPrintEngine().PrintAsync(coverJob, ct);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
