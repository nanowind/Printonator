using System.IO;
using Printonator.Core;
using Printonator.Core.Models;

namespace Printonator.Spool.Printing;

/// <summary>
/// Gộp toàn bộ lô thành 1 bản in (gộp file, MergeIntoOneFile): mỗi file PDF → rasterize từng trang
/// (Windows.Data.Pdf) → HTML &lt;img&gt;; ảnh → data-URI PNG trong &lt;img&gt;; TXT/CSV → &lt;pre&gt;
/// escaped; định dạng khác → bỏ qua. Dựng HTML → headless browser printToPDF → PDF tạm → in 1 job.
/// KHÔNG nhận job đơn (CanHandle=false) — chỉ dùng qua MergeAndPrintAsync.
/// </summary>
public sealed class MergePrintEngine : IPrintEngine
{
    public bool CanHandle(string format) => false;

    /// <summary>Chỉ dùng qua MergeAndPrintAsync (CanHandle=false nên queue không đẩy job đơn vào).</summary>
    public Task<Result<bool>> PrintAsync(PrintJob job, CancellationToken ct)
        => MergeAndPrintAsync(job is null ? Array.Empty<PrintJob>() : new[] { job }, ct);

    /// <summary>Gộp lô job (PDF/ảnh/TXT/CSV) thành 1 bản in. Không file nào in được → Fail.</summary>
    public async Task<Result<bool>> MergeAndPrintAsync(IReadOnlyList<PrintJob> jobs, CancellationToken ct)
    {
        if (jobs is null || jobs.Count == 0)
            return FailCoverEmpty("Không có file nào để gộp — bỏ qua.", "Chọn ít nhất 1 file PDF, ảnh, TXT hoặc CSV.");

        var printer = jobs.FirstOrDefault()?.Config.PrinterName;
        if (string.IsNullOrWhiteSpace(printer))
            return FailCoverEmpty("Chưa chọn máy in để gộp.", "Chọn máy in ở thanh công cụ rồi in lại.");

        var tempDir = Path.Combine(Path.GetTempPath(), $"printonator-merge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var html = await BuildMergeHtmlAsync(jobs, ct);
            if (string.IsNullOrWhiteSpace(html))
                return Result<bool>.Fail(new PrintError
                {
                    Code = ErrorCodes.UnsupportedFormat,
                    Category = PrintErrorCategory.Config,
                    Message = "Không có file PDF/ảnh/TXT/CSV nào gộp được trong lô.",
                    Hint = "Gộp file chỉ hỗ trợ PDF, ảnh, TXT và CSV. Bỏ những file khác hoặc tắt gộp.",
                });

            var browser = new BrowserLocator().ResolveBrowser();
            if (browser is not { } b)
                return Result<bool>.Fail(new PrintError
                {
                    Code = ErrorCodes.EngineNotFound,
                    Category = PrintErrorCategory.App,
                    Message = "Không tìm thấy Edge/Chrome để gộp file.",
                    Hint = "Máy cần có Microsoft Edge hoặc Google Chrome (mặc định Windows 10/11 có sẵn).",
                });

            var htmlPath = Path.Combine(tempDir, "merge.html");
            await File.WriteAllTextAsync(htmlPath, html, ct);

            var (ok, base64, err) = await DevToolsPrintClient.PrintPdfAsync(
                b.Path,
                new Uri(htmlPath).AbsoluteUri,
                CdpPrintParams.Build(jobs.First().Config, null),
                Path.Combine(tempDir, "profile"),
                ct);

            if (!ok || string.IsNullOrEmpty(base64))
                return Result<bool>.Fail(new PrintError
                {
                    Code = ErrorCodes.SpoolerFailed,
                    Category = PrintErrorCategory.App,
                    Message = $"Không dựng được PDF gộp: {err ?? "lỗi không rõ"}",
                    Hint = "Kiểm tra browser có chạy headless được không, rồi in lại.",
                    Detail = err,
                });

            var outPdf = Path.Combine(tempDir, "out.pdf");
            await File.WriteAllBytesAsync(outPdf, Convert.FromBase64String(base64), ct);

            var merged = new PrintJob
            {
                FilePath = outPdf,
                FileName = "Gộp cả lô",
                Format = "PDF",
                Config = new PrintConfig { PrinterName = printer, Copies = Math.Max(jobs.First().Config.Copies, 1) },
            };
            return await new SpoolPrintEngine().PrintAsync(merged, ct);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// <summary>Dựng HTML gộp: PDF → ảnh từng trang (page-break), ảnh → data-URI, TXT/CSV → &lt;pre&gt; escaped.</summary>
    internal static async Task<string> BuildMergeHtmlAsync(IReadOnlyList<PrintJob> jobs, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body style=\"margin:0\">");

        foreach (var job in jobs)
        {
            ct.ThrowIfCancellationRequested();

            var fmt = (job.Format ?? "").ToUpperInvariant();
            if (!File.Exists(job.FilePath)) continue;

            if (fmt == "PDF")
            {
                var rendered = await WindowsPdfRasterizer.RenderPagesAsync(job.FilePath, AllPagesOf(job), ct);
                if (rendered.IsSuccess && rendered.Value is { Count: > 0 } imgs)
                {
                    for (var i = 0; i < imgs.Count; i++)
                    {
                        sb.Append(WindowsPdfRasterizer.BuildHtml([imgs[i]]));
                        sb.Append("<div style=\"page-break-after:always\"></div>");
                    }
                }
            }
            else if (fmt is "PNG" or "JPG" or "JPEG" or "BMP" or "GIF" or "TIF" or "TIFF" or "WEBP" or "ICO")
            {
                var bytes = await File.ReadAllBytesAsync(job.FilePath, ct);
                var data = Convert.ToBase64String(bytes);
                sb.Append("<div style=\"page-break-after:always\">")
                  .Append("<img style=\"width:100%;height:100%\" src=\"data:image/png;base64,")
                  .Append(data).Append("\"></div>");
            }
            else if (fmt is "TXT" or "CSV")
            {
                var text = await File.ReadAllTextAsync(job.FilePath, ct);
                sb.Append("<div style=\"page-break-after:always\">")
                  .Append("<pre style=\"white-space:pre-wrap;padding:20px;\">")
                  .Append(System.Net.WebUtility.HtmlEncode(text))
                  .Append("</pre></div>");
            }
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static IReadOnlyList<int> AllPagesOf(PrintJob job)
        => job.PageCount > 0 ? Enumerable.Range(1, job.PageCount).ToArray() : [1];

    private static Result<bool> FailCoverEmpty(string msg, string hint)
        => Result<bool>.Fail(new PrintError
        {
            Code = ErrorCodes.NoFilesSelected,
            Category = PrintErrorCategory.Config,
            Message = msg,
            Hint = hint,
        });
}