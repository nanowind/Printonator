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
    public void SummaryText_Shows_Collate_Color_PagesPerSheet()
    {
        // Collate gom bản
        var coll = new PrintConfig { Collation = PrintCollation.ByDocuments };
        Assert.Contains("gom bản", coll.SummaryText);

        // Collate rời bản
        var uncoll = new PrintConfig { Collation = PrintCollation.ByPages };
        Assert.Contains("rời bản", uncoll.SummaryText);

        // "2 mặt theo máy" / "màu theo máy" khi driver quyết (phân biệt từng option), pages-per-sheet luôn hiện
        var def = new PrintConfig(); // mặc định: ColorMode=AsPrinter, DuplexMode=AsPrinter, PagesPerSheet=1
        Assert.Contains("2 mặt theo máy", def.SummaryText);   // duplex theo driver (có nhãn)
        Assert.Contains("màu theo máy", def.SummaryText);     // màu theo driver (có nhãn)
        Assert.Contains("1-tr/tờ", def.SummaryText);          // pages-per-sheet luôn hiện
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

    [Fact]
    public void DuplexMode_Default_Is_AsPrinter()
    {
        var cfg = new PrintConfig();
        Assert.Equal(PrintDuplexMode.AsPrinter, cfg.DuplexMode);
        Assert.False(cfg.Duplex); // AsPrinter không tính là "2 mặt ép"
    }

    [Fact]
    public void Duplex_Bool_RoundTrips_To_DuplexMode()
    {
        var cfg = new PrintConfig();

        cfg.Duplex = true;
        Assert.Equal(PrintDuplexMode.LongEdge, cfg.DuplexMode);
        Assert.True(cfg.Duplex);

        cfg.Duplex = false;
        Assert.Equal(PrintDuplexMode.Simplex, cfg.DuplexMode); // giữ hành vi cũ "mặc định 1 mặt"
        Assert.False(cfg.Duplex);

        cfg.DuplexMode = PrintDuplexMode.ShortEdge;
        Assert.True(cfg.Duplex); // ShortEdge vẫn là "2 mặt"

        cfg.DuplexMode = PrintDuplexMode.AsPrinter;
        Assert.False(cfg.Duplex); // AsPrinter không tính là "2 mặt ép"
    }

    [Fact]
    public void CopyInto_Preserves_DuplexMode_Enum()
    {
        var src = new PrintConfig { DuplexMode = PrintDuplexMode.ShortEdge };
        var target = new PrintConfig { Duplex = true }; // mặc định khác → phải bị ghi đè hết
        src.CopyInto(target);

        Assert.Equal(PrintDuplexMode.ShortEdge, target.DuplexMode); // không mất chiều lật qua shim bool
        Assert.True(target.Duplex);
    }

    [Fact]
    public void SummaryText_Shows_ShortEdge()
    {
        var cfg = new PrintConfig { DuplexMode = PrintDuplexMode.ShortEdge };
        Assert.Contains("2 mặt — lật cạnh ngắn", cfg.SummaryText);
    }

    [Fact]
    public void Preset_RoundTrip_Keeps_DuplexMode()
    {
        var path = Path.Combine(Path.GetTempPath(), $"printonator-preset-{Guid.NewGuid():N}.json");
        try
        {
            var store = new PresetStore(path);
            var preset = new Preset
            {
                Name = "Lịch 2 mặt",
                Copies = 1,
                DuplexMode = PrintDuplexMode.ShortEdge,
            };
            Assert.True(store.Save(preset));

            var loaded = store.Load().Single(p => p.Name == "Lịch 2 mặt");
            Assert.Equal(PrintDuplexMode.ShortEdge, loaded.DuplexMode);
            // Enum là nguồn sự thật; bool Duplex chỉ là shim legacy (không set trong preset này → false)
            Assert.False(loaded.Duplex);

            var cfg = loaded.ToPrintConfig();
            Assert.Equal(PrintDuplexMode.ShortEdge, cfg.DuplexMode);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Preset_Legacy_BoolOnly_Loads_As_LongEdge()
    {
        var path = Path.Combine(Path.GetTempPath(), $"printonator-preset-{Guid.NewGuid():N}.json");
        try
        {
            // JSON cũ (trước khi có trường DuplexMode): chỉ có "Duplex": true
            File.WriteAllText(path, """[{"Name":"Hợp đồng cũ","Copies":1,"Duplex":true,"PaperSize":"A4"}]""");
            var store = new PresetStore(path);
            var preset = store.Load().Single(p => p.Name == "Hợp đồng cũ");

            Assert.True(preset.Duplex);
            Assert.Equal(PrintDuplexMode.AsPrinter, preset.DuplexMode); // trường mới khuyết → mặc định

            var cfg = preset.ToPrintConfig();
            Assert.Equal(PrintDuplexMode.LongEdge, cfg.DuplexMode); // bool cũ → LongEdge (giữ hành vi cũ "2 mặt")
            Assert.True(cfg.Duplex);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Preset_AsPrinter_Profile_Stays_AsPrinter()
    {
        // Regression Major #1: profile "Theo máy in" (legacy JSON chỉ có "Duplex":false, không có enum)
        // KHÔNG được ép thành "1 mặt" (Simplex) khi áp vào job — enum phải giữ AsPrinter, driver quyết lúc in.
        var preset = new Preset
        {
            Name = "Theo máy in",
            Duplex = false, // legacy: bool false, không có DuplexMode trong JSON
        };
        var cfg = preset.ToPrintConfig();
        Assert.Equal(PrintDuplexMode.AsPrinter, cfg.DuplexMode);
        Assert.False(cfg.Duplex); // shim: AsPrinter không tính là "2 mặt ép"
    }
}