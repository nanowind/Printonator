using Printonator.Core.Models;
using Xunit;

namespace Printonator.Core.Tests;

/// <summary>
/// Unit test lõi: ResolvePhysicalPages (page-range + section-aware DOCX).
/// Đây là phần QUAN TRỌNG NHẤT của logic in — nếu sai thì in sai trang.
/// </summary>
public class PageRangeTests
{
    private static PrintJob MakeJob(string range, int pageCount = 10, bool withSections = false)
    {
        var job = new PrintJob
        {
            FilePath = "C:\\test.pdf",
            FileName = "test.pdf",
            Format = "PDF",
            Config = new PrintConfig { PageRange = range },
            PageCount = pageCount,
        };
        if (withSections)
        {
            job.Sections.Add(new SectionMap { Index = 1, FirstPhysicalPage = 1, LastPhysicalPage = 2 });
            job.Sections.Add(new SectionMap { Index = 2, FirstPhysicalPage = 3, LastPhysicalPage = 10 });
        }
        return job;
    }

    [Theory]
    [InlineData("All", 10, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })]
    [InlineData("2,5", 10, new[] { 2, 5 })]
    [InlineData("3-4", 10, new[] { 3, 4 })]
    [InlineData("1-2,7", 10, new[] { 1, 2, 7 })]
    [InlineData("5-3", 10, new[] { 3, 4, 5 })]           // reversed → sorted
    [InlineData("", 10, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })] // empty = All
    [InlineData("last", 10, new[] { 10 })]               // last page only
    [InlineData("last3", 10, new[] { 8, 9, 10 })]        // last 3 pages
    [InlineData("last1", 10, new[] { 10 })]              // last 1 page = last
    public void ParseRange_Valid(string range, int pages, int[] expected)
    {
        var result = MakeJob(range, pages).ResolvePhysicalPages();
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(expected, result.Value);
    }

    [Theory]
    [InlineData("0", 10)]          // page 0 invalid
    [InlineData("11", 10)]         // beyond page count
    [InlineData("abc", 10)]        // not a number
    [InlineData("1-2;5", 10)]      // bad separator
    [InlineData("1-", 10)]         // missing end
    [InlineData("-5", 10)]         // missing start
    [InlineData("last0", 10)]      // last0 invalid — N must be > 0
    [InlineData("last", 0)]        // chưa biết số trang → fail
    public void ParseRange_Invalid_ReturnsError(string range, int pages)
    {
        var result = MakeJob(range, pages).ResolvePhysicalPages();
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidPageRange, result.Error!.Code);
        Assert.Equal(PrintErrorCategory.Config, result.Error.Category);
        Assert.False(string.IsNullOrEmpty(result.Error.Hint));
    }

    [Fact]
    public void SectionRange_MapsTo_PhysicalPages()
    {
        // S2:1-3 = section 2, trang 1-3 → physical 3,4,5 (FirstPhysicalPage=3)
        var result = MakeJob("S2:1-3", 10, withSections: true).ResolvePhysicalPages();
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(new[] { 3, 4, 5 }, result.Value);
    }

    [Fact]
    public void SectionRange_InvalidSection_ReturnsSectionNotFound()
    {
        var result = MakeJob("S5:1-2", 10, withSections: true).ResolvePhysicalPages();
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.SectionNotFound, result.Error!.Code);
        // Hint phải liệt kê các section hợp lệ
        Assert.Contains("S1", result.Error.Hint);
        Assert.Contains("S2", result.Error.Hint);
    }

    [Fact]
    public void SectionRange_OutOfPageCount_ReturnsInvalid()
    {
        // Section 2 chỉ có 8 trang (3-10) — S2:9 vượt giới hạn
        var result = MakeJob("S2:9", 10, withSections: true).ResolvePhysicalPages();
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidPageRange, result.Error!.Code);
    }

    [Fact]
    public void SectionRange_MultiPages_Distinct_Sorted()
    {
        var result = MakeJob("S1:2,1-2", 10, withSections: true).ResolvePhysicalPages();
        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { 1, 2 }, result.Value); // distinct + sorted
    }

    [Fact]
    public void All_WithZeroPageCount_ReturnsPage1()
    {
        var result = MakeJob("All", 0).ResolvePhysicalPages();
        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { 1 }, result.Value);
    }

    [Fact]
    public void SectionRange_LastMacro_ReturnsInvalid()
    {
        // Section không hỗ trợ macro last/lastN — phải nhập số trang cụ thể
        var result = MakeJob("S2:last", 10, withSections: true).ResolvePhysicalPages();
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidPageRange, result.Error!.Code);
    }
}