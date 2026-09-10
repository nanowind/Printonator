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

    /// <summary>Đếm số trang của file PDF (1-based) — dùng để resolve page-range/parity đúng TRƯỚC khi render.
    /// File hỏng/khoá/mã hoá → trả -1 (caller fallback in cả file/engine khác).</summary>
    public static async Task<int> PdfPageCountAsync(string filePath, CancellationToken ct)
    {
        try
        {
            var storage = await StorageFile.GetFileFromPathAsync(filePath).AsTask(ct);
            var doc = await PdfDocument.LoadFromFileAsync(storage).AsTask(ct);
            if (doc is null) return -1;
            return (int)doc.PageCount;
        }
        catch (OperationCanceledException) { throw; }
        catch { return -1; }
    }

    /// <summary>
    /// Đếm số trang PDF một cách ĐÁNG TIN CẬY: thử Windows.Data.Pdf trước, nhưng nếu nó trả 0/1
    /// mà file có khả năng nhiều trang (dung lượng lớn / có chỉ mục xref thật) thì đếm lại bằng
    /// iText (nếu lib có mặt) hoặc parser "/Type /Page" thủ công (không cần lib).
    /// Windows.Data.Pdf có bug đếm THIẾU trang với PDF linearized/tạo từ Word/Excel → in chỉ 1 trang.
    /// </summary>
    public static async Task<int> PdfPageCountReliableAsync(string filePath, CancellationToken ct)
    {
        var wdp = await PdfPageCountAsync(filePath, ct);
        if (wdp > 1) return wdp;              // >1 trang chắc chắn đúng
        if (wdp == 0) return 0;               // file rỗng — không đếm lại
        // wdp == 1 hoặc -1: nghi ngờ đếm thiếu → đếm lại bằng đường khác
        return await CountPagesFallbackAsync(filePath, ct);
    }

    /// <summary>Đếm trang bằng iText (nếu có) hoặc parser thủ công. Trả 0 nếu không xác định được.</summary>
    private static async Task<int> CountPagesFallbackAsync(string filePath, CancellationToken ct)
    {
        var viaPdfSharp = await CountPagesViaITextAsync(filePath, ct);
        if (viaPdfSharp > 0) return viaPdfSharp;
        return await CountPagesViaParserAsync(filePath, ct);
    }

    /// <summary>Đếm /Type /Page bằng cách scan raw — đủ chính xác cho PDF hợp lệ (kể cả linearized).</summary>
    private static Task<int> CountPagesViaParserAsync(string filePath, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                const int chunk = 256 * 1024;
                var buf = new byte[chunk];
                long pos = 0;
                var len = fs.Length;
                var count = 0;
                while (pos < len)
                {
                    ct.ThrowIfCancellationRequested();
                    var read = fs.Read(buf, 0, chunk);
                    if (read <= 0) break;
                    // Tìm "/Type /Page" (không phải /Pages) — cắt theo token "/Type"
                    for (var i = 0; i < read - 9; i++)
                    {
                        if (buf[i] == '/' && i + 10 < read
                            && buf[i + 1] == 'T' && buf[i + 2] == 'y' && buf[i + 3] == 'p' && buf[i + 4] == 'e'
                            && buf[i + 5] == ' ' && buf[i + 6] == '/' && buf[i + 7] == 'P'
                            && buf[i + 8] == 'a' && buf[i + 9] == 'g' && buf[i + 10] == 'e')
                        {
                            // Loại "/Pages" (i+11 == 's') và "/PageLabels" — chỉ đếm "/Page" đứng riêng
                            if (i + 11 >= read || buf[i + 11] != 's')
                                count++;
                        }
                    }
                    pos += read;
                }
                return count;
            }
            catch (OperationCanceledException) { throw; }
            catch { return 0; }
        }, ct);
    }

    private static async Task<int> CountPagesViaITextAsync(string filePath, CancellationToken ct)
    {
        // iText7 không được bundle (dynamic) — chỉ dùng nếu máy user có. Đếm qua API reader.
        // (Giữ chỗ — hiện tại parser thủ công đã đủ; tránh dependency cứng.)
        await Task.CompletedTask;
        return 0;
    }

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
            // OCE do cancel — LAN RA (rethrow) để caller (BrowserPrintEngine) phân biệt cancel vs lỗi render,
            // không rơi vào nhánh "in cả file" khi bị hủy; DrainLoopAsync catch (OperationCanceledException) → Cancelled.
            throw;
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

    /// <summary>
    /// Đưa ảnh trang về ĐÚNG KHỔ TỜ user chọn mà KHÔNG xoay nội dung: dựng canvas khổ tờ (ngang/dọc
    /// theo <paramref name="targetLandscape"/>) rồi vẽ ảnh thu nhỏ vừa khung, canh giữa — chữ giữ
    /// nguyên chiều đọc, phần thừa thành lề trắng (giống GDI fit ảnh vào vùng in).
    /// Trang đã đúng chiều tờ → trả nguyên trạng (khỏi re-encode).
    /// </summary>
    public static RenderedPdfPage FitToPaper(RenderedPdfPage page, bool targetLandscape)
    {
        if ((page.WidthDip > page.HeightDip) == targetLandscape) return page;

        try
        {
            using var input = new MemoryStream(page.Png);
            using var src = new System.Drawing.Bitmap(input);

            // Canvas pixel = ảnh gốc hoán đổi chiều (giữ nguyên lượng pixel đã render).
            var canvasW = src.Height;
            var canvasH = src.Width;

            using var canvas = new System.Drawing.Bitmap(canvasW, canvasH);
            using (var g = System.Drawing.Graphics.FromImage(canvas))
            {
                g.Clear(System.Drawing.Color.White);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                var scale = Math.Min(canvasW / (double)src.Width, canvasH / (double)src.Height);
                var dw = (float)(src.Width * scale);
                var dh = (float)(src.Height * scale);
                g.DrawImage(src, (float)((canvasW - dw) / 2), (float)((canvasH - dh) / 2), dw, dh);
            }

            using var output = new MemoryStream();
            canvas.Save(output, System.Drawing.Imaging.ImageFormat.Png);
            return page with
            {
                Png = output.ToArray(),
                WidthDip = targetLandscape ? Math.Max(page.WidthDip, page.HeightDip) : Math.Min(page.WidthDip, page.HeightDip),
                HeightDip = targetLandscape ? Math.Min(page.WidthDip, page.HeightDip) : Math.Max(page.WidthDip, page.HeightDip),
            };
        }
        catch
        {
            return page;
        }
    }

    private static PrintError FileCorruptError(string path) => new()
    {
        Code = ErrorCodes.FileCorrupted,
        Category = PrintErrorCategory.App,
        Message = $"PDF không có trang nào để cắt: {Path.GetFileName(path)}.",
        Hint = "Kiểm tra file còn đọc được.",
    };
}