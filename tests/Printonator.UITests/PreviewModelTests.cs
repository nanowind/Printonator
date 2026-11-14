using Printonator.Core.Models;
using Printonator.UI;
using Xunit;

namespace Printonator.UITests;

/// <summary>
/// PrintPreviewModel là model THUẦN (không WPF, không I/O) nên test chạy không cần STA/Application —
/// đây là phần duy nhất của preview kiểm chứng được tự động.
/// </summary>
public class PreviewModelTests
{
    private static PrintJob MakeJob(PrintConfig cfg, int pageCount) => new()
    {
        FilePath = "C:\\test.pdf",
        FileName = "test.pdf",
        Format = "PDF",
        Config = cfg,
        PageCount = pageCount,
    };

    [Fact]
    public void Kho_giay_A4_doc_dung_ty_le()
    {
        var m = PrintPreviewModel.Build(null, new PrintConfig
        {
            PaperSize = "A4",
            Orientation = PrintOrientation.Portrait,
        });

        Assert.Equal(210, m.SheetWidthMm);
        Assert.Equal(297, m.SheetHeightMm);
        Assert.False(m.PaperIsAssumed);
    }

    [Fact]
    public void Kho_giay_A3_ngang_doi_chieu()
    {
        var m = PrintPreviewModel.Build(null, new PrintConfig
        {
            PaperSize = "A3",
            Orientation = PrintOrientation.Landscape,
        });

        Assert.Equal(420, m.SheetWidthMm);
        Assert.Equal(297, m.SheetHeightMm);
        Assert.False(m.PaperIsAssumed);
    }

    [Fact]
    public void AsDocument_dung_A4_va_bao_la_uoc_luong()
    {
        var m = PrintPreviewModel.Build(null, new PrintConfig { PaperSize = PaperCatalog.AsDocument });

        Assert.True(m.PaperIsAssumed);
        Assert.Equal(210, m.SheetWidthMm);
        Assert.Equal(297, m.SheetHeightMm);
    }

    [Fact]
    public void Loc_le_chan_het_trang_la_LOI_khong_ve_to_gia()
    {
        var cfg = new PrintConfig { PageRange = "1,3", Parity = PageParityFilter.Even };
        var m = PrintPreviewModel.Build(MakeJob(cfg, 4), cfg);

        Assert.True(m.IsError);
        Assert.False(string.IsNullOrWhiteSpace(m.ErrorMessage));
        Assert.Empty(m.Sheets);
    }

    [Fact]
    public void So_trang_chua_biet_hien_mot_to_dau_hoi()
    {
        var cfg = new PrintConfig { PageRange = "All" };
        var m = PrintPreviewModel.Build(MakeJob(cfg, 0), cfg);

        Assert.True(m.IsUnknownPageCount);
        Assert.False(m.IsError);
        Assert.Single(m.Sheets);
        Assert.Single(m.Sheets[0].Thumbs);
        Assert.Null(m.Sheets[0].Thumbs[0].PageNumber);
        Assert.Equal(0, m.SkippedCount);
    }

    [Fact]
    public void Nup_chia_trang_theo_so_trang_moi_to()
    {
        var cfg = new PrintConfig { PageRange = "All", PagesPerSheet = 4 };
        var m = PrintPreviewModel.Build(MakeJob(cfg, 6), cfg);

        Assert.False(m.IsError, m.ErrorMessage);
        Assert.Equal(2, m.Sheets.Count);
        Assert.Equal(4, m.Sheets[0].Thumbs.Count);
        Assert.Equal(2, m.Sheets[1].Thumbs.Count);
        Assert.Equal(5, m.Sheets[1].Thumbs[0].PageNumber);
    }

    [Theory]
    [InlineData(PrintDuplexMode.LongEdge, PrintOrientation.Portrait, false)]
    [InlineData(PrintDuplexMode.LongEdge, PrintOrientation.Landscape, true)]
    [InlineData(PrintDuplexMode.ShortEdge, PrintOrientation.Portrait, true)]
    [InlineData(PrintDuplexMode.ShortEdge, PrintOrientation.Landscape, false)]
    public void Canh_lat_khop_voi_huong_giay(PrintDuplexMode duplex, PrintOrientation orientation, bool expectHorizontal)
    {
        var m = PrintPreviewModel.Build(null, new PrintConfig
        {
            PaperSize = "A4",
            DuplexMode = duplex,
            Orientation = orientation,
        });

        Assert.True(m.BackSideFlipped);
        Assert.Equal(expectHorizontal, m.FlipEdgeHorizontal);
    }

    [Fact]
    public void Simplex_khong_co_lat_mat_sau()
    {
        var m = PrintPreviewModel.Build(null, new PrintConfig
        {
            PaperSize = "A4",
            DuplexMode = PrintDuplexMode.Simplex,
            Orientation = PrintOrientation.Portrait,
        });

        Assert.False(m.BackSideFlipped);
        Assert.False(m.FlipEdgeHorizontal);
    }

    [Fact]
    public void So_ban_va_so_trang_bi_bo()
    {
        var cfg = new PrintConfig { PageRange = "All", Parity = PageParityFilter.Odd, Copies = 3 };
        var m = PrintPreviewModel.Build(MakeJob(cfg, 10), cfg);

        Assert.False(m.IsError, m.ErrorMessage);
        Assert.Equal(3, m.Copies);
        Assert.Equal(5, m.SkippedCount);
    }

    /// <summary>Chỉ GdiPrintEngine (PDF) đọc PagesPerSheet — DOCX/ảnh ở engine khác không ghép trang,
    /// nên preview phải vẽ 1 trang/tờ chứ không hứa "3 tờ" cho 12 trang 4-up.</summary>
    [Fact]
    public void Nup_chi_hop_le_voi_PDF_dinh_dang_khac_ve_1_trang_moi_to()
    {
        var cfg = new PrintConfig { PageRange = "All", PagesPerSheet = 4 };
        var docx = new PrintJob
        {
            FilePath = "C:\\test.docx",
            FileName = "test.docx",
            Format = "DOCX",
            Config = cfg,
            PageCount = 12,
        };

        var m = PrintPreviewModel.Build(docx, cfg);

        Assert.False(m.IsError, m.ErrorMessage);
        Assert.False(m.PagesPerSheetHonoured);
        Assert.Equal(12, m.Sheets.Count);
        Assert.Single(m.Sheets[0].Thumbs);
    }

    /// <summary>PageCount 0 + range "1,3" + lọc chẵn: short-circuit phải chạy TRƯỚC resolver —
    /// resolver coi file 0 trang là 1 trang nên sẽ fail oan ("không còn trang nào").</summary>
    [Fact]
    public void File_chua_biet_so_trang_khong_bao_loi_du_loc_le_chan()
    {
        var cfg = new PrintConfig { PageRange = "1,3", Parity = PageParityFilter.Even };
        var m = PrintPreviewModel.Build(MakeJob(cfg, 0), cfg);

        Assert.True(m.IsUnknownPageCount);
        Assert.False(m.IsError);
        Assert.Null(m.ErrorMessage);
    }

    [Fact]
    public void Build_khong_sua_config_duoc_dua_vao()
    {
        var cfg = new PrintConfig
        {
            PageRange = "2-5,7",
            Parity = PageParityFilter.Odd,
            PagesPerSheet = 4,
            Copies = 7,
            PaperSize = "A3",
            Orientation = PrintOrientation.Landscape,
            DuplexMode = PrintDuplexMode.ShortEdge,
            ColorMode = PrintColorMode.Grayscale,
            Booklet = false,
        };
        var before = Snapshot(cfg);

        PrintPreviewModel.Build(MakeJob(cfg, 12), cfg);

        Assert.Equal(before, Snapshot(cfg));
    }

    private static string Snapshot(PrintConfig c) => string.Join("|",
        c.PageRange, c.Parity, c.PagesPerSheet, c.Copies, c.PaperSize, c.Orientation,
        c.DuplexMode, c.ColorMode, c.Booklet, c.Quality, c.ScaleMode, c.ScalePercent,
        c.SheetName, c.WatermarkText, c.MergeIntoOneFile);

    /// <summary>SkippedCount chỉ đếm phần bộ lọc lẻ/chẵn cắt — page range KHÔNG phải "trang bị bỏ"
    /// (nhãn UI nói rõ "theo bộ lọc lẻ/chẵn").</summary>
    [Fact]
    public void SkippedCount_khong_tinh_trang_ngoai_page_range()
    {
        var cfg = new PrintConfig { PageRange = "1-2", Parity = PageParityFilter.All };
        var m = PrintPreviewModel.Build(MakeJob(cfg, 10), cfg);

        Assert.False(m.IsError, m.ErrorMessage);
        Assert.Equal(2, m.Sheets.Count);
        Assert.Equal(0, m.SkippedCount);
    }
}
