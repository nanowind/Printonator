using System.IO;
using Printonator.Core.Models;
using Printonator.Spool.Printing;
using Xunit;

namespace Printonator.UITests;

/// <summary>
/// Unit test ENGINE RENDER bằng trình duyệt (KHÔNG bundle — dò Edge/Chrome trên máy):
/// locator, map PrintConfig → CDP printToPDF params, CanHandle. Deterministic: inject override path.
/// (Luồng E2E bật browser thật được verify thủ công qua probe — test tự động chỉ test phần logic thuần.)
/// </summary>
public class BrowserEngineTests
{
    [Fact]
    public void Locator_WithOverrideFile_ReturnsIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"printonator-br-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, "fake-edge.exe");
            File.WriteAllBytes(exe, [0x4D, 0x5A]);
            var locator = new BrowserLocator(() => exe);
            var hit = locator.ResolveBrowser();
            Assert.NotNull(hit);
            Assert.Equal(exe, hit!.Value.Path);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void Locator_WithMissingOverride_FallsBack_OrNull_NoThrow()
    {
        var locator = new BrowserLocator(() => Path.Combine(Path.GetTempPath(), "không-có.exe"));
        // Máy này gần như chắc chắn có Edge/Chrome → không throw, hoặc trả về browser thật.
        var hit = locator.ResolveBrowser();
        _ = hit; // chỉ cần không crash
    }

    [Fact]
    public void CompactRanges_GroupsConsecutivePages()
    {
        Assert.Equal("1-3, 5, 8-9", CdpPrintParams.CompactRanges([3, 1, 2, 5, 8, 9]));
        Assert.Equal("1", CdpPrintParams.CompactRanges([1]));
        Assert.Equal("1-5", CdpPrintParams.CompactRanges([5, 4, 3, 2, 1]));
        Assert.Equal("", CdpPrintParams.CompactRanges([]));
    }

    [Fact]
    public void BuildPageRanges_AllOrUnknown_ReturnsNull()
    {
        var cfg = new PrintConfig { PageRange = "All" };
        var job = MakeJob(cfg, 10);
        Assert.Null(CdpPrintParams.BuildPageRanges(job));

        cfg.PageRange = "1-3";
        job.PageCount = 0; // chưa probe
        Assert.Null(CdpPrintParams.BuildPageRanges(job));
    }

    [Fact]
    public void ResolveSelectedPages_Applies_ParityFilter()
    {
        // All + Odd → trang 1,3,5,7,9
        var odd = MakeJob(new PrintConfig { PageRange = "All", Parity = PageParityFilter.Odd }, 10);
        Assert.Equal("1, 3, 5, 7, 9", CdpPrintParams.CompactRanges(CdpPrintParams.ResolveSelectedPages(odd)!));

        // Range 1-6 + Even → 2,4,6
        var even = MakeJob(new PrintConfig { PageRange = "1-6", Parity = PageParityFilter.Even }, 10);
        Assert.Equal("2, 4, 6", CdpPrintParams.CompactRanges(CdpPrintParams.ResolveSelectedPages(even)!));

        // All + All (không lọc) → null (in hết, không cần range)
        var all = MakeJob(new PrintConfig { PageRange = "All", Parity = PageParityFilter.All }, 10);
        Assert.Null(CdpPrintParams.ResolveSelectedPages(all));
    }

    [Fact]
    public void DpiFor_MapsQuality()
    {
        Assert.Equal((int)200, CdpPrintParams.DpiFor(PrintQuality.High));
        Assert.Equal((int)150, CdpPrintParams.DpiFor(PrintQuality.Medium));
        Assert.Equal((int)150, CdpPrintParams.DpiFor(PrintQuality.AsPrinter));
        Assert.Equal((int)100, CdpPrintParams.DpiFor(PrintQuality.Low));
        Assert.Equal((int)75, CdpPrintParams.DpiFor(PrintQuality.Draft));
    }

    [Fact]
    public void BuildParams_PaperAsDocument_UsesCssPageSize()
    {
        var p = CdpPrintParams.Build(new PrintConfig { PaperSize = PaperCatalog.AsDocument }, null);
        Assert.Equal(true, p["preferCSSPageSize"]);
        Assert.False(p.ContainsKey("paperWidth"));  // không ép khổ — dùng khổ gốc từng trang
        Assert.False(p.ContainsKey("paperHeight"));
    }

    [Fact]
    public void BuildPageRanges_SpecificRange_ReturnsCompact()
    {
        var cfg = new PrintConfig { PageRange = "2-4,7" };
        var job = MakeJob(cfg, 10);
        Assert.Equal("2-4, 7", CdpPrintParams.BuildPageRanges(job));
    }

    [Fact]
    public void BuildParams_MapsConfig_ToCdp()
    {
        var cfg = new PrintConfig
        {
            PaperSize = "A4",           // 210 x 297 mm
            Orientation = PrintOrientation.Landscape,
            ScaleMode = PrintScaleMode.Zoom,
            ScalePercent = 130,
            Copies = 2,
        };
        var p = CdpPrintParams.Build(cfg, "1-3");

        Assert.Equal(true, p["landscape"]);
        Assert.Equal(1.3, p["scale"]);
        Assert.Equal(false, p["displayHeaderFooter"]);
        Assert.Equal(true, p["printBackground"]);
        Assert.Equal("1-3", p["pageRanges"]);
        Assert.Equal((double)210 / 25.4, (double)p["paperWidth"]!, 3);
        Assert.Equal((double)297 / 25.4, (double)p["paperHeight"]!, 3);
        Assert.Equal(0.4, (double)p["marginTop"]!);
    }

