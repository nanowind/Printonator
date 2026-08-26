using Printonator.Core.Models;
using Printonator.Core.Presets;
using Xunit;

namespace Printonator.Core.Tests;

/// <summary>
/// Unit test các trường cấu hình in mở rộng theo Print Conductor:
/// ColorMode (as printer/document/color/grayscale), PaperSource (khay),
/// ScaleMode, PagesPerSheet/booklet, Collation, ProfileName + mapping Preset.
/// </summary>
public class PrintConfigTests
{
    [Fact]
    public void Defaults_Are_Neutral_AsInPrinter_AsInDocument()
    {
        var cfg = new PrintConfig();
        Assert.Equal(PrintColorMode.AsPrinter, cfg.ColorMode);
        Assert.Equal(PrintScaleMode.AsDocument, cfg.ScaleMode);
        Assert.Null(cfg.PaperSource);
        Assert.Equal(1, cfg.PagesPerSheet);
        Assert.False(cfg.Booklet);
        Assert.Equal(PrintCollation.AsPrinter, cfg.Collation);
        Assert.Equal(PageParityFilter.All, cfg.Parity);
        Assert.Equal(PrintQuality.AsPrinter, cfg.Quality);
        Assert.Null(cfg.ProfileName);
        // Color bool tương thích cũ: mặc định → false
        Assert.False(cfg.Color);
    }

    [Fact]
    public void SummaryText_Shows_Parity_Quality_And_KhoGoc()
    {
        var cfg = new PrintConfig
        {
            Parity = PageParityFilter.Odd,
            Quality = PrintQuality.Draft,
            PaperSize = PaperCatalog.AsDocument,
        };
        var s = cfg.SummaryText;
        Assert.Contains("khổ gốc", s);
        Assert.Contains("trang lẻ", s);
        Assert.Contains("res:Draft", s);
    }

    [Fact]
    public void Color_Bool_Maps_To_ColorMode()
    {
        var cfg = new PrintConfig();
        cfg.Color = true;
        Assert.Equal(PrintColorMode.Color, cfg.ColorMode);
        Assert.True(cfg.Color);

        cfg.Color = false;
        Assert.Equal(PrintColorMode.Grayscale, cfg.ColorMode); // giữ hành vi cũ "mặc định B&W"
        Assert.False(cfg.Color);

        cfg.ColorMode = PrintColorMode.AsDocument;
        Assert.False(cfg.Color); // AsDocument không tính là "màu ép"
    }

    [Fact]
    public void Clone_Copies_All_NewFields()
    {
        var cfg = new PrintConfig
        {
            ColorMode = PrintColorMode.Grayscale,
            PaperSource = "Khay 1",
            ScaleMode = PrintScaleMode.FitToPrintable,
            ScalePercent = 85,
            PagesPerSheet = 4,
            Booklet = false,
            Collation = PrintCollation.ByDocuments,
            Parity = PageParityFilter.Odd,
            Quality = PrintQuality.High,
            ProfileName = "Nháp tiết kiệm",
        };
        var copy = cfg.Clone();

        Assert.NotSame(cfg, copy);
        Assert.Equal(PrintColorMode.Grayscale, copy.ColorMode);
        Assert.Equal("Khay 1", copy.PaperSource);
        Assert.Equal(PrintScaleMode.FitToPrintable, copy.ScaleMode);
        Assert.Equal(85, copy.ScalePercent);
        Assert.Equal(4, copy.PagesPerSheet);
        Assert.Equal(PrintCollation.ByDocuments, copy.Collation);
        Assert.Equal(PageParityFilter.Odd, copy.Parity);
        Assert.Equal(PrintQuality.High, copy.Quality);
        Assert.Equal("Nháp tiết kiệm", copy.ProfileName);
    }

    [Fact]
    public void CopyInto_Overwrites_Target()
    {
        var src = new PrintConfig
        {
            ColorMode = PrintColorMode.Color,
            PaperSource = "Nạp tay (Manual)",
            ScaleMode = PrintScaleMode.Zoom,
            ScalePercent = 130,
            PagesPerSheet = 2,
            Collation = PrintCollation.ByPages,
            PageRange = "1-3",
        };
        var target = new PrintConfig { Copies = 99, PaperSize = "A3", ColorMode = PrintColorMode.AsDocument };
        src.CopyInto(target);

        Assert.Equal(PrintColorMode.Color, target.ColorMode);
        Assert.Equal("Nạp tay (Manual)", target.PaperSource);
        Assert.Equal(PrintScaleMode.Zoom, target.ScaleMode);
        Assert.Equal(130, target.ScalePercent);
        Assert.Equal(2, target.PagesPerSheet);
        Assert.Equal(PrintCollation.ByPages, target.Collation);
        Assert.Equal("1-3", target.PageRange);
        Assert.Equal(1, target.Copies); // CopyInto ghi đè TOÀN BỘ — Copies của src (1)
        Assert.Equal("A4", target.PaperSize); // PaperSize của src (mặc định A4)
    }

    [Fact]
    public void SummaryText_Shows_NonDefault_Options()
    {
        var cfg = new PrintConfig
        {
            Copies = 2,
            PaperSize = "A4",
            Duplex = true,
            ColorMode = PrintColorMode.Grayscale,
        };
        var s = cfg.SummaryText;
        Assert.Contains("2x", s);
        Assert.Contains("A4", s);
        Assert.Contains("2 mặt", s);
        Assert.Contains("B&W", s);

        var cfg2 = new PrintConfig { PagesPerSheet = 4, ScaleMode = PrintScaleMode.Zoom, ScalePercent = 120 };
        var s2 = cfg2.SummaryText;
        Assert.Contains("4-tr/tờ", s2);
        Assert.Contains("zoom 120%", s2);
    }

    [Fact]
    public void Preset_RoundTrip_Preserves_NewFields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"printonator-preset-{Guid.NewGuid():N}.json");
        try
        {
            var store = new PresetStore(path);
            var preset = new Preset
            {
                Name = "Hợp đồng tiết kiệm",
                Copies = 2,
                Duplex = true,
                PaperSize = "A4",
                ColorMode = PrintColorMode.Grayscale,
                PaperSource = "Khay 1",
                ScaleMode = PrintScaleMode.ShrinkToPrintable,
                PagesPerSheet = 2,
                Collation = PrintCollation.ByDocuments,
                Parity = PageParityFilter.Even,
                Quality = PrintQuality.Draft,
            };
            Assert.True(store.Save(preset));

            var loaded = store.Load().Single(p => p.Name == "Hợp đồng tiết kiệm");
            Assert.Equal(PrintColorMode.Grayscale, loaded.ColorMode);
            Assert.Equal("Khay 1", loaded.PaperSource);
            Assert.Equal(PrintScaleMode.ShrinkToPrintable, loaded.ScaleMode);
            Assert.Equal(2, loaded.PagesPerSheet);
            Assert.Equal(PrintCollation.ByDocuments, loaded.Collation);
            Assert.Equal(PageParityFilter.Even, loaded.Parity);
            Assert.Equal(PrintQuality.Draft, loaded.Quality);

            // ToPrintConfig + ProfileName gắn tên preset
            var cfg = loaded.ToPrintConfig();
            Assert.Equal("Khay 1", cfg.PaperSource);
            Assert.Equal(PrintColorMode.Grayscale, cfg.ColorMode);
            Assert.Equal(PageParityFilter.Even, cfg.Parity);
            Assert.Equal(PrintQuality.Draft, cfg.Quality);
            Assert.Equal("Hợp đồng tiết kiệm", cfg.ProfileName);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}