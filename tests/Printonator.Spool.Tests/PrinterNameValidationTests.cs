using System.Reflection;
using Printonator.Spool.Printing;

namespace Printonator.Spool.Tests;

/// <summary>
/// Chặn command injection qua tên máy in: SpoolPrintEngine truyền tên máy vào ShellExecute("printto")
/// → tên chứa ký tự shell (dấu nháy, &amp;, |, ;) sẽ chạy lệnh ngoài. ValidPrinterName phải chặn các
/// tên đó mà vẫn cho qua tên máy in thật (kể cả tiếng Việt có dấu, dấu chấm, gạch nối).
/// </summary>
public class PrinterNameValidationTests
{
    private static bool Valid(string? name)
    {
        var m = typeof(SpoolPrintEngine).GetMethod("ValidPrinterName",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(m);
        return (bool)m!.Invoke(null, new object?[] { name })!;
    }

    [Theory]
    [InlineData("Microsoft Print to PDF")]
    [InlineData("Canon LBP151 (222)")]
    [InlineData("HP LaserJet Pro M404dn")]
    [InlineData("Máy in văn phòng")]
    [InlineData("Brother_DCP-7060D")]
    [InlineData("Printer.1")]
    public void Ten_may_in_hop_le_duoc_chap_nhan(string name)
        => Assert.True(Valid(name));

    [Theory]
    [InlineData("\" & calc & \"")]
    [InlineData("Printer; shutdown -s")]
    [InlineData("Printer | powershell")]
    [InlineData("Printer$(whoami)")]
    [InlineData("Printer`whoami`")]
    [InlineData("Printer\nrmdir /s")]
    [InlineData("Printer<>")]
    [InlineData("Printer{0}")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ten_co_ky_tu_shell_bi_chan(string? name)
        => Assert.False(Valid(name));
}
