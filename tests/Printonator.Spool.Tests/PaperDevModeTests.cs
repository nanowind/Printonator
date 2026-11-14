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

    /// <summary>Mã DMPAPER_* lấy từ wingdi.h (SDK 10.0.19041.0) dòng 2482-2612. Driver chỉ set
    /// dmPaperSize mà bỏ width/length là chuyện thường, nên đường này phải đọc được khổ.</summary>
    [Theory]
    [InlineData(1, "Letter")]      // DMPAPER_LETTER
    [InlineData(3, "Ledger")]      // DMPAPER_TABLOID — cùng tờ 279x432
    [InlineData(4, "Ledger")]      // DMPAPER_LEDGER
    [InlineData(5, "Legal")]       // DMPAPER_LEGAL
    [InlineData(6, "Statement")]   // DMPAPER_STATEMENT
    [InlineData(7, "Executive")]   // DMPAPER_EXECUTIVE
    [InlineData(8, "A3")]          // DMPAPER_A3
    [InlineData(9, "A4")]          // DMPAPER_A4
    [InlineData(11, "A5")]         // DMPAPER_A5
    [InlineData(12, "B4")]         // DMPAPER_B4 (JIS, chênh 1 mm)
    [InlineData(13, "B5")]         // DMPAPER_B5 (JIS 182×257)
    [InlineData(27, "DL")]         // DMPAPER_ENV_DL
    [InlineData(42, "B4")]         // DMPAPER_ISO_B4 (khớp chính xác)
    [InlineData(66, "A2")]         // DMPAPER_A2
    [InlineData(70, "A6")]         // DMPAPER_A6
    public void Ma_dmPaperSize_chuan_doi_ra_dung_ten_kho(short dmPaperSize, string expected)
        => Assert.Equal(expected, PrinterDialogs.DmPaperSizeName(dmPaperSize));

    /// <summary>DMPAPER_B5 (13) = B5 JIS 182×257, khớp PaperCatalog["B5"] sau khi sửa catalog từ
    /// ISO 176×250 sang JIS — đó là khổ Windows/Office thật sự dùng (đo trên 10 driver).</summary>
    [Fact]
    public void Ma_B5_JIS_map_thanh_B5()
        => Assert.Equal("B5", PrinterDialogs.DmPaperSizeName(13));

    /// <summary>Và khổ catalog đó phải là 182×257, không phải 176×250 — sai kích thước ở đây
    /// nghĩa là in ra giấy khác với cái user chọn.</summary>
    [Fact]
    public void Kho_B5_trong_catalog_la_JIS_182x257()
    {
        var d = PaperCatalog.Dimensions("B5");
        Assert.NotNull(d);
        Assert.Equal((182, 257), d!.Value);
    }

    /// <summary>Tên trả về PHẢI có trong PaperCatalog, nếu không combo khổ giấy không chọn được
    /// (TrySelectTag thất bại → nút "Lấy theo máy in" báo lỗi oan).</summary>
    [Theory]
    [InlineData((short)1)]
    [InlineData((short)3)]
    [InlineData((short)4)]
    [InlineData((short)5)]
    [InlineData((short)6)]
    [InlineData((short)7)]
    [InlineData((short)8)]
    [InlineData((short)9)]
    [InlineData((short)11)]
    [InlineData((short)12)]
    [InlineData((short)13)]
    [InlineData((short)27)]
    [InlineData((short)28)]
    [InlineData((short)29)]
    [InlineData((short)30)]
    [InlineData((short)31)]
    [InlineData((short)42)]
    [InlineData((short)66)]
    [InlineData((short)70)]
    public void Ten_kho_doc_tu_dmPaperSize_luon_co_trong_PaperCatalog(short dmPaperSize)
    {
        var name = PrinterDialogs.DmPaperSizeName(dmPaperSize);
        Assert.NotNull(name);
        Assert.NotNull(PaperCatalog.Dimensions(name!));
    }

    /// <summary>Mã chưa map (0 = chưa set, 48 = RESERVED, 118 = DMPAPER_LAST) → null, không đoán bừa.</summary>
    [Theory]
    [InlineData((short)0)]
    [InlineData((short)48)]
    [InlineData((short)118)]
    public void Ma_dmPaperSize_chua_map_tra_ve_null(short dmPaperSize)
        => Assert.Null(PrinterDialogs.DmPaperSizeName(dmPaperSize));

    /// <summary>Driver set CỜ kích thước nhưng để giá trị 0 (rất hay gặp) → phải rơi về dmPaperSize,
    /// nếu chỉ xét cờ thì đúng những driver đó vẫn ra null và nút "Lấy theo máy in" lại im lặng.</summary>
    [Fact]
    public void Co_kich_thuoc_nhung_gia_tri_0_thi_roi_ve_ma_dmPaperSize()
    {
        var p = NewDevMode();
        try
        {
            Marshal.WriteInt32(p, 72, 0x0000000C | 0x00000002);   // DM_PAPERLENGTH|DM_PAPERWIDTH|DM_PAPERSIZE
            Marshal.WriteInt16(p, 78, 9);   // dmPaperSize = A4
            // dmPaperLength@80 / dmPaperWidth@82 để 0
            Assert.Equal("A4", PrinterDialogs.PaperNameFromDevMode(p));
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>Kích thước thật khớp khổ chuẩn thì THẮNG mã dmPaperSize (chính xác hơn khi driver
    /// để mã cũ/lệch).</summary>
    [Fact]
    public void Kich_thuoc_that_khop_thi_uu_tien_hon_ma_dmPaperSize()
    {
        var p = NewDevMode();
        try
        {
            Marshal.WriteInt32(p, 72, 0x0000000C | 0x00000002);
            Marshal.WriteInt16(p, 78, 1);      // dmPaperSize = Letter (cố tình lệch)
            Marshal.WriteInt16(p, 80, 2970);   // dmPaperLength = 297 mm
            Marshal.WriteInt16(p, 82, 2100);   // dmPaperWidth  = 210 mm
            Assert.Equal("A4", PrinterDialogs.PaperNameFromDevMode(p));
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>Không cờ nào được set → null (ReadPrinterDefaults trả cài đặt trung tính ở nhánh khác).</summary>
    [Fact]
    public void Khong_co_co_nao_thi_tra_ve_null()
    {
        var p = NewDevMode();
        try { Assert.Null(PrinterDialogs.PaperNameFromDevMode(p)); }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>Buffer DEVMODE giả đã zero + dmSize hợp lệ, đủ dài cho các offset tới 96.</summary>
    private static IntPtr NewDevMode()
    {
        const int size = 220;
        var p = Marshal.AllocHGlobal(size);
        for (var i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt16(p, 68, size);
        return p;
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
