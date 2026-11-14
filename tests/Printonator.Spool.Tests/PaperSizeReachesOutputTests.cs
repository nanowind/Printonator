using Printonator.Core.Models;
using Printonator.Spool.Printing;

namespace Printonator.Spool.Tests;

/// <summary>
/// Mắt xích cuối của chuỗi fix khổ giấy: khổ đã đúng lúc đặt <c>PageSetup</c> (kiểm ở
/// OfficeRealPaperTests/OfficeRealExcelPaperTests), nhưng chưa ai chứng minh khổ đó ĐI RA ĐẦU RA.
/// Test này in thật qua engine browser ra máy in ảo, rồi ĐO khổ trang trong file PDF kết quả.
///
/// Đây là lớp duy nhất bắt được lỗi "cấu hình đúng nhưng CSS @page sai" — ví dụ quên đổi đơn vị,
/// hoặc `preferCSSPageSize` không được bật nên Chromium lấy khổ mặc định Letter/A4.
/// </summary>
public class PaperSizeReachesOutputTests
{
    /// <summary>In một file TXT qua engine browser ra "Microsoft Print to PDF", rồi đọc lại khổ
    /// trang đầu của PDF xuất ra. 1 DIP = 1/96 inch → mm = dip × 25.4 / 96.</summary>
    private static async Task<(double Wmm, double Hmm)> PrintAndMeasureAsync(string paperSize)
    {
        var dir = Path.Combine(Path.GetTempPath(), "printonator-paper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var src = Path.Combine(dir, "trang.txt");
            await File.WriteAllTextAsync(src, "Printonator — kiểm khổ giấy đầu ra.");

            var job = new PrintJob
            {
                FilePath = src,
                FileName = Path.GetFileName(src),
                Format = "TXT",
                Config = new PrintConfig
                {
                    PrinterName = "Microsoft Print to PDF",
                    PaperSize = paperSize,
                    Orientation = PrintOrientation.Portrait,
                },
            };

            var r = await new BrowserPrintEngine().PrintAsync(job, CancellationToken.None);
            Assert.True(r.IsSuccess,
                r.IsSuccess ? "" : $"in lỗi: {r.Error?.Message} {r.Error?.Hint} {r.Error?.Detail}");

            var outPdf = Path.Combine(dir, "trang.pdf");
            Assert.True(File.Exists(outPdf), $"Không thấy PDF xuất ra: {outPdf}");

            // Đo khổ trang thật trong file kết quả (không hỏi lại config — config mới là thứ có thể sai).
            var pages = await WindowsPdfRasterizer.RenderPagesAsync(
                outPdf, [1], CancellationToken.None, renderDpi: 72);
            Assert.True(pages.IsSuccess, pages.IsSuccess ? "" : $"không đọc lại được PDF: {pages.Error?.Message}");
            var p = pages.Value![0];
            return (p.WidthDip * 25.4 / 96.0, p.HeightDip * 25.4 / 96.0);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>Sai số 5 mm: PDF dùng point nguyên nên làm tròn vài phần mười mm, nhưng lệch khổ
    /// thật (A4 vs A3) là 87 mm — 5 mm đủ chặt để bắt sai mà không bắt nhầm làm tròn.</summary>
    private static void AssertMm(double actual, double expected, string what)
        => Assert.True(Math.Abs(actual - expected) < 5.0,
            $"{what}: đầu ra {actual:F1} mm, mong đợi {expected:F1} mm");

    /// <summary>
    /// Ba khổ phải cho BA kích thước KHÁC NHAU. Nếu cấu hình khổ bị bỏ qua ở đường browser, cả ba
    /// sẽ ra cùng một khổ (A4) và các test riêng lẻ ở trên vẫn xanh vì A4 là khổ mặc định.
    /// Test này bắt đúng kiểu hỏng đó.
    /// </summary>
    [Fact]
    public async Task Ba_kho_khac_nhau_ra_ba_kich_thuoc_khac_nhau()
    {
        var a4 = await PrintAndMeasureAsync("A4");
        var a3 = await PrintAndMeasureAsync("A3");
        var legal = await PrintAndMeasureAsync("Legal");

        Assert.True(Math.Abs(a3.Wmm - a4.Wmm) > 40,
            $"A3 ({a3.Wmm:F1} mm) phải rộng hơn A4 ({a4.Wmm:F1} mm) rõ rệt");
        // Legal và A4 cùng rộng ~216/210 nhưng CAO khác hẳn (356 vs 297).
        Assert.True(Math.Abs(legal.Hmm - a4.Hmm) > 20,
            $"Legal ({legal.Hmm:F1} mm) phải cao hơn A4 ({a4.Hmm:F1} mm) rõ rệt");
    }

    [Fact]
    public async Task In_kho_A3_ra_PDF_dung_kho_A3()
    {
        var (w, h) = await PrintAndMeasureAsync("A3");

        AssertMm(w, 297, "A3 rộng");
        AssertMm(h, 420, "A3 cao");
    }

    [Fact]
    public async Task In_kho_A4_ra_PDF_dung_kho_A4()
    {
        var (w, h) = await PrintAndMeasureAsync("A4");

        AssertMm(w, 210, "A4 rộng");
        AssertMm(h, 297, "A4 cao");
    }

    /// <summary>Legal ≠ A4 ở chiều cao (356 vs 297 mm) — nếu cấu hình khổ bị bỏ qua, test này đỏ
    /// trong khi test A4 vẫn xanh (A4 là khổ mặc định của phần lớn driver).</summary>
    [Fact]
    public async Task In_kho_Legal_ra_PDF_dung_kho_Legal()
    {
        var (w, h) = await PrintAndMeasureAsync("Legal");

        AssertMm(w, 216, "Legal rộng");
        AssertMm(h, 356, "Legal cao");
    }
}