    [Fact]
    public void BuildParams_Fill_ZeroMargins()
    {
        var p = CdpPrintParams.Build(new PrintConfig { ScaleMode = PrintScaleMode.Fill }, null);
        Assert.Equal(0.0, (double)p["marginTop"]!);
        Assert.Equal(0.0, (double)p["marginLeft"]!);
    }

    [Fact]
    public void ScaleFor_MapsModes()
    {
        Assert.Equal(1.0, CdpPrintParams.ScaleFor(new PrintConfig { ScaleMode = PrintScaleMode.Original }));
        Assert.Equal(0.8, CdpPrintParams.ScaleFor(new PrintConfig { ScaleMode = PrintScaleMode.ShrinkToPrintable }));
        var zoom = CdpPrintParams.ScaleFor(new PrintConfig { ScaleMode = PrintScaleMode.Zoom, ScalePercent = 50 });
        Assert.Equal(0.5, zoom);
        Assert.Equal(1.5, CdpPrintParams.ScaleFor(new PrintConfig { ScaleMode = PrintScaleMode.Zoom, ScalePercent = 400 })); // clamp zoom-in
    }

    [Fact]
    public void Engine_CanHandle_BrowserFormats_WhenBrowserFound()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"printonator-br2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, "edge.exe");
            File.WriteAllBytes(exe, [0x4D, 0x5A]);
            var engine = new BrowserPrintEngine(() => (Name: "FakeEdge", Path: exe));

            Assert.True(engine.CanHandle("PDF"));
            Assert.True(engine.CanHandle("png"));
            Assert.True(engine.CanHandle("TXT"));
            Assert.False(engine.CanHandle("DOCX"));  // office để engine office xử lý
            Assert.False(engine.CanHandle("XLSX"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void Engine_CanHandle_False_WhenNoBrowser()
    {
        var engine = new BrowserPrintEngine(() => null);
        Assert.False(engine.CanHandle("PDF"));
        Assert.False(engine.CanHandle("PNG"));
    }

    [Fact]
    public void NeedsRender_PdfSubsetRange_True_Others_False()
    {
        var pdf = MakeJob(new PrintConfig { PageRange = "2-4" }, 10);
        Assert.True(BrowserPrintEngine.NeedsBrowserRender(pdf)); // PDF + range → slice

        var pdfAll = MakeJob(new PrintConfig(), 10);
        Assert.False(BrowserPrintEngine.NeedsBrowserRender(pdfAll)); // PDF + All + A4/dọc → shell

        var pdfA3 = MakeJob(new PrintConfig { PaperSize = "A3" }, 10);
        Assert.True(BrowserPrintEngine.NeedsBrowserRender(pdfA3)); // khổ khác A4 → render

        var pdfLand = MakeJob(new PrintConfig { Orientation = PrintOrientation.Landscape }, 10);
        Assert.True(BrowserPrintEngine.NeedsBrowserRender(pdfLand)); // ngang → render
    }

    // ===== PDF slicing: Windows.Data.Pdf (built-in) + browser assembly =====

    [Fact]
    public void BuildHtml_OneImagePerSlice_PageBreakBetween()
    {
        // PNG header 8 bytes chuẩn: 89 50 4E 47 0D 0A 1A 0A → base64 "iVBORw0KGgo"
        var one = WindowsPdfRasterizer.BuildHtml([new RenderedPdfPage([137, 80, 78, 71, 13, 10, 26, 10], 794, 1123)]);
        Assert.Contains("data:image/png;base64,iVBORw0KGgo", one);
        Assert.DoesNotContain("page-break-after", one);

        var two = WindowsPdfRasterizer.BuildHtml([
            new RenderedPdfPage([0x1], 794, 1123),
            new RenderedPdfPage([0x2], 794, 1123),
        ]);
        Assert.Equal(1, CountOccurrences(two, "page-break-after:always"));
        Assert.Equal(2, CountOccurrences(two, "<img"));

        var fromBytes = WindowsPdfRasterizer.BuildHtml([new RenderedPdfPage([137, 80, 78, 71], 794, 1123)]);
        Assert.Contains("data:image/png;base64,", fromBytes);
    }

    [Fact]
    public void BuildForSlicedImages_ZeroMargins_OriginalSize()
    {
        var p = CdpPrintParams.BuildForSlicedImages(8.27, 11.69); // A4 inch
        Assert.Equal(8.27, (double)p["paperWidth"]!);
        Assert.Equal(11.69, (double)p["paperHeight"]!);
        Assert.Equal(0.0, (double)p["marginTop"]!);
        Assert.Equal(0.0, (double)p["marginBottom"]!);
        Assert.Equal(1.0, (double)p["scale"]!);
        Assert.Equal(false, p["displayHeaderFooter"]);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0; var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    private static PrintJob MakeJob(PrintConfig cfg, int pageCount) => new()
    {
        FilePath = "C:\\probe.pdf",
        FileName = "probe.pdf",
        Format = "PDF",
        Config = cfg,
        PageCount = pageCount,
    };
}