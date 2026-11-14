using System.Reflection;
using Printonator.Core.Models;
using Printonator.Spool.Printing;

namespace Printonator.Spool.Tests;

/// <summary>
/// Engine KHÔNG lọc được trang lẻ/chẵn phải BÁO LỖI thay vì in cả file trong im lặng — và các đường
/// render (đã cắt sẵn PDF) phải reset Parity=All để không tự chặn chính mình.
/// </summary>
public class ParityGuardTests
{
    private static PrintJob MakeJob(string format = "DOCX", int pageCount = 10,
        string range = "All", PageParityFilter parity = PageParityFilter.All) => new()
        {
            FilePath = "C:\\test." + format.ToLowerInvariant(),
            FileName = "test." + format.ToLowerInvariant(),
            Format = format,
            Config = new PrintConfig { PageRange = range, Parity = parity, PrinterName = "___no_such_printer___" },
            PageCount = pageCount,
        };

    [Fact]
    public void WordPrintArgs_All_Odd_TraVeDanhSachTrangLe()
    {
        var r = OfficeComPrintEngine.WordPrintArgs(MakeJob(parity: PageParityFilter.Odd));
        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.False(r.Value.All);
        Assert.Equal("1,3,5,7,9", r.Value.Pages);
    }

    [Fact]
    public void WordPrintArgs_All_All_LietKeDuTrang()
    {
        var r = OfficeComPrintEngine.WordPrintArgs(MakeJob());
        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.False(r.Value.All);   // giữ hành vi cũ: gửi danh sách trang tường minh
        Assert.Equal("1,2,3,4,5,6,7,8,9,10", r.Value.Pages);
    }

    [Fact]
    public void WordPrintArgs_RongSauLoc_TraLoiThayViInCaFile()
    {
        var r = OfficeComPrintEngine.WordPrintArgs(MakeJob(range: "2", parity: PageParityFilter.Odd));
        Assert.False(r.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidPageRange, r.Error!.Code);
    }

    [Fact]
    public async Task SpoolPrintEngine_ParityOdd_TraUnsupportedOption()
    {
        var job = MakeJob("TXT", 10, "All", PageParityFilter.Odd);
        var r = await new SpoolPrintEngine().PrintAsync(job, CancellationToken.None);
        Assert.False(r.IsSuccess);
        Assert.Equal(ErrorCodes.UnsupportedOption, r.Error!.Code);
    }

    [Fact]
    public void BuildSpoolConfig_ResetParityVeAll()
    {
        var job = MakeJob("PDF", 10, "1-6", PageParityFilter.Odd);
        var m = typeof(BrowserPrintPipeline).GetMethod("BuildSpoolConfig",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(m);
        var cfg = (PrintConfig)m!.Invoke(null, new object[] { job })!;
        Assert.Equal(PageParityFilter.All, cfg.Parity);
    }
}
