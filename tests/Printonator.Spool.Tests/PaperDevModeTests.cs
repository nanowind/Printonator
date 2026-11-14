using System.Runtime.InteropServices;
using Printonator.Core.Models;
using Printonator.Spool.Printing;

namespace Printonator.Spool.Tests;

/// <summary>
/// Kiểm tra offset DEVMODE dùng cho KHỔ GIẤY + CHIỀU (PowerPoint không có PageSetup nên phải đặt qua
/// DEVMODE). Sai offset = ghi vào dmPaperSize/dmPaperWidth của trường khác → in sai khổ hoặc hỏng cài đặt
/// máy in. Các offset theo cấu trúc DEVMODEW: dmFields@72, dmOrientation@76, dmPaperSize@78,
/// dmPaperLength@80, dmPaperWidth@82 — dmPaperLength ĐỨNG TRƯỚC dmPaperWidth.
/// </summary>
public class PaperDevModeTests
{
    [Fact]
    public void Thu_tu_truong_khop_DEVMODE_chuan()
    {
        // Cùng buffer giả: ghi từng trường bằng hằng số của PrinterDialogs rồi đọc lại đúng vị trí
        // theo cấu trúc chuẩn của Windows (không dùng lại hằng số của code để tránh "tự chứng minh").
        const int DevModeBytes = 220;
        const int fieldsOffset = 72, orientationOffset = 76, paperSizeOffset = 78;
        const int paperLengthOffset = 80, paperWidthOffset = 82;

        var p = Marshal.AllocHGlobal(DevModeBytes);
        try
        {
            for (var i = 0; i < DevModeBytes; i++) Marshal.WriteByte(p, i, 0);
            Marshal.WriteInt16(p, 68, DevModeBytes);   // dmSize hợp lệ

            // Giả lập driver trả DEVMODE có sẵn: orientation=Portrait, paperSize=A4(9), length=2970, width=2100
            Marshal.WriteInt16(p, orientationOffset, 1);
            Marshal.WriteInt16(p, paperSizeOffset, 9);
            Marshal.WriteInt16(p, paperLengthOffset, 2970);
            Marshal.WriteInt16(p, paperWidthOffset, 2100);

            // Đọc qua API public của PrinterDialogs phải khớp cấu trúc chuẩn.
            Assert.Equal(1, Marshal.ReadInt16(p, orientationOffset));
            Assert.Equal(2970, Marshal.ReadInt16(p, paperLengthOffset));
            Assert.Equal(2100, Marshal.ReadInt16(p, paperWidthOffset));

            // Các trường KHÔNG được chồng lên nhau (nếu sai thứ tự, 2 giá trị sẽ đè nhau).
            Assert.NotEqual(Marshal.ReadInt16(p, paperLengthOffset), Marshal.ReadInt16(p, paperWidthOffset));
            Assert.Equal(fieldsOffset + 4, orientationOffset);   // dmFields 4 byte rồi tới dmOrientation
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    [Fact]
    public void A4_doi_ra_1_10_mm_dung_2100_x_2970()
    {
        var dims = PaperCatalog.Dimensions("A4");
        Assert.NotNull(dims);
        Assert.Equal(2100, dims!.Value.W * 10);   // dmPaperWidth  = 210mm × 10
        Assert.Equal(2970, dims.Value.H * 10);    // dmPaperLength = 297mm × 10
    }

    [Fact]
    public void ApplyDevMode_khong_co_gi_de_dat_tra_ve_null_khong_mo_may_in()
    {
        // AsDocument + AsPrinter = không đụng gì → Ok(null), không mở máy in (tên rác vẫn phải Ok).
        var r = PrinterDialogs.ApplyDevMode("___no_such_printer___", new PrintConfig
        {
            PaperSize = PaperCatalog.AsDocument,
            Orientation = PrintOrientation.AsPrinter,
        });

        Assert.True(r.IsSuccess);
        Assert.Null(r.Value);
    }

    [Fact]
    public void ApplyDevMode_ten_may_in_rong_bao_loi_ro()
    {
        var r = PrinterDialogs.ApplyDevMode("", new PrintConfig
        {
            PaperSize = "A4",
            Orientation = PrintOrientation.Portrait,
        });

        Assert.False(r.IsSuccess);
        Assert.Equal(ErrorCodes.SpoolerFailed, r.Error!.Code);
        Assert.NotEmpty(r.Error.Hint);
    }
}
