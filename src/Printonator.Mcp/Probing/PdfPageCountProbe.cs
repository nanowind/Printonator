using System.Text.RegularExpressions;

namespace Printonator.Mcp.Probing;

/// <summary>
/// Đếm trang PDF best-effort (đọc /Count trong các object /Pages).
/// Dùng cho PrintGuard ước lượng quota — thiên về ĐẾM THỪA (an toàn), không bao giờ under-count.
/// Không thay thế PDFium render; chỉ là probe nhanh trước khi in.
/// </summary>
public static class PdfPageCountProbe
{
    private static readonly Regex CountRegex = new(@"/Count\s+(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Trả số trang PDF, hoặc 0 nếu không xác định được (file hỏng/không phải PDF).</summary>
    public static int TryCount(string path)
    {
        try
        {
            if (!File.Exists(path)) return 0;
            var info = new FileInfo(path);
            if (info.Length < 4) return 0;

            using var fs = File.OpenRead(path);
            var head = new byte[4];
            fs.ReadExactly(head, 0, 4);
            if (head[0] != '%' || head[1] != 'P' || head[2] != 'D' || head[3] != 'F')
                return 0; // không phải PDF

            // Đọc tối đa 2MB đầu: /Count thường nằm trong object /Pages gần đầu file
            var toRead = (int)Math.Min(fs.Length, 2_000_000);
            var buf = new byte[toRead];
            fs.Position = 0;
            fs.ReadExactly(buf, 0, toRead);
            var text = System.Text.Encoding.ASCII.GetString(buf);

            var best = 0;
            foreach (Match m in CountRegex.Matches(text))
            {
                if (int.TryParse(m.Groups[1].Value, out var n) && n > best) best = n;
            }

            // PDF 1 trang đôi khi không ghi /Count — coi như 1 nếu có header /Pages
            if (best == 0 && text.Contains("/Type /Pages", StringComparison.Ordinal))
                best = 1;

            return best;
        }
        catch
        {
            return 0;
        }
    }
}