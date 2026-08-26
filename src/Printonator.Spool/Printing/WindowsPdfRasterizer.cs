using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using Printonator.Core.Models;

namespace Printonator.Spool.Printing;

/// <summary>Một trang PDF đã render thành ảnh + khổ giấy gốc (DIPs, 1/96 inch).</summary>
public sealed record RenderedPdfPage(byte[] Png, double WidthDip, double HeightDip);

/// <summary>
/// Rasterize trang PDF bằng API CÓ SẴN trong Windows 10/11 (Windows.Data.Pdf — pdf renderer của
/// Windows, KHÔNG bundle lib, KHÔNG cần browser). Render CHỈ các trang được chọn → PNG (150dpi),
/// kèm khổ giấy gốc (DIPs) để dựng lại PDF đúng cỡ khi in. Là nền cho tính năng cắt trang PDF.
/// Không nuốt lỗi: file hỏng/khoá/được mã hoá → Result.Fail PrintError rõ ràng.
/// </summary>
public static class WindowsPdfRasterizer
{
    /// <summary>DPI render mặc định — đủ sắc cho in, cân bằng kích thước (150dpi ≈ chất lượng photo).</summary>
    public const double DefaultRenderDpi = 150;

    /// <summary>Render các trang (1-based) thành PNG với DPI cho sẵn. Lỗi → Result.Fail(PrintError).</summary>
    public static async Task<Result<IReadOnlyList<RenderedPdfPage>>> RenderPagesAsync(
        string filePath, IReadOnlyList<int> pages /* 1-based */, CancellationToken ct, int renderDpi = 150)
    {
        PdfDocument? doc = null;
        try
        {
            var storage = await StorageFile.GetFileFromPathAsync(filePath).AsTask(ct);
            doc = await PdfDocument.LoadFromFileAsync(storage).AsTask(ct);
            if (doc is null || doc.PageCount == 0)
                return Result<IReadOnlyList<RenderedPdfPage>>.Fail(FileCorruptError(filePath));

            var dpi = renderDpi <= 0 ? (int)DefaultRenderDpi : renderDpi;
            var imgs = new List<RenderedPdfPage>();
            foreach (var n in pages)
            {
                ct.ThrowIfCancellationRequested();
                if (n < 1 || n > (int)doc.PageCount) continue;

                var page = doc.GetPage((uint)(n - 1));
                try
                {
                    var size = page.Size; // DIPs (1/96 inch) — khổ giấy ảo của PDF ở 96dpi
                    uint destW = (uint)Math.Max(1, Math.Round(size.Width / 96.0 * dpi));
                    uint destH = (uint)Math.Max(1, Math.Round(size.Height / 96.0 * dpi));
                    var options = new PdfPageRenderOptions { DestinationWidth = destW, DestinationHeight = destH };

                    using var stream = new InMemoryRandomAccessStream();
                    await page.RenderToStreamAsync(stream, options).AsTask(ct);
                    stream.Seek(0);
                    var bytes = new byte[stream.Size];
                    var buffer = bytes.AsBuffer();
                    await stream.ReadAsync(buffer, (uint)bytes.Length, InputStreamOptions.None).AsTask(ct);

                    imgs.Add(new RenderedPdfPage(bytes, size.Width, size.Height));
                }
                finally
                {
                    page.Dispose();
                }
            }

            return imgs.Count == 0
                ? Result<IReadOnlyList<RenderedPdfPage>>.Fail(FileCorruptError(filePath))
                : Result<IReadOnlyList<RenderedPdfPage>>.Ok(imgs);
        }
        catch (OperationCanceledException)
        {
            return Result<IReadOnlyList<RenderedPdfPage>>.Fail(new PrintError
            {
                Code = ErrorCodes.EngineTimeout,
                Category = PrintErrorCategory.System,
                Message = "Đang render PDF bị hủy giữa chừng.",
                Hint = "Bấm in lại nếu cần.",
            });
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<RenderedPdfPage>>.Fail(new PrintError
            {
                Code = ErrorCodes.FileCorrupted,
                Category = PrintErrorCategory.App,
                Message = $"Không đọc được PDF để cắt trang: {Path.GetFileName(filePath)}.",
                Hint = "File PDF bị hỏng, bị mật khẩu, hoặc định dạng lạ. Thử mở trong Edge/Adobe xem được không.",
                Detail = ex.Message,
            });
        }
        finally
        {
            // PdfDocument/PdfPage là WinRT — GC quản lý, không cần Dispose rõ ràng
        }
    }

    /// <summary>Dựng HTML ảnh có page-break (in đúng số trang đã chọn) từ các trang đã render.</summary>
    public static string BuildHtml(IReadOnlyList<RenderedPdfPage> pages)
    {
        var sb = new System.Text.StringBuilder("<html><body style='margin:0;padding:0'>");
        for (var i = 0; i < pages.Count; i++)
        {
            var data = Convert.ToBase64String(pages[i].Png);
            var brk = i > 0 ? "page-break-after:always;" : "";
            sb.Append("<div style='").Append(brk)
              .Append("width:100%;height:100%'><img style='width:100%;height:100%;display:block' src='data:image/png;base64,")
              .Append(data).Append("'/></div>");
        }
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static PrintError FileCorruptError(string path) => new()
    {
        Code = ErrorCodes.FileCorrupted,
        Category = PrintErrorCategory.App,
        Message = $"PDF không có trang nào để cắt: {Path.GetFileName(path)}.",
        Hint = "Kiểm tra file còn đọc được.",
    };
}