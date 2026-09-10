using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using Printonator.Core.Models;

namespace Printonator.Spool.Printing;

/// <summary>
/// Ghi file PDF MỚI từ các trang PNG đã rasterize (Windows.Data.Pdf) — KHÔNG dùng trình duyệt.
/// Output chỉ chứa ảnh các trang + cấu trúc PDF tối giản: mọi METADATA / CHỮ KÝ SỐ / annotation /
/// field/biểu mẫu của file gốc đều BỊ BỎ (đúng bản chất "in ra" = tài liệu MỚI — không kế thừa
/// chứng thư của bản gốc; đã chốt 2026-09-10: copy thẳng file gốc là lỗ hổng bảo mật vì giữ chữ ký).
/// Ảnh nhúng dạng JPEG (DCTDecode) — gọn, đọc được mọi nơi.
/// Khổ trang = khổ GỐC của từng trang PDF (DIPs → points), giữ đúng chiều đọc.
/// </summary>
public static class PdfImageWriter
{
    /// <summary>Ghi PDF từ các trang PNG. Lỗi ghi → ném (caller bọc thành PrintError).</summary>
    public static void Write(string path, IReadOnlyList<RenderedPdfPage> pages)
    {
        if (pages is null || pages.Count == 0)
            throw new InvalidOperationException("Không có trang nào để ghi PDF.");
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Thiếu đường dẫn PDF xuất.", nameof(path));

        // 1) PNG → JPEG + kích thước trang (points = DIPs / 96 * 72).
        var imgs = new List<(byte[] Jpeg, int Px, int Py, double Wpt, double Hpt)>(pages.Count);
        foreach (var p in pages)
        {
            using var bmp = new Bitmap(new MemoryStream(p.Png));
            byte[] jpg;
            using (var jpgMs = new MemoryStream())
            {
                bmp.Save(jpgMs, ImageFormat.Jpeg);
                jpg = jpgMs.ToArray();
            }
            imgs.Add((jpg, bmp.Width, bmp.Height, p.WidthDip / 96.0 * 72.0, p.HeightDip / 96.0 * 72.0));
        }

        var n = pages.Count;
        // Số object: [0]=dummy, 1=Catalog, 2=Pages, 3..2+n=Page, 3+n..2+2n=Image, 3+2n..2+3n=Content, +1=xref
        var offsets = new long[3 + 3 * n + 1];

        using var ms = new MemoryStream();
        void WriteAscii(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        void BeginObj(int num) { offsets[num] = ms.Position; WriteAscii($"{num} 0 obj\n"); }
        void EndObj() { WriteAscii("endobj\n"); }

        // Header
        WriteAscii("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");

        // 1: Catalog
        BeginObj(1); WriteAscii("<< /Type /Catalog /Pages 2 0 R >>\n"); EndObj();

        // 2: Pages
        {
            BeginObj(2);
            var kids = string.Join(" ", Enumerable.Range(3, n).Select(i => $"{i} 0 R"));
            WriteAscii($"<< /Type /Pages /Kids [{kids}] /Count {n} >>\n");
            EndObj();
        }

        // 3..2+n: Page objects (số hiệu tạm thời cho Image/Contents tính sau — object number cố định theo layout)
        for (var i = 0; i < n; i++)
        {
            var pageNum = 3 + i;
            var imgNum = 3 + n + i;
            var ctNum = 3 + 2 * n + i;
            var (_, _, _, wpt, hpt) = imgs[i];
            BeginObj(pageNum);
            WriteAscii($"<< /Type /Page /Parent 2 0 R " +
                       $"/MediaBox [0 0 {Rounded(wpt)} {Rounded(hpt)}] " +
                       $"/Resources << /XObject << /Im{i} {imgNum} 0 R >> >> " +
                       $"/Contents {ctNum} 0 R >>\n");
            EndObj();
        }

        // 3+n..2+2n: Image XObjects (JPEG)
        for (var i = 0; i < n; i++)
        {
            var imgNum = 3 + n + i;
            var (jpg, px, py, _, _) = imgs[i];
            BeginObj(imgNum);
            WriteAscii($"<< /Type /XObject /Subtype /Image /Width {px} /Height {py} " +
                       $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpg.Length} >>\n");
            WriteAscii($"stream\n");
            ms.Write(jpg, 0, jpg.Length);
            WriteAscii("\nendstream\n");
            EndObj();
        }

        // 3+2n..2+3n: Content streams — vẽ ảnh lấp đầy trang (khổ points)
        for (var i = 0; i < n; i++)
        {
            var ctNum = 3 + 2 * n + i;
            var (_, _, _, wpt, hpt) = imgs[i];
            var content = $"q\n{Rounded(wpt)} 0 0 {Rounded(hpt)} 0 0 cm\n/Im{i} Do\nQ\n";
            BeginObj(ctNum);
            WriteAscii($"<< /Length {content.Length} >>\nstream\n{content}endstream\n");
            EndObj();
        }

        // Xref
        var xrefPos = ms.Position;
        offsets[offsets.Length - 1] = xrefPos;
        WriteAscii($"xref\n0 {offsets.Length}\n");
        WriteAscii("0000000000 65535 f \n");
        for (var i = 1; i <= offsets.Length - 2; i++)
            WriteAscii($"{offsets[i]:0000000000} 00000 n \n");
        WriteAscii($"trailer\n<< /Size {offsets.Length} /Root 1 0 R >>\n");
        WriteAscii("startxref\n" + xrefPos + "\n%%EOF");

        File.WriteAllBytes(path, ms.ToArray());
    }

    private static string Rounded(double v) => Math.Round(v, 2).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}