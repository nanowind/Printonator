using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Printonator.Spool.Printing;

namespace Printonator.Spool.Tests;

/// <summary>
/// Test ép XÁM lúc vẽ của GdiPrintEngine.CreateGrayscaleAttributes (đường PDF → rasterize → GDI).
/// Đây là thứ đảm bảo "Grayscale" ra xám thật, không phụ thuộc driver có xử lý dmColor hay không.
/// </summary>
public class GrayscaleDrawTests
{
    [Fact]
    public void GrayscaleAttributes_bien_mau_sang_xam()
    {
        using var red = DrawThroughGrayscale(Color.FromArgb(255, 255, 0, 0));
        using var blue = DrawThroughGrayscale(Color.FromArgb(255, 0, 0, 255));

        var r = red.GetPixel(0, 0);
        Assert.True(r.R == r.G && r.G == r.B, $"đỏ phải ra xám, được ({r.R},{r.G},{r.B})");
        // 0.299 × 255 = 76.2 → luma Rec.601
        Assert.InRange(r.R, 74, 78);

        var b = blue.GetPixel(0, 0);
        Assert.True(b.R == b.G && b.G == b.B, $"xanh dương phải ra xám, được ({b.R},{b.G},{b.B})");
        // 0.114 × 255 = 29.1 → xanh dương tối hơn đỏ rõ rệt
        Assert.InRange(b.R, 27, 31);
    }

    [Fact]
    public void GrayscaleAttributes_giu_alpha()
    {
        using var semi = DrawThroughGrayscale(Color.FromArgb(128, 255, 0, 0));

        var p = semi.GetPixel(0, 0);
        // Không bị ép thành đục hoàn toàn, và không mất alpha.
        Assert.NotEqual(255, p.A);
        Assert.True(p.A > 0, $"alpha bị mất (A={p.A})");
        Assert.True(p.R == p.G && p.G == p.B, $"vẫn phải xám, được ({p.R},{p.G},{p.B})");
    }

    /// <summary>Tô 1 điểm màu lên bitmap 1x1 rồi vẽ nó qua CreateGrayscaleAttributes sang bitmap mới.</summary>
    private static Bitmap DrawThroughGrayscale(Color color)
    {
        using var src = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        src.SetPixel(0, 0, color);

        var dst = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        try
        {
            using var g = Graphics.FromImage(dst);
            // ImageAttributes là handle GDI+ KHÔNG thread-safe → tự tạo + Dispose trong phạm vi dùng.
            using var attrs = GdiPrintEngine.CreateGrayscaleAttributes();
            g.CompositingMode = CompositingMode.SourceCopy; // ghi đè — kết quả không phụ thuộc nền
            g.DrawImage(src, new Rectangle(0, 0, 1, 1), 0, 0, src.Width, src.Height,
                GraphicsUnit.Pixel, attrs);
            return dst;
        }
        catch
        {
            dst.Dispose();
            throw;
        }
    }
}
