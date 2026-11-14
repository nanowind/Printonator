using System.Runtime.InteropServices;
using Printonator.Core.Models;
using Printonator.Spool.Printing;

namespace Printonator.Spool.Tests;

/// <summary>
/// Khổ giấy và chiều giấy là HAI setting độc lập của user nhưng từng nằm chung một Try nuốt lỗi:
/// máy in/ driver từ chối khổ → chiều giấy user chọn mất luôn trong im lặng.
/// Test bằng stub thay cho Excel thật: binder dynamic của C# resolve được trên type public, nên
/// `sheet.PageSetup.PaperSize/Orientation` chạy đúng như với COM mà không cần cài Office.
/// </summary>
public class ExcelSetupTests
{
    /// <summary>Giả lập PageSetup của Excel. PaperSize ném khi được yêu cầu; Orientation ghi lại giá trị.</summary>
    public sealed class FakePageSetup
    {
        public bool ThrowOnPaper { get; init; }
        public int PaperSize { set { if (ThrowOnPaper) throw new COMException("Unable to set the PaperSize property."); } }
        public int Orientation { get; set; }
    }

    public sealed class FakeSheet
    {
        public FakeSheet(bool throwOnPaper = false)
            => PageSetup = new FakePageSetup { ThrowOnPaper = throwOnPaper };

        public FakePageSetup PageSetup { get; }
    }

    /// <summary>Driver TỪ CHỐI khổ: chiều giấy vẫn phải được đặt. Đây chính là bug — trước khi tách
    /// Try, exception ở PaperSize làm cả khối dừng nên Orientation không bao giờ được ghi.</summary>
    [Fact]
    public void Kho_bi_tu_choi_van_dat_duoc_chieu_giay()
    {
        var sheet = new FakeSheet(throwOnPaper: true);

        OfficeComPrintEngine.ApplyExcelSetup(sheet, "A4", PrintOrientation.Landscape, logMissingCode: false);

        Assert.Equal(2, sheet.PageSetup.Orientation);   // xlLandscape
    }

    [Fact]
    public void Kho_hop_le_va_chieu_giay_deu_dat_duoc()
    {
        var sheet = new FakeSheet();

        OfficeComPrintEngine.ApplyExcelSetup(sheet, "A4", PrintOrientation.Portrait, logMissingCode: false);

        Assert.Equal(1, sheet.PageSetup.Orientation);   // xlPortrait
    }

    /// <summary>AsDocument = "giữ chiều trong file" → KHÔNG được ép chiều nào.</summary>
    [Fact]
    public void AsDocument_khong_ep_chieu_giay()
    {
        var sheet = new FakeSheet();

        OfficeComPrintEngine.ApplyExcelSetup(sheet, PaperCatalog.AsDocument, PrintOrientation.AsDocument,
                                            logMissingCode: false);

        Assert.Equal(0, sheet.PageSetup.Orientation);   // chưa ai gán
    }

    /// <summary>Khổ không có mã Excel (A0) không được ném ra ngoài — hàm phải nuốt và vẫn đặt chiều.
    /// Trước đây nhánh này là Try chung nên hành vi giống; test khoá lại để không ai "dọn" thành
    /// gán trực tiếp.</summary>
    [Fact]
    public void Kho_khong_co_ma_khong_nem_ra_ngoai()
    {
        var sheet = new FakeSheet();

        OfficeComPrintEngine.ApplyExcelSetup(sheet, "A0", PrintOrientation.Landscape, logMissingCode: false);

        Assert.Equal(2, sheet.PageSetup.Orientation);
    }
}
