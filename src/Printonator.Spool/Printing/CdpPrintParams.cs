using System.Text.Json;
using Printonator.Core.Models;
using Printonator.Core.Printing;

namespace Printonator.Spool.Printing;

/// <summary>
/// Builder THUẦN (không I/O — dễ unit test): PrintConfig → params của CDP Page.printToPDF.
/// Chrome DevTools Protocol (đã verify tài liệu chính thức):
/// - pageRanges  "1-5, 8, 11-13" — in đúng các trang chọn lọc;
/// - scale       number (0-1 = 100%) — Shrink/Fit/Original/Zoom;
/// - paperWidth/paperHeight (inch), landscape, margin*, printBackground, displayHeaderFooter=false.
/// </summary>
public static class CdpPrintParams
{
    /// <summary>Khổ giấy mặc định khi user chọn "theo máy in" nhưng không lấy được khổ driver (A4 — chuẩn catte VN).</summary>
    public const string DefaultPaper = "A4";

    /// <summary>Nén danh sách trang rời rạc thành chuỗi range CDP: [1,2,3,5,8,9] → "1-3, 5, 8-9".</summary>
    public static string CompactRanges(IEnumerable<int> pages)
    {
        var sorted = pages.Distinct().OrderBy(p => p).ToArray();
        if (sorted.Length == 0) return "";
        return PageGrouping.CompactRanges(sorted);
    }

    /// <summary>
    /// Trang cần in → chuỗi pageRanges (chỉ dùng cho NON-PDF: PDF viewer từ chối ranges CDP).
    /// null = in toàn bộ.
    /// </summary>
    public static string? BuildPageRanges(PrintJob job)
    {
        if (job.PageCount <= 0) return null; // chưa probe được số trang → in hết
        if (string.IsNullOrWhiteSpace(job.Config.PageRange)
            || job.Config.PageRange.Equals("All", StringComparison.OrdinalIgnoreCase))
            return null;

        var r = ResolveSelectedPages(job);
        return r is { Length: > 0 } pages ? CompactRanges(pages) : null;
    }

    /// <summary>
    /// Danh sách trang vật lý user muốn in (1-based), dựa trên Config.PageRange + PageCount đã probe,
    /// SAU KHI áp bộ lọc trang lẻ/chẵn (Parity — PC: Print odd or even pages).
    /// null = in toàn bộ KHÔNG cần lọc (All + không lẻ/chẵn / chưa biết số trang / range lỗi).
    /// </summary>
    public static int[]? ResolveSelectedPages(PrintJob job)
    {
        if (job.PageCount <= 0) return null;
        var isAll = string.IsNullOrWhiteSpace(job.Config.PageRange)
                    || job.Config.PageRange.Equals("All", StringComparison.OrdinalIgnoreCase);

        int[] pages;
        if (isAll)
        {
            // All + không lọc lẻ/chẵn → in hết, không cần range (browser tự quyết)
            if (job.Config.Parity == PageParityFilter.All) return null;
            pages = Enumerable.Range(1, job.PageCount).ToArray();
        }
        else
        {
            var r = job.ResolvePhysicalPages();
            if (!r.IsSuccess || r.Value is not { Length: > 0 } p) return null;
            pages = p;
        }

        if (job.Config.Parity == PageParityFilter.Odd) pages = pages.Where(n => n % 2 == 1).ToArray();
        else if (job.Config.Parity == PageParityFilter.Even) pages = pages.Where(n => n % 2 == 0).ToArray();

        return pages.Length > 0 ? pages : null;
    }

    /// <summary>Chất lượng in → DPI rasterize (dùng cho WindowsPdfRasterizer khi không để driver quyết).</summary>
    public static int DpiFor(PrintQuality quality) => quality switch
    {
        PrintQuality.High => 200,
        PrintQuality.Low => 100,
        PrintQuality.Draft => 75,
        _ => 150, // AsPrinter / Medium
    };

