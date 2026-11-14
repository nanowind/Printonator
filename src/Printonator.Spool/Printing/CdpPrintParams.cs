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
    /// GIỮ NGUYÊN chữ ký cho caller cũ — đường IN JOB phải dùng
    /// <see cref="BuildPageRangesChecked"/> (range sai ở đây thành "in cả file" im lặng).
    /// </summary>
    public static string? BuildPageRanges(PrintJob job)
    {
        var r = BuildPageRangesChecked(job);
        return r.IsSuccess ? r.Value : null;
    }

    /// <summary>
    /// BẢN CHECKED của <see cref="BuildPageRanges"/> — dùng ở đường IN JOB (non-PDF qua browser).
    /// Ok(null)  = in toàn bộ (chưa probe được số trang, hoặc All + không lọc lẻ/chẵn).
    /// Ok(ranges)= chuỗi pageRanges CDP ("1-3, 5") đã áp range + lẻ/chẵn.
    /// Fail = range không đọc được / ngoài phạm vi file, HOẶC lọc lẻ/chẵn xong không còn trang nào.
    /// LƯU Ý: chỉ đúng khi ĐÃ có số trang. TXT/CSV không được probe (PageCountProber bỏ qua) → PageCount
    /// &lt;= 0 → Ok(null) ở trên, TRƯỚC khi parse range → range trên TXT/CSV vẫn bị BỎ QUA im lặng (in cả file).
    // ponytail: trần = range/lẻ-chẵn trên TXT/CSV là best-effort "in hết"; nâng cấp = probe số trang bằng
    // cách render TXT/CSV ra PDF trước (PageCountProber đếm từ PDF đó), rồi đường checked này chạy như PDF.
    /// </summary>
    public static Result<string?> BuildPageRangesChecked(PrintJob job)
    {
        var r = ResolveSelectedPagesChecked(job);
        if (!r.IsSuccess) return Result<string?>.Fail(r.Error!);
        return Result<string?>.Ok(r.Value is { Length: > 0 } pages ? CompactRanges(pages) : null);
    }

    /// <summary>
    /// Danh sách trang vật lý user muốn in (1-based), dựa trên Config.PageRange + PageCount đã probe,
    /// SAU KHI áp bộ lọc trang lẻ/chẵn (Parity — PC: Print odd or even pages).
    /// null = in toàn bộ KHÔNG cần lọc (All + không lẻ/chẵn / chưa biết số trang / range lỗi).
    /// GIỮ NGUYÊN chữ ký cho caller cũ — đường IN JOB phải dùng
    /// <see cref="ResolveSelectedPagesChecked"/> (range sai ở đây thành "in cả file" im lặng).
    /// </summary>
    public static int[]? ResolveSelectedPages(PrintJob job)
    {
        var r = ResolveSelectedPagesChecked(job);
        return r.IsSuccess ? r.Value : null;
    }

    /// <summary>
    /// BẢN CHECKED của <see cref="ResolveSelectedPages"/> — dùng ở đường IN JOB.
    /// Ok(null)  = in toàn bộ, KHÔNG cần lọc (chưa probe được số trang, hoặc All + không lẻ/chẵn).
    /// Ok(pages) = danh sách trang vật lý sẽ in (đã áp bộ lọc lẻ/chẵn).
    /// Fail = range không đọc được / ngoài phạm vi file, HOẶC lọc lẻ/chẵn xong không còn trang nào.
    /// Trước đây cả 3 trường hợp cùng trả null → "in cả file" im lặng: PDF 3 trang + range "1" + lọc
    /// chẵn vẫn in trang 1; range "1-20" trên file 10 trang vẫn in cả file, job Done.
    /// </summary>
    public static Result<int[]?> ResolveSelectedPagesChecked(PrintJob job)
    {
        var isAll = string.IsNullOrWhiteSpace(job.Config.PageRange)
                    || job.Config.PageRange.Equals("All", StringComparison.OrdinalIgnoreCase);

        // Chưa probe được số trang → không biết trang nào là lẻ/chẵn, không có range để tính → in hết.
        if (job.PageCount <= 0) return Result<int[]?>.Ok(null);
        // All + không lọc lẻ/chẵn → in hết, không cần range (browser tự quyết)
        if (isAll && job.Config.Parity == PageParityFilter.All) return Result<int[]?>.Ok(null);

        int[] pages;
        if (isAll)
        {
            pages = Enumerable.Range(1, job.PageCount).ToArray();
        }
        else
        {
            var r = job.ResolvePhysicalPages();
            if (!r.IsSuccess) return Result<int[]?>.Fail(r.Error!); // range sai → LỖI, không in cả file
            pages = r.Value ?? [];
        }

        if (job.Config.Parity == PageParityFilter.Odd) pages = pages.Where(n => n % 2 == 1).ToArray();
        else if (job.Config.Parity == PageParityFilter.Even) pages = pages.Where(n => n % 2 == 0).ToArray();

        if (pages.Length > 0) return Result<int[]?>.Ok(pages);

        // Lọc hết sạch → LỖI (giống PrintJob.ResolvePrintPages), không rơi về "in cả file".
        var filterText = job.Config.Parity == PageParityFilter.Even ? "trang chẵn" : "trang lẻ";
        var rangeText = isAll ? "All" : job.Config.PageRange;
        return Result<int[]?>.Fail(new PrintError
        {
            Code = ErrorCodes.InvalidPageRange,
            Category = PrintErrorCategory.Config,
            Message = $"Không còn trang nào để in: phạm vi \"{rangeText}\" lọc {filterText} xong không còn trang phù hợp.",
            Hint = $"Bỏ bộ lọc lẻ/chẵn, hoặc chọn phạm vi trang khác (file có {job.PageCount} trang).",
        });
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
    /// landscape = true → tờ NGANG (rộng &gt; cao), false → tờ DỌC. Ảnh đã được thu nhỏ vừa khổ tờ
    /// (WindowsPdfRasterizer.FitToPaper) nên KHÔNG set cờ landscape của CDP — truyền thẳng khổ
    /// giấy theo inch, tránh bị swap 2 lần thành sai chiều.
    /// </summary>
    public static Dictionary<string, object?> BuildForSlicedImages(double widthInches, double heightInches, bool landscape = false)
    {
        // Ép đúng chiều user chọn: ngang → W &gt; H, dọc → H &gt; W (trang nguồn có thể ngược lại).
        var (w, h) = landscape
            ? (Math.Max(widthInches, heightInches), Math.Min(widthInches, heightInches))
            : (Math.Min(widthInches, heightInches), Math.Max(widthInches, heightInches));
        var p = new Dictionary<string, object?>
        {
            ["landscape"] = false,
            ["displayHeaderFooter"] = false,
            ["printBackground"] = true,
            ["scale"] = 1.0,
            ["preferCSSPageSize"] = false,
            ["paperWidth"] = Math.Round(w, 3),
            ["paperHeight"] = Math.Round(h, 3),
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