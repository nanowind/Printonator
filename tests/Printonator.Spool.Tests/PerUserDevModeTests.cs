using Printonator.Core.Models;
using Printonator.Spool.Printing;

namespace Printonator.Spool.Tests;

/// <summary>
/// Test đường ghi DEVMODE qua PRINTER_INFO_9 (level 9 — DEVMODE riêng của user, không cần admin).
/// SetPrinter(2) trả lỗi 5 ACCESS_DENIED cho user thường kể cả khi buffer không đổi, nên level 9 là
/// đường duy nhất chạy được; ba test dưới đây chốt phần tính toán + một lần ghi thật trên máy in ảo.
/// </summary>
[Collection(RealOfficeCom.CollectionName)]   // cùng máy in ảo với test Office thật → chạy tuần tự
public class PerUserDevModeTests
{
    [Fact]
    public void DevMode_payload_giu_nguyen_dmDriverExtra()
    {
        Assert.Equal(260, PrinterDialogs.DevModePayloadSize(220, 40));
        // dmSize < MinDevModeSize (96) = header vô lý → 0, không bao giờ AllocHGlobal bằng số rác.
        Assert.Equal(0, PrinterDialogs.DevModePayloadSize(90, 0));
        // dmDriverExtra âm = rác → 0.
        Assert.Equal(0, PrinterDialogs.DevModePayloadSize(220, -1));
    }

    [Theory]
    [InlineData(5, ErrorCodes.PrinterNoPermission)]      // ACCESS_DENIED
    [InlineData(1314, ErrorCodes.PrinterNoPermission)]   // PRIVILEGE_NOT_HELD
    [InlineData(87, ErrorCodes.SpoolerFailed)]           // ERROR_INVALID_PARAMETER
    public void DevMode_loi_quyen_anh_xa_dung_ma(int win32, string expected)
        => Assert.Equal(expected, PrinterDialogs.DevModeErrorCode(win32));

    /// <summary>Bằng chứng DUY NHẤT (tự động) rằng buffer level 9 tự dựng được spooler chấp nhận:
    /// ghi thật rồi Dispose thật. Không có máy in ảo → Skipped, KHÔNG early-return (báo Passed rỗng).</summary>
    [PdfPrinterFact]
    public void ApplyDevMode_ghi_duoc_bang_level_9_va_restore_khong_nem()
    {
        var r = PrinterDialogs.ApplyDevMode(RealOfficeCom.PdfPrinter,
            new PrintConfig { ColorMode = PrintColorMode.Grayscale });

        Assert.True(r.IsSuccess, r.Error?.ToString());
        Assert.NotNull(r.Value);
        r.Value!.Dispose();   // restore (xoá override per-user) — không được ném
    }
}

/// <summary>Chỉ chạy khi máy có máy in ảo PDF (cùng điều kiện với test Office thật).</summary>
public sealed class PdfPrinterFactAttribute : FactAttribute
{
    public PdfPrinterFactAttribute()
    {
        if (!RealOfficeCom.HasPdfPrinter())
            Skip = $"Không có máy in \"{RealOfficeCom.PdfPrinter}\" — không kiểm được đường ghi DEVMODE.";
    }
}
