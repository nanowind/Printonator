using Printonator.Spool.Printing;
using Xunit;

namespace Printonator.Spool.Tests;

/// <summary>
/// Đọc kích thước ảnh từ header — đường in ảnh qua GDI dùng nó để biết trang NGANG/DỌC mà
/// KHÔNG phải decode cả ảnh (decode hai lần = gấp đôi thời gian trên ảnh lớn).
/// </summary>
public sealed class ImageSizeHeaderTests
{
    /// <summary>PNG 3x5 dựng tay: sig 8 byte + IHDR (length 4 + "IHDR" 4 + width 4 + height 4...).</summary>
    private static byte[] Png(int w, int h)
    {
        var b = new byte[40];
        byte[] sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        sig.CopyTo(b, 0);
        // length 13 @8..11, "IHDR" @12..15, width @16..19, height @20..23
        b[15] = (byte)'R';
        b[14] = (byte)'D';
        b[13] = (byte)'H';
        b[12] = (byte)'I';
        b[11] = 13;
        b[16] = (byte)(w >> 24); b[17] = (byte)(w >> 16); b[18] = (byte)(w >> 8); b[19] = (byte)w;
        b[20] = (byte)(h >> 24); b[21] = (byte)(h >> 16); b[22] = (byte)(h >> 8); b[23] = (byte)h;
        return b;
    }

    /// <summary>JPEG dựng tay: SOI + APP0 (bỏ qua được) + SOF0 (chiều cao @+5, rộng @+7).</summary>
    private static byte[] Jpeg(int w, int h)
    {
        var b = new List<byte> { 0xFF, 0xD8 };            // SOI
        b.AddRange([0xFF, 0xE0, 0x00, 0x10]);             // APP0, length 16
        b.AddRange(new byte[14]);                          // payload APP0
        b.AddRange([0xFF, 0xC0, 0x00, 0x11]);             // SOF0, length 17
        b.Add(8);                                          // precision
        b.AddRange([(byte)(h >> 8), (byte)h]);             // height
        b.AddRange([(byte)(w >> 8), (byte)w]);             // width
        b.AddRange(new byte[9]);
        return [.. b];
    }

    [Fact]
    public void Png_doc_dung_rong_cao()
    {
        Assert.True(GdiPrintEngine.TryReadImageSize(Png(1920, 1080), out var w, out var h));
        Assert.Equal(1920, w);
        Assert.Equal(1080, h);
    }

    [Fact]
    public void Jpeg_doc_dung_rong_cao()
    {
        Assert.True(GdiPrintEngine.TryReadImageSize(Jpeg(4000, 500), out var w, out var h));
        Assert.Equal(4000, w);
        Assert.Equal(500, h);
    }

    [Fact]
    public void Dinh_dang_la_tra_false_de_caller_tu_decode()
    {
        Assert.False(GdiPrintEngine.TryReadImageSize([0x42, 0x4D, 0, 0, 0, 0, 0, 0], out _, out _)); // BMP
        Assert.False(GdiPrintEngine.TryReadImageSize([], out _, out _));
        Assert.False(GdiPrintEngine.TryReadImageSize([0x89, 0x50], out _, out _));                  // PNG cụt
    }
}
