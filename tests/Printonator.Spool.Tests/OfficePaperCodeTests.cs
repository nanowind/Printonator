using Printonator.Core.Models;
using Printonator.Spool.Printing;

namespace Printonator.Spool.Tests;

/// <summary>
/// Bảng mã khổ giấy Word/Excel PHẢI khớp enum thật của Office. Word và Excel dùng HAI hệ số khác
/// nhau (Word: A4=7, A3=6, B5=11; Excel: A4=9, A3=8, B5=13) và Word còn NUỐT IM LẶNG mã sai nghĩa
/// (set 8 → Word hiểu A4), nên sai một số là in nhầm khổ mà job vẫn báo Done.
/// Giá trị ở đây đọc từ Microsoft.Office.Interop.Word.WdPaperSize / .Excel.XlPaperSize
/// (GAC 15.0.0.0) trên máy có Office 16.0 — không phải suy đoán.
/// </summary>
public class OfficePaperCodeTests
{
    /// <summary>WdPaperSize: wdPaperA3=6, A4=7, A5=9, B4=10, B5=11, Letter=2, Legal=4,
    /// Ledger=19, Executive=5, Tabloid=23, Statement=22.</summary>
    [Theory]
    [InlineData("A3", 6)]
    [InlineData("A4", 7)]
    [InlineData("A5", 9)]
    [InlineData("B4", 10)]
    [InlineData("B5", 11)]
    [InlineData("Letter", 2)]
    [InlineData("Legal", 4)]
    [InlineData("Ledger", 19)]
    [InlineData("Executive", 5)]
    [InlineData("Tabloid", 23)]
    [InlineData("Statement", 22)]
    public void Ma_Word_khop_WdPaperSize(string name, int expected)
        => Assert.Equal(expected, OfficeComPrintEngine.WordPaperCode[name]);

    /// <summary>XlPaperSize trùng số với DMPAPER_*: A3=8, A4=9, A5=11, B4=12, B5=13, Letter=1,
    /// Legal=5, Ledger=4, Executive=7, Tabloid=3, Statement=6.</summary>
    [Theory]
    [InlineData("A3", 8)]
    [InlineData("A4", 9)]
    [InlineData("A5", 11)]
    [InlineData("B4", 12)]
    [InlineData("B5", 13)]
    [InlineData("Letter", 1)]
    [InlineData("Legal", 5)]
    [InlineData("Ledger", 4)]
    [InlineData("Executive", 7)]
    [InlineData("Tabloid", 3)]
    [InlineData("Statement", 6)]
    public void Ma_Excel_khop_XlPaperSize(string name, int expected)
        => Assert.Equal(expected, OfficeComPrintEngine.ExcelPaperCode[name]);

    /// <summary>Hai bảng phải KHÁC nhau ở những khổ dùng chung — nếu ai đó "gộp cho gọn" một bảng
    /// thì một trong hai app sẽ in nhầm khổ. Đây là chốt chống hồi quy.</summary>
    [Theory]
    [InlineData("A3")] [InlineData("A4")] [InlineData("A5")] [InlineData("B5")]
    [InlineData("Letter")] [InlineData("Legal")] [InlineData("Executive")]
    public void Word_va_Excel_dung_ma_khac_nhau(string name)
        => Assert.NotEqual(OfficeComPrintEngine.WordPaperCode[name], OfficeComPrintEngine.ExcelPaperCode[name]);

    /// <summary>Khổ không có thành viên trong enum Office (A0/A1/A2/A6) KHÔNG được nằm trong bảng:
    /// mã bịa sẽ ném "Value out of range" (Word) hoặc "Unable to set the PaperSize" (Excel).
    /// Word đi đường PageWidth/PageHeight, Excel bỏ qua (có log) và giữ khổ driver.</summary>
    [Theory]
    [InlineData("A0")] [InlineData("A1")] [InlineData("A2")] [InlineData("A6")]
    public void Kho_khong_co_ma_Office_khong_nam_trong_bang(string name)
    {
        Assert.False(OfficeComPrintEngine.WordPaperCode.ContainsKey(name));
        Assert.False(OfficeComPrintEngine.ExcelPaperCode.ContainsKey(name));
    }

