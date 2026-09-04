using System.IO;
using Printonator.Core;
using Printonator.Core.IO;
using Printonator.Core.Models;
using Printonator.Core.Printing;

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

        using var temp = TempDir.Create("printonator-merge");
        var tempDir = temp.FullPath;
        {
            var html = await BuildMergeHtmlAsync(jobs, ct);
            if (string.IsNullOrWhiteSpace(html))
                return Result<bool>.Fail(PrintErrorFactory.UnsupportedFormat("lô không có file hợp lệ"));

            return await BrowserPrintPipeline.RenderAndSpoolAsync(
                html,
                CdpPrintParams.Build(jobs.First().Config, null),
                jobs.First(),
                " (gộp)",
                tempDir,
                ct);
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