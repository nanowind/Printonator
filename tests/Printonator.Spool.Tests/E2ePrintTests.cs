using Printonator.Core.Models;
using Printonator.Spool.Printing;

namespace Printonator.Spool.Tests;

/// <summary>
/// E2E print tests: chạy engine THẬT (browser render headless) in ra máy PDF ẢO và kiểm tra
/// FILE PDF xuất hiện cạnh file gốc — đúng yêu cầu "print to PDF tự lấy tên file + cùng thư mục,
/// khỏi gõ tên mỗi lần". Chạy được trên CI windows-latest (Edge có sẵn, không cần Office/máy in thật —
/// máy in ảo KHÔNG đụng spooler).
/// </summary>
public class E2ePrintTests
{
    [Fact]
    public async Task PrintToPdfPrinter_SavesPdfNextToSource_WithSameName()
    {
        var dir = Path.Combine(Path.GetTempPath(), "printonator-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var src = Path.Combine(dir, "bieu mau 2026.txt");
            await File.WriteAllTextAsync(src, "Printonator E2E — in ra may PDF phai tao file PDF cung thu muc.");

            var job = new PrintJob
            {
                FilePath = src,
                FileName = Path.GetFileName(src),
                Format = "TXT",
                Config = new PrintConfig { PrinterName = "Microsoft Print to PDF" },
            };

            var r = await new BrowserPrintEngine().PrintAsync(job, CancellationToken.None);
            Assert.True(r.IsSuccess,
                r.IsSuccess ? "" : $"print lỗi: {r.Error?.Message} {r.Error?.Hint} {r.Error?.Detail}");

            // PDF phải nằm CẠNH file gốc, cùng tên, đuôi .pdf — không gõ tên, không mở hộp thoại.
            var expected = Path.Combine(dir, "bieu mau 2026.pdf");
            Assert.True(File.Exists(expected), $"Không thấy file PDF xuất ra: {expected}");
            Assert.True(new FileInfo(expected).Length > 0, "File PDF xuất ra bị RỖNG");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void IsVirtualPrinter_DetectsPdfPrinters()
    {
        Assert.True(PrinterService.IsVirtualPrinter("Microsoft Print to PDF"));
        Assert.True(PrinterService.IsVirtualPrinter("Microsoft XPS Document Writer"));
        Assert.False(PrinterService.IsVirtualPrinter("Canon LBP226"));
        Assert.False(PrinterService.IsVirtualPrinter("HP LaserJet P1102"));
    }

    [Fact]
    public void PdfOutputPath_IsSameFolderSameName()
    {
        var job = new PrintJob
        {
            FilePath = @"C:\tmp\08 2026 royafood Nhiet ke chi thi hien so 07912.xls",
            FileName = "08 2026 royafood Nhiet ke chi thi hien so 07912.xls",
            Format = "XLS",
            Config = new PrintConfig { PrinterName = "Microsoft Print to PDF" },
        };
        var path = PrinterService.PdfOutputPath(job);
        Assert.Equal(@"C:\tmp\08 2026 royafood Nhiet ke chi thi hien so 07912.pdf", path);

        // Máy vật lý → null (in bình thường, không xuất file)
        var phys = new PrintJob
        {
            FilePath = @"C:\tmp\x.xls",
            FileName = "x.xls",
            Format = "XLS",
            Config = new PrintConfig { PrinterName = "Canon LBP226" },
        };
        Assert.Null(PrinterService.PdfOutputPath(phys));
    }
}