    /// <summary>Mọi tên khổ trong bảng mã Office PHẢI có trong PaperCatalog, nếu không UI không bao
    /// giờ tạo ra được lựa chọn đó (chỉ MCP free-text chạm tới) và preview rơi về A4 sai.</summary>
    [Fact]
    public void Moi_ten_kho_Office_deu_co_trong_PaperCatalog()
    {
        foreach (var name in OfficeComPrintEngine.WordPaperCode.Keys
                     .Concat(OfficeComPrintEngine.ExcelPaperCode.Keys).Distinct())
        {
            Assert.NotNull(PaperCatalog.Dimensions(name));
        }
    }

    /// <summary>Mọi mã Word phải nằm trong 0..41 (giới hạn thật của WdPaperSize — 42+ ném lỗi).</summary>
    [Fact]
    public void Moi_ma_Word_deu_trong_gioi_han_enum()
        => Assert.All(OfficeComPrintEngine.WordPaperCode, kv =>
        {
            Assert.InRange(kv.Value, 0, 41);
        });

    // ===== PlanWordPaper: phần dễ hỏng khi đặt khổ bằng kích thước =====

    /// <summary>Khổ có mã Word → dùng mã, không đụng PageWidth/PageHeight.</summary>
    [Theory]
    [InlineData("A4", 7)] [InlineData("A3", 6)] [InlineData("B5", 11)] [InlineData("Legal", 4)]
    public void Kho_co_ma_Word_thi_dung_ma(string name, int code)
    {
        var plan = OfficeComPrintEngine.PlanWordPaper(name);

        Assert.Equal((int?)code, plan.Code);
        Assert.Null(plan.WidthPt);
        Assert.Null(plan.HeightPt);
    }

    /// <summary>A6 (105×148 mm) không có mã Word nhưng nằm trong trần → đi đường kích thước.</summary>
    [Fact]
    public void Kho_nho_khong_co_ma_di_duong_kich_thuoc()
    {
        var plan = OfficeComPrintEngine.PlanWordPaper("A6");

        Assert.Null(plan.Code);
        Assert.True(plan.IsCustom);
        Assert.Equal(105 * 72.0 / 25.4, plan.WidthPt!.Value, 3);
        Assert.Equal(148 * 72.0 / 25.4, plan.HeightPt!.Value, 3);
    }

    /// <summary>A2 rộng 420 mm (đặt được) nhưng cao 594 mm = 1684 pt VƯỢT trần 1584 pt. Nếu vẫn ghi
    /// width trước rồi height ném thì tài liệu còn khổ lai 420 × A4 — in sai mà job báo Done.
    /// Vì thế phải HUỶ CẢ HAI, không ghi một chiều nào.</summary>
    [Theory]
    [InlineData("A2")] [InlineData("A1")] [InlineData("A0")]
    public void Kho_vuot_tran_Word_thi_khong_dat_gi(string name)
    {
        var plan = OfficeComPrintEngine.PlanWordPaper(name);

        Assert.Null(plan.Code);
        Assert.Null(plan.WidthPt);
        Assert.Null(plan.HeightPt);
        Assert.True(plan.IsSkipped);
    }

    /// <summary>AsDocument / AsPrinter / tên lạ → không đặt gì, giữ khổ trong file.</summary>
    [Theory]
    [InlineData("AsDocument")]
    [InlineData("AsPrinter")]
    [InlineData("")]
    [InlineData("khổ-của-tôi")]
    public void Gia_tri_dac_biet_khong_dat_gi(string name)
    {
        var plan = OfficeComPrintEngine.PlanWordPaper(name);

        Assert.True(plan.IsSkipped);
    }

    /// <summary>Trần thật của Word là 1584 pt (đo trên Word 16.0). Đổi hằng số này mà không đo lại
    /// là mở đường cho đúng lỗi khổ lai ở test trên.</summary>
    [Fact]
    public void Tran_trang_cua_Word_dung_1584_pt()
    {
        Assert.Equal(1584.0, OfficeComPrintEngine.WordMaxPagePoint);
        Assert.Equal(7.2, OfficeComPrintEngine.WordMinPagePoint, 3);
    }
}
