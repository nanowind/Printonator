using Printonator.Core.Models;

namespace Printonator.UI;

/// <summary>Một trang thu nhỏ trên tờ preview. PageNumber = null khi chưa biết số trang file.</summary>
public sealed record PreviewThumb(int? PageNumber, bool Kept);

/// <summary>Một tờ giấy preview (1 tờ = PagesPerSheet trang).</summary>
public sealed record PreviewSheet(int Index, IReadOnlyList<PreviewThumb> Thumbs);

/// <summary>
/// Dữ liệu thuần cho cửa sổ xem trước: KHÔNG WPF type, không I/O, không truy cập máy in —
/// nên dựng tức thì và test được bằng unit test. Cửa sổ chỉ bind vào model này.
/// </summary>
public sealed record PrintPreviewModel
{
    public bool IsError { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>Gợi ý sửa lỗi (PrintError.Hint) — phần nói user PHẢI LÀM GÌ, UI hiện kèm Message.</summary>
    public string? ErrorHint { get; init; }

    /// <summary>Chưa biết số trang file (job null hoặc PageCount &lt;= 0) — KHÔNG phải lỗi, chỉ là chưa đo được.</summary>
    public bool IsUnknownPageCount { get; init; }

    public double SheetWidthMm { get; init; }
    public double SheetHeightMm { get; init; }

    /// <summary>Khổ giấy là phỏng đoán (AsDocument/AsPrinter/không có trong danh mục) — UI ghi "ước lượng".</summary>
    public bool PaperIsAssumed { get; init; }

    public int PagesPerSheet { get; init; } = 1;

    // ponytail: chỉ GdiPrintEngine (PDF) đọc PagesPerSheet; Office COM và browser không có imposition nào.
    // Trần thật: N-up cho DOCX/XLSX/ảnh phải do engine dựng (imposition) — lúc đó mới bỏ cờ này.
    // false = preview VẼ 1 trang/tờ (không ghép) và caption ghi rõ "chỉ áp dụng cho PDF".
    public bool PagesPerSheetHonoured { get; init; } = true;

    public int Copies { get; init; } = 1;
    public bool BackSideFlipped { get; init; }     // in 2 mặt
    public bool FlipEdgeHorizontal { get; init; }
    public bool Grayscale { get; init; }
    public int SkippedCount { get; init; }
    public IReadOnlyList<PreviewSheet> Sheets { get; init; } = [];

    /// <summary>Dựng preview từ job (có thể null = chưa chọn file) và cấu hình DRAFT đang sửa trên form.</summary>
    public static PrintPreviewModel Build(PrintJob? job, PrintConfig cfg)
    {
        // 1) Khổ giấy: không biết khổ → A4 và đánh dấu là ước lượng.
        var dims = PaperCatalog.Dimensions(cfg.PaperSize);
        var (w, h) = dims ?? (210, 297);

        // 2) Chiều: Landscape đổi chỗ rộng/cao. AsDocument/AsPrinter giữ nguyên (không đoán được).
        var landscape = cfg.Orientation == PrintOrientation.Landscape;
        if (landscape) (w, h) = (h, w);

        // 3) số trang/tờ — Booklet luôn 2 trang/tờ khi user chưa chọn N-up.
        var perSheet = Math.Max(cfg.PagesPerSheet, 1);
        if (cfg.Booklet && perSheet <= 1) perSheet = 2;

        // 6) Lật mặt sau: chỉ khi duplex thật; AsPrinter = không rõ → không khẳng định có lật.
        var flipped = cfg.DuplexMode is PrintDuplexMode.LongEdge or PrintDuplexMode.ShortEdge;

        var model = new PrintPreviewModel
        {
            SheetWidthMm = w,
            SheetHeightMm = h,
            PaperIsAssumed = dims is null,
            PagesPerSheet = perSheet,
            // PDF là trường hợp tốt nhất (GdiPrintEngine đọc PagesPerSheet); định dạng khác thì bỏ qua.
            PagesPerSheetHonoured = perSheet == 1 || job is null || job.Format == "PDF",
            Copies = Math.Max(cfg.Copies, 1),
            Grayscale = cfg.ColorMode is PrintColorMode.Grayscale,
            BackSideFlipped = flipped,
            // Cạnh dài là cạnh DỌC trên tờ dọc, NGANG trên tờ ngang → lật cạnh dài trên tờ ngang là lật ngang.
            FlipEdgeHorizontal = flipped && ((cfg.DuplexMode == PrintDuplexMode.LongEdge) == landscape),
        };

        // 7) Chưa biết số trang thì KHÔNG gọi resolver: PageCount 0 → nó trả [1] (bịa) và có thể fail oan với Even.
        if (job is null || job.PageCount <= 0)
            return model with
            {
                IsUnknownPageCount = true,
                Sheets = [new PreviewSheet(1, [new PreviewThumb(null, true)])],
            };

        // 8) Range + parity do PrintJob quyết — preview không tự parse lại (fail-closed như lúc in thật).
        var r = job.ResolvePrintPages(cfg);
        if (!r.IsSuccess)
            return model with { IsError = true, ErrorMessage = r.Error!.Message, ErrorHint = r.Error.Hint };

        var kept = r.Value!;
        // Số trang bị bỏ = CHỈ do bộ lọc lẻ/chẵn (không tính phần range tự cắt) — nhãn UI nói
        // "theo bộ lọc lẻ/chẵn", nên range "1-2" trên file 10 trang KHÔNG được tính là 8 trang bị bỏ.
        var physical = job.ResolvePhysicalPages(cfg);
        var paritySkipped = physical.IsSuccess ? Math.Max(physical.Value!.Length - kept.Length, 0) : 0;

        // N-up chỉ có thật với PDF → định dạng khác vẽ 1 trang/tờ (xem PagesPerSheetHonoured).
        var chunk = model.PagesPerSheetHonoured ? perSheet : 1;
        return model with
        {
            SkippedCount = paritySkipped,
            Sheets = kept.Chunk(chunk)
                .Select((c, i) => new PreviewSheet(i + 1, c.Select(p => new PreviewThumb(p, true)).ToList()))
                .ToList(),
        };
    }
}
