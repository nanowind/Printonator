using Printonator.Core.Models;
using Xunit;

namespace Printonator.Core.Tests;

/// <summary>
/// Bộ lọc trang lẻ/chẵn (Parity). Bug cũ: lọc xong rỗng thì rơi về "in cả file" — Word in đủ trang
/// dù user chọn "chỉ trang lẻ" mà job vẫn báo Done. ResolvePhysicalPages KHÔNG lọc (core khác cần tổng).
/// </summary>
public class ParityPagesTests
{
    private static PrintJob MakeJob(string range, PageParityFilter parity, int pageCount = 10) => new()
    {
        FilePath = "C:\\test.docx",
        FileName = "test.docx",
        Format = "DOCX",
        Config = new PrintConfig { PageRange = range, Parity = parity },
        PageCount = pageCount,
    };

    [Fact]
    public void ApplyParity_All_GiuNguyenDanhSach()
        => Assert.Equal(new[] { 1, 2, 3 }, PrintJob.ApplyParity(PageParityFilter.All, new[] { 1, 2, 3 }));

    [Fact]
    public void ApplyParity_Odd_ChiTrangLe()
        => Assert.Equal(new[] { 1, 3 }, PrintJob.ApplyParity(PageParityFilter.Odd, new[] { 1, 2, 3 }));

    [Fact]
    public void ApplyParity_Even_ChiTrangChan()
        => Assert.Equal(new[] { 2 }, PrintJob.ApplyParity(PageParityFilter.Even, new[] { 1, 2, 3 }));

    [Fact]
    public void ApplyParity_LonXonVaTrungLap_TraVeSapXepKhongTrung()
        => Assert.Equal(new[] { 1, 3, 5 }, PrintJob.ApplyParity(PageParityFilter.Odd, new[] { 5, 3, 5, 1, 2 }));

    [Fact]
    public void ResolvePrintPages_All_TraVeHetTrang()
        => Assert.Equal(Enumerable.Range(1, 10),
            MakeJob("All", PageParityFilter.All).ResolvePrintPages().Value!);

    [Fact]
    public void ResolvePrintPages_All_Odd_TraVeTrangLe()
    {
        var r = MakeJob("All", PageParityFilter.Odd).ResolvePrintPages();
        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.Equal(new[] { 1, 3, 5, 7, 9 }, r.Value);
    }

    [Fact]
    public void ResolvePrintPages_Range1Den6_Even()
    {
        var r = MakeJob("1-6", PageParityFilter.Even).ResolvePrintPages();
        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.Equal(new[] { 2, 4, 6 }, r.Value);
    }

    [Fact]
    public void ResolvePrintPages_RongSauLoc_TraLoiKhongInCaFile()
    {
        var r = MakeJob("2", PageParityFilter.Odd).ResolvePrintPages();
        Assert.False(r.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidPageRange, r.Error!.Code);
        Assert.Equal(PrintErrorCategory.Config, r.Error.Category);
    }

    [Fact]
    public void ResolvePhysicalPages_KhongBiLocParity()
    {
        var r = MakeJob("All", PageParityFilter.Odd).ResolvePhysicalPages();
        Assert.Equal(Enumerable.Range(1, 10), r.Value!);
    }
}
