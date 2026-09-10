using Printonator.Core;
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
    public async Task VirtualPdfPrinter_PdfSource_RendersNotCopies_KeepsOriginalUntouched()
    {
        var dir = Path.Combine(Path.GetTempPath(), "printonator-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Bước 1: TXT → in ra máy PDF ảo → tạo file PDF nguồn hợp lệ (browser render có sẵn)
            var txt = Path.Combine(dir, "nguon.txt");
            await File.WriteAllTextAsync(txt, "Printonator E2E — PDF source cho máy ảo phải RENDER (in chuẩn), không copy file gốc.");
            var txtJob = new PrintJob
            {
                FilePath = txt,
                FileName = "nguon.txt",
                Format = "TXT",
                Config = new PrintConfig { PrinterName = "Microsoft Print to PDF" },
            };
            var r0 = await new BrowserPrintEngine().PrintAsync(txtJob, CancellationToken.None);
            Assert.True(r0.IsSuccess, r0.IsSuccess ? "" : $"bước 1 lỗi: {r0.Error?.Message} {r0.Error?.Hint}");
            var srcPdf = Path.Combine(dir, "nguon.pdf");
            Assert.True(File.Exists(srcPdf), $"Không tạo được PDF nguồn: {srcPdf}");

            // Bước 2: PDF nguồn → GdiPrintEngine + máy ảo → phải render (in chuẩn), KHÔNG copy thẳng
            var pdfJob = new PrintJob
            {
                FilePath = srcPdf,
                FileName = "nguon.pdf",
                Format = "PDF",
                Config = new PrintConfig { PrinterName = "Microsoft Print to PDF", PageRange = "1" },
            };
            var r = await new GdiPrintEngine().PrintAsync(pdfJob, CancellationToken.None);
            Assert.True(r.IsSuccess,
                r.IsSuccess ? "" : $"print lỗi: {r.Error?.Message} {r.Error?.Hint} {r.Error?.Detail}");

            // Output = file PDF KHÁC (PdfOutputPath thêm suffix _printonator cho nguồn .pdf)
            var expected = Path.Combine(dir, "nguon_printonator.pdf");
            Assert.True(File.Exists(expected), $"Không thấy PDF xuất ra: {expected}");
            Assert.True(File.Exists(srcPdf), "File nguồn không được đè/mất");
            Assert.True(new FileInfo(expected).Length > 0, "PDF xuất ra bị RỖNG");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void FitToPaper_LandscapePage_OnPortraitPaper_ShrinksWithoutRotating()
    {
        // Ảnh 200x100 (trang ngang) in ra tờ DỌC: canvas 100x200, nội dung KHÔNG xoay (lề trắng trên/dưới).
        using var bmp = new System.Drawing.Bitmap(200, 100);
        using (var g = System.Drawing.Graphics.FromImage(bmp)) g.Clear(System.Drawing.Color.Red);
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        var page = new RenderedPdfPage(ms.ToArray(), 200, 100);

        var fitted = WindowsPdfRasterizer.FitToPaper(page, targetLandscape: false);

        Assert.Equal(100, fitted.WidthDip);
        Assert.Equal(200, fitted.HeightDip);
        using var outBmp = new System.Drawing.Bitmap(new MemoryStream(fitted.Png));
        Assert.Equal(100, outBmp.Width);
        Assert.Equal(200, outBmp.Height);
        Assert.Equal(System.Drawing.Color.White.R, outBmp.GetPixel(50, 5).R);   // lề trên
        Assert.Equal(System.Drawing.Color.Red.R, outBmp.GetPixel(50, 100).R);   // nội dung canh giữa
    }

    [Fact]
    public void FitToPaper_SameOrientation_ReturnsUnchanged()
    {
        var page = new RenderedPdfPage([137, 80, 78, 71], 200, 100);
        Assert.Same(page, WindowsPdfRasterizer.FitToPaper(page, targetLandscape: true));
    }

    [Fact]
    public async Task VirtualPdfPrinter_OutputLocked_ReturnsCleanError_NoThrow()
    {
        var dir = Path.Combine(Path.GetTempPath(), "printonator-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Bước 1: TXT → máy PDF ảo → có PDF nguồn thật để in tiếp
            var txt = Path.Combine(dir, "khoa.txt");
            await File.WriteAllTextAsync(txt, "Printonator E2E — file xuat PDF bi khoa phai bao loi ro, khong crash.");
            var txtJob = new PrintJob
            {
                FilePath = txt,
                FileName = "khoa.txt",
                Format = "TXT",
                Config = new PrintConfig { PrinterName = "Microsoft Print to PDF" },
            };
            var r0 = await new BrowserPrintEngine().PrintAsync(txtJob, CancellationToken.None);
            Assert.True(r0.IsSuccess, r0.IsSuccess ? "" : $"bước 1 lỗi: {r0.Error?.Message} {r0.Error?.Hint}");
            var srcPdf = Path.Combine(dir, "khoa.pdf");
            Assert.True(File.Exists(srcPdf), $"Không tạo được PDF nguồn: {srcPdf}");

            // Bước 2: giữ file xuất (khoa_printonator.pdf) đang mở, không share → engine không ghi đè được
            var locked = Path.Combine(dir, "khoa_printonator.pdf");
            await File.WriteAllTextAsync(locked, "dang mo");
            using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

            var job = new PrintJob
            {
                FilePath = srcPdf,
                FileName = "khoa.pdf",
                Format = "PDF",
                Config = new PrintConfig { PrinterName = "Microsoft Print to PDF", PageRange = "1" },
            };
            var r = await new GdiPrintEngine().PrintAsync(job, CancellationToken.None);

            // Phải là lỗi MỀM (PrintError) — không ném exception ra ngoài
            Assert.False(r.IsSuccess);
            Assert.Equal(ErrorCodes.SpoolerFailed, r.Error!.Code);
            Assert.Contains("Không lưu được PDF", r.Error.Message);
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
    public void ClassifyVirtual_ByPort_KhongNhamMayVatLyCoTenPDF()
    {
        // Máy VẬT LÝ có tên chứa "pdf"/"fax" + port thật (USB/IP/WSD) → PHẢI là vật lý (không in ra PDF)
        Assert.False(PrinterService.ClassifyVirtual("Kyocera ECOSYS M2545 PDF", "IP_192.168.1.50"));
        Assert.False(PrinterService.ClassifyVirtual("Hóa đơn FAX phòng kế toán", "USB001"));
        Assert.False(PrinterService.ClassifyVirtual("Canon LBP226", "WSD-632aba5a-b0bd-483f"));
        Assert.False(PrinterService.ClassifyVirtual("Printer Ne03", "Ne03:"));

        // Máy ẢO thật + port file → virtual (giữ tính năng xuất PDF)
        Assert.True(PrinterService.ClassifyVirtual("Microsoft Print to PDF", "PORTPROMPT:"));
        Assert.True(PrinterService.ClassifyVirtual("Send to OneNote", "napsport:"));
        Assert.True(PrinterService.ClassifyVirtual("Adobe PDF", @"Documents\*.pdf"));
        Assert.True(PrinterService.ClassifyVirtual("Microsoft Fax", "SHRFAX:"));

        // Port lạ (PDF-XChange custom) / không đọc được → giữ heuristic tên (không ép physical)
        Assert.True(PrinterService.ClassifyVirtual("PDF-XChange 5.0", "PDF-XChange5-ABBYY-FR15"));
        Assert.True(PrinterService.ClassifyVirtual("Microsoft Print to PDF", null));
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