    /// <summary>Scale (CDP 0-1, 1.0 = 100%) từ ScaleMode + ScalePercent; zoom-in cấp 1.5 tối đa (browser PDF viewer cấp ~1.5).</summary>
    public static double ScaleFor(PrintConfig cfg)
        => cfg.ScaleMode switch
        {
            PrintScaleMode.ShrinkToPrintable => 0.8,   // thu nhỏ mềm bảo toàn bố cục
            PrintScaleMode.FitToPrintable => 0.95,     // gần khít vùng in
            PrintScaleMode.Original => 1.0,
            PrintScaleMode.Fill => 1.0,                // fill không tỉ lệ trong Chromium → co cho vừa, margin 0 (xử lý ở engine)
            PrintScaleMode.Zoom => Math.Clamp(cfg.ScalePercent is >= 10 and <= 999 ? cfg.ScalePercent / 100.0 : 1.0, 0.1, 1.5),
            _ => 1.0,                                  // AsDocument / không biết → nguyên cỡ
        };

    /// <summary>Khổ giấy (mm trong catalog) → inch; không biết → null (browser dùng mặc định).</summary>
    public static (double W, double H)? PaperInches(string? paperName)
    {
        var dims = PaperCatalog.Dimensions(string.IsNullOrWhiteSpace(paperName) ? DefaultPaper : paperName);
        return dims is { } d ? (d.W / 25.4, d.H / 25.4) : null;
    }

    /// <summary>Dựng dictionary params cho Page.printToPDF (đã serialize sẵn — Thuận tiện test JSON).</summary>
    public static Dictionary<string, object?> Build(PrintConfig cfg, string? pageRanges)
    {
        var p = new Dictionary<string, object?>
        {
            ["landscape"] = cfg.Orientation == PrintOrientation.Landscape,
            ["displayHeaderFooter"] = false,               // in sạch, không số trang/địa chỉ
            ["printBackground"] = true,                    // giữ màu nền (bản vẽ/ảnh nền)
            ["scale"] = ScaleFor(cfg),
            ["preferCSSPageSize"] = false,                 // mặc định: ép khổ ta chọn, không theo CSS file
        };

        // "Theo tài liệu" (PC: Page size based) → để browser/PDF dùng khổ GỐC của từng trang file
        var asDocument = cfg.PaperSize == PaperCatalog.AsDocument;
        p["preferCSSPageSize"] = asDocument;

        if (!asDocument)
        {
            var inches = PaperInches(cfg.PaperSize);
            if (inches is { } sz)
            {
                p["paperWidth"] = Math.Round(sz.W, 3);
                p["paperHeight"] = Math.Round(sz.H, 3);
            }
        }

        var fill = cfg.ScaleMode == PrintScaleMode.Fill;
        var margin = fill ? 0.0 : 0.4; // Fill → in tràn lề
        p["marginTop"] = margin;
        p["marginBottom"] = margin;
        p["marginLeft"] = margin;
        p["marginRight"] = margin;

        if (!string.IsNullOrWhiteSpace(pageRanges)) p["pageRanges"] = pageRanges;
        return p;
    }

    /// <summary>
    /// Params để in HTML ẢNH đã cắt (mỗi trang 1 ảnh khớp khổ giấy gốc PDF):
    /// khổ giấy = inch từ DIPs/96 của trang PDF gốc, margin 0, scale 1 — ảnh lấp đầy tờ.
    /// </summary>
    public static Dictionary<string, object?> BuildForSlicedImages(double widthInches, double heightInches)
    {
        var p = new Dictionary<string, object?>
        {
            ["landscape"] = false,
            ["displayHeaderFooter"] = false,
            ["printBackground"] = true,
            ["scale"] = 1.0,
            ["preferCSSPageSize"] = false,
            ["paperWidth"] = Math.Round(widthInches, 3),
            ["paperHeight"] = Math.Round(heightInches, 3),
            ["marginTop"] = 0.0,
            ["marginBottom"] = 0.0,
            ["marginLeft"] = 0.0,
            ["marginRight"] = 0.0,
        };
        return p;
    }

    public static string Serialize(Dictionary<string, object?> p)
        => JsonSerializer.Serialize(p);
}