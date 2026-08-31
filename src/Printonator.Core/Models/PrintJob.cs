namespace Printonator.Core.Models;

/// <summary>Trạng thái một job in — theo đúng state machine trong concept.</summary>
public enum JobState
{
    Queued,
    AwaitingApproval,   // job từ AI (MCP) chờ người duyệt — chỉ duyệt qua ApproveJob
    Converting,
    Spooling,
    Done,
    Error,
    Cancelled,
}

/// <summary>Nguồn tạo job — phân biệt job người dùng tạo với job AI gửi qua MCP (cần duyệt).</summary>
public enum JobSource
{
    User,
    Mcp,
    WatchFolder,
}

/// <summary>Chiều giấy in — có thêm "theo file / theo máy" như Print Conductor (không ép chiều).</summary>
public enum PrintOrientation
{
    Portrait,
    Landscape,

    /// <summary>Giữ chiều trong file (As in document) — engine KHÔNG ép PageSetup.Orientation.</summary>
    AsDocument,

    /// <summary>Giữ chiều driver máy in (As in printer) — engine KHÔNG ép PageSetup.Orientation.</summary>
    AsPrinter,
}

/// <summary>
/// Chế độ màu in — theo Print Conductor (As in printer / As in document / Color / Grayscale).
/// "As in printer" = để driver máy in quyết; "As in document" = theo nội dung file.
/// </summary>
public enum PrintColorMode
{
    AsPrinter,     // dùng cài đặt hiện tại của driver máy in (Printing Preferences)
    AsDocument,    // theo nội dung/document
    Color,         // ép màu
    Grayscale,     // ép đen trắng (tiết kiệm mực)
}

/// <summary>
/// Chế độ 2 mặt (duplex) — theo Print Conductor: As in printer / Simplex / Long-edge / Short-edge.
/// "As in printer" = để driver máy in quyết; LongEdge lật cạnh dài (in sách),
/// ShortEdge lật cạnh ngắn (in lịch/bảng tháng).
/// </summary>
public enum PrintDuplexMode
{
    AsPrinter,   // dùng cài đặt hiện tại của driver máy in (Printing Preferences)
    Simplex,     // in 1 mặt
    LongEdge,    // in 2 mặt — lật cạnh dài
    ShortEdge,   // in 2 mặt — lật cạnh ngắn
}

/// <summary>
/// Cách scale trang khi in lên giấy — theo Print Conductor:
/// Shrink (thu nhỏ trang lớn cho vừa vùng in) / Fit (co+giãn vừa vùng in) /
/// Original (nguyên cỡ) / Fill (lấp đầy tờ) / Zoom (phần trăm tùy chỉnh).
/// </summary>
public enum PrintScaleMode
{
    AsDocument,        // giữ nguyên cài đặt scale trong file (mặc định)
    ShrinkToPrintable, // chỉ thu nhỏ trang lớn hơn vùng in
    FitToPrintable,    // co/giãn mọi trang cho vừa vùng in được
    Original,          // in nguyên cỡ 100%
    Fill,              // lấp đầy cả tờ giấy (có thể tràn lề)
    Zoom,              // zoom theo phần trăm tùy chỉnh (ScalePercent)
}

/// <summary>Kiểu gom bản khi in nhiều bản (collation) — theo Print Conductor.</summary>
public enum PrintCollation
{
    AsPrinter,    // theo driver máy in
    ByDocuments,  // gom từng bộ tài liệu (bản 1,2,3 của doc 1 rồi doc 2...)
    ByPages,      // gom theo trang (trang 1,1,1 rồi trang 2,2,2...)
}

/// <summary>Chỉ in một nửa các trang (PC: Print odd or even pages) — engine render áp được, shell in bình thường.</summary>
public enum PageParityFilter
{
    All,   // in tất cả trang
    Odd,   // chỉ trang lẻ: 1,3,5...
    Even,  // chỉ trang chẵn: 2,4,6...
}

/// <summary>Độ phân giải in (PC: Printer resolution) — AsPrinter để driver quyết; còn lại dùng cho rasterize ảnh/PDF.</summary>
public enum PrintQuality
{
    AsPrinter,  // driver quyết (mặc định)
    High,       // ~200dpi
    Medium,     // ~150dpi
    Low,        // ~100dpi
    Draft,      // ~75dpi (mực in tiết kiệm)
}

/// <summary>Cấu hình in cho 1 file (hỗ trợ cả page-range nhiều kiểu).</summary>
public sealed class PrintConfig
{
    public int Copies { get; set; } = 1;

    /// <summary>Chế độ 2 mặt — mặc định As in printer = driver máy in quyết.</summary>
    public PrintDuplexMode DuplexMode { get; set; } = PrintDuplexMode.AsPrinter;

    /// <summary>
    /// Tương thích cũ: MCP/UI cũ gán bool Duplex. Đọc: true khi in 2 mặt (LongEdge/ShortEdge);
    /// gán: true → LongEdge, false → Simplex (giữ hành vi cũ "mặc định 1 mặt").
    /// </summary>
    public bool Duplex
    {
        get => DuplexMode is PrintDuplexMode.LongEdge or PrintDuplexMode.ShortEdge;
        set => DuplexMode = value ? PrintDuplexMode.LongEdge : PrintDuplexMode.Simplex;
    }

    // Mặc định "theo tài liệu" (AsDocument) = giữ khổ giấy SẴN CÓ trong file — KHÔNG ép A4.
    // (ApplyExcelSetup/ApplyWordPaper bỏ qua AsDocument; ép A4 đè cấu hình in của file — v0.1.6 bug)
    public string PaperSize { get; set; } = PaperCatalog.AsDocument;

    /// <summary>Chế độ màu (mặc định As in printer = driver quyết).</summary>
    public PrintColorMode ColorMode { get; set; } = PrintColorMode.AsPrinter;

    /// <summary>
    /// Tương thích cũ: MCP/UI cũ gán bool Color. Đọc: true khi ColorMode=Color;
    /// gán: true → Color, false → Grayscale (giữ hành vi cũ "mặc định B&W").
    /// </summary>
    public bool Color
    {
        get => ColorMode == PrintColorMode.Color;
        set => ColorMode = value ? PrintColorMode.Color : PrintColorMode.Grayscale;
    }

    public PrintOrientation Orientation { get; set; } = PrintOrientation.AsDocument;   // giữ chiều theo file
    public string? PrinterName { get; set; }         // null = dùng máy in mặc định

    /// <summary>
    /// Page range do người dùng nhập: "All", "2,5", "3-4", "1-2,7", hoặc rỗng = All.
    /// Với DOCX có section: "S2:1-3" = section 2 trang 1-3 (app tự map sang trang vật lý).
    /// </summary>
    public string PageRange { get; set; } = "All";

    /// <summary>Tên sheet Excel cần in (null/rỗng = in TẤT CẢ sheet). Dùng cho file Excel nhiều sheet —
    /// user chọn sheet qua dropdown trong Print Settings; range áp cho sheet đã chọn.</summary>
    public string? SheetName { get; set; }

    /// <summary>Excel: fit tất cả cột vào 1 trang ngang (PageSetup.FitToPagesWide=1) — bảng rộng không bị cắt cột.</summary>
    public bool FitToPageWide { get; set; }

    /// <summary>Excel: tự chọn chiều giấy theo nội dung (vùng dữ liệu rộng hơn cao → landscape, ngược lại portrait).</summary>
    public bool AutoOrientation { get; set; }

    /// <summary>Nguồn giấy (khay) — null/"" = As in printer (driver tự chọn khay). Tên khay lấy từ máy in.</summary>
    public string? PaperSource { get; set; }

    /// <summary>Kiểu scale khi in (mặc định As in document = giữ cài đặt file).</summary>
    public PrintScaleMode ScaleMode { get; set; } = PrintScaleMode.AsDocument;

    /// <summary>Phần trăm zoom — dùng khi ScaleMode == Zoom (vd 130 = 130%).</summary>
    public int ScalePercent { get; set; } = 100;

    /// <summary>Số trang trên mỗi tờ (N-up): 1 = không gom; 2/4/6/9/16 = gom trang.</summary>
    public int PagesPerSheet { get; set; } = 1;

    /// <summary>In dạng booklet (2 trang/tờ, gấp sách) — cần máy in duplex 2 mặt.</summary>
    public bool Booklet { get; set; }

    /// <summary>Kiểu gom bản khi nhiều copies.</summary>
    public PrintCollation Collation { get; set; } = PrintCollation.AsPrinter;

    /// <summary>Chỉ in trang lẻ/chẵn (PC: Print odd or even pages) — engine render lọc được, shell để nguyên.</summary>
    public PageParityFilter Parity { get; set; } = PageParityFilter.All;

    /// <summary>Độ phân giải in — AsPrinter = driver quyết (mặc định); còn lại đổi chất lượng rasterize.</summary>
    public PrintQuality Quality { get; set; } = PrintQuality.AsPrinter;

    /// <summary>Tên profile (preset) đang áp cho file — chỉ để hiển thị/ghi chú, không đổi hành vi.</summary>
    public string? ProfileName { get; set; }

    /// <summary>In thêm 1 trang bìa trước lô (ghi tên lô, ngày, số lượng file).</summary>
    public bool CoverPage { get; set; }

    /// <summary>Gộp toàn bộ file được chọn thành 1 bản in (chỉ PDF/ảnh/TXT).</summary>
    public bool MergeIntoOneFile { get; set; }

    /// <summary>Chữ dấu mờ in trên mỗi trang — null/rỗng = không watermark.</summary>
    public string? WatermarkText { get; set; }

    /// <summary>Độ mờ của dấu mờ (0.1–1.0).</summary>
    public double WatermarkOpacity { get; set; } = 0.3;

    /// <summary>Vị trí dấu mờ: center/top-left/top-right/bottom-left/bottom-right.</summary>
    public string WatermarkPosition { get; set; } = "center";

    public PrintConfig Clone() => (PrintConfig)MemberwiseClone();

    /// <summary>Copy toàn bộ giá trị sang config khác (UI/MCP dùng khi áp cấu hình cho job).</summary>
    public void CopyInto(PrintConfig target)
    {
        target.Copies = Copies;
        target.DuplexMode = DuplexMode;   // copy thẳng enum (không qua shim bool — giữ được ShortEdge/AsPrinter)
        target.PaperSize = PaperSize;
        target.ColorMode = ColorMode;
        target.Orientation = Orientation;
        target.PrinterName = PrinterName;
        target.PageRange = PageRange;
        target.PaperSource = PaperSource;
        target.ScaleMode = ScaleMode;
        target.ScalePercent = ScalePercent;
        target.PagesPerSheet = PagesPerSheet;
        target.Booklet = Booklet;
        target.Collation = Collation;
        target.Parity = Parity;
        target.Quality = Quality;
        target.ProfileName = ProfileName;
        target.SheetName = SheetName;
        target.FitToPageWide = FitToPageWide;
        target.AutoOrientation = AutoOrientation;
        target.CoverPage = CoverPage;
        target.MergeIntoOneFile = MergeIntoOneFile;
        target.WatermarkText = WatermarkText;
        target.WatermarkOpacity = WatermarkOpacity;
        target.WatermarkPosition = WatermarkPosition;
    }

    /// <summary>Chuỗi ngắn gọn mô tả cấu hình khác biệt so với mặc định (cho cột Settings).</summary>
    public string SummaryText
    {
        get
        {
            var paper = PaperSize == PaperCatalog.AsDocument ? "khổ gốc" : PaperSize;
            var parts = new List<string> { $"{Math.Max(Copies, 1)}x", paper };

            // Chế độ in — hiện rõ cả "theo máy" NHƯNG kèm nhãn để phân biệt với màu theo máy
            switch (DuplexMode)
            {
                case PrintDuplexMode.LongEdge:
                    parts.Add("2 mặt");   // giữ nguyên chuỗi cũ — test SummaryText đang assert "2 mặt"
                    break;
                case PrintDuplexMode.ShortEdge:
                    parts.Add("2 mặt — lật cạnh ngắn");
                    break;
                case PrintDuplexMode.Simplex:
                    parts.Add("1 mặt");
                    break;
                case PrintDuplexMode.AsPrinter:
                default:
                    parts.Add("2 mặt theo máy"); // driver quyết 1/2 mặt — ghi rõ đó là mục duplex
                    break;
            }

            // Màu — hiện rõ cả "theo máy", kèm nhãn "màu"
            parts.Add(ColorMode switch
            {
                PrintColorMode.Color => "Màu",
                PrintColorMode.Grayscale => "B&W",
                _ => "màu theo máy", // AsPrinter/AsDocument
            });

            // Gom bản (collate) — được yêu cầu; ẩn "theo driver" (mặc định)
            if (Collation == PrintCollation.ByDocuments) parts.Add("gom bản");
            else if (Collation == PrintCollation.ByPages) parts.Add("rời bản");

            if (Parity == PageParityFilter.Odd) parts.Add("trang lẻ");
            else if (Parity == PageParityFilter.Even) parts.Add("trang chẵn");
            if (Quality != PrintQuality.AsPrinter) parts.Add($"res:{Quality}");
            if (!string.IsNullOrEmpty(PaperSource))
                parts.Add($"khay: {PaperSource}");
            if (Booklet) parts.Add("booklet");
            else parts.Add($"{PagesPerSheet}-tr/tờ"); // luôn hiện số trang/tờ (1-tr/tờ cho đầy đủ)
            if (ScaleMode is not PrintScaleMode.AsDocument)
                parts.Add(ScaleMode == PrintScaleMode.Zoom ? $"zoom {ScalePercent}%" : ScaleMode switch
                {
                    PrintScaleMode.ShrinkToPrintable => "shrink",
                    PrintScaleMode.FitToPrintable => "fit",
                    PrintScaleMode.Original => "original",
                    PrintScaleMode.Fill => "fill",
                    _ => "scale",
                });
            if (CoverPage) parts.Add("bìa");
            if (MergeIntoOneFile) parts.Add("gộp");
            if (!string.IsNullOrEmpty(WatermarkText)) parts.Add("dấu mờ");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>Thông tin máy in — UI/MCP luôn hiện đúng trạng thái + khả năng của từng máy.</summary>
public sealed record PrinterInfo
{
    public required string Name { get; init; }
    public bool IsAvailable { get; init; }          // online / offline
    public string? StatusDetail { get; init; }      // vd "Offline", "Hết giấy", lỗi đọc
    public bool SupportsDuplex { get; init; }
    public bool SupportsColor { get; init; }
    public string[] SupportedPaperSizes { get; init; } = ["A4"];
    public string? TrayInfo { get; init; }

    /// <summary>Danh sách khay giấy (tên thân thiện) máy in hỗ trợ — cho UI chọn Paper source.</summary>
    public string[] Trays { get; init; } = [];
    public bool IsVirtual { get; init; }           // Microsoft Print to PDF...
    public bool IsDefault { get; init; }            // máy in đang là mặc định của Windows

    /// <summary>Dòng trạng thái hiển thị trong UI (không dùng trực tiếp StatusDetail — tự fallback).</summary>
    public string StatusText => StatusDetail ?? (IsAvailable ? "Sẵn sàng" : "Không khả dụng");

    /// <summary>Tóm tắt khả năng in cho UI/MCP: Duplex, Màu, Khay.</summary>
    public string CapabilitiesSummary =>
        $"Duplex: {(SupportsDuplex ? "có" : "không")} · Màu: {(SupportsColor ? "có" : "không")}" +
        (string.IsNullOrEmpty(TrayInfo) ? "" : $" · Khay: {TrayInfo}");

    /// <summary>Tóm tắt khổ giấy hỗ trợ (tối đa 8 khổ, tránh tràn UI).</summary>
    public string PaperSummary =>
        $"Giấy: {string.Join(", ", SupportedPaperSizes.Take(8))}" +
        (SupportedPaperSizes.Length > 8 ? ", …" : "");
}

/// <summary>
/// Một job in (1 file). Có section-map cho DOCX để giải quyết đúng vấn đề
/// "in trang 3 hóa ra là trang 1 section 2".
/// </summary>
public sealed class PrintJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required string Format { get; init; }        // PDF, DOCX, XLSX, PPTX, PNG...
    public required PrintConfig Config { get; init; }
    public JobSource Source { get; init; } = JobSource.User;   // MCP → cần duyệt (guard)
    public JobState State { get; internal set; } = JobState.Queued;

    /// <summary>Chuỗi hiển thị trạng thái cho UI (✓ khi Done) — tránh binding TwoWay vào thuộc tính read-only.</summary>
    public string StateText => State == JobState.Done ? "✓ Done" : State.ToString();

    // Thuộc tính phẳng cho sort theo cột "Settings" (ListCollectionView không resolve path lồng "Config.Copies")
    public int SortCopies => Config.Copies;
    public string SortPaper => Config.PaperSize;

    /// <summary>Thư mục chứa file — khóa nhóm UI "folder cha → file con" (group bằng đường dẫn đầy đủ).</summary>
    public string FolderGroup => string.IsNullOrEmpty(FilePath) ? "" : (Path.GetDirectoryName(FilePath) ?? "");

    /// <summary>Tên thư mục hiển thị (leaf name) — converter hiển thị thay FolderGroup.</summary>
    public string FolderLabel
    {
        get
        {
            var g = FolderGroup.TrimEnd('\\', '/');
            return string.IsNullOrEmpty(g) ? "File rời" : Path.GetFileName(g);
        }
    }
    public PrintError? Error { get; internal set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? StartedAt { get; internal set; }
    public DateTimeOffset? FinishedAt { get; internal set; }
    public int PageCount { get; set; }
    public bool WasReloaded { get; set; }              // file đã sửa qua double-click → nạp bản mới

    /// <summary>Job có máy in RIÊNG per-file (MCP/UI chỉ định) — ApplySelectedPrinter KHÔNG ghi đè máy này.</summary>
    public bool HasPerFilePrinter { get; set; }

    /// <summary>Bản đồ trang: section → (trang vật lý đầu, trang vật lý cuối). DOCX có section mới có.</summary>
    public List<SectionMap> Sections { get; } = new();

    /// <summary>
    /// Giải page-range (hỗ trợ section) thành danh sách trang VẬT LÝ để gửi xuống in.
    /// Trả về lỗi PrintError nếu range không hợp lệ.
    /// </summary>
    public Result<int[]> ResolvePhysicalPages()
    {
        var raw = string.IsNullOrWhiteSpace(Config.PageRange) ? "All" : Config.PageRange.Trim();

        if (raw.Equals("All", StringComparison.OrdinalIgnoreCase))
            return Result<int[]>.Ok(Enumerable.Range(1, Math.Max(PageCount, 1)).ToArray());

        // Section mode: "S2:1-3"
        if (raw.StartsWith("S", StringComparison.OrdinalIgnoreCase) && raw.Contains(':'))
        {
            var parts = raw.Split(':');
            if (!int.TryParse(parts[0].TrimStart('S', 's'), out var secIdx))
                return Result<int[]>.Fail(new PrintError
                {
                    Code = ErrorCodes.InvalidPageRange,
                    Category = PrintErrorCategory.Config,
                    Message = $"Không đọc được số section trong \"{raw}\".",
                    Hint = "Nhập đúng dạng: S2:1-3 (section 2, trang 1 đến 3)."
                });

            var sec = Sections.FirstOrDefault(s => s.Index == secIdx);
            if (sec is null)
                return Result<int[]>.Fail(new PrintError
                {
                    Code = ErrorCodes.SectionNotFound,
                    Category = PrintErrorCategory.Config,
                    Message = $"Không tìm thấy Section {secIdx} trong file.",
                    Hint = $"File này có {Sections.Count} section: {string.Join(", ", Sections.Select(s => $"S{s.Index}"))}."
                });

            // Section không hỗ trợ macro last/lastN — chỉ nhận số trang cụ thể
            if (parts[1].StartsWith("last", StringComparison.OrdinalIgnoreCase))
                return Result<int[]>.Fail(new PrintError
                {
                    Code = ErrorCodes.InvalidPageRange,
                    Category = PrintErrorCategory.Config,
                    Message = $"Macro \"{parts[1]}\" không dùng được trong section.",
                    Hint = $"Nhập số trang cụ thể: S{secIdx}:1-{sec.PageCount}."
                });

            return ParseRange(parts[1], 1, sec.PageCount,
                $"Section {secIdx} chỉ có {sec.PageCount} trang.")
                .Map(pageList => pageList.Select(p => p + sec.FirstPhysicalPage - 1).ToArray());
        }

        // Macro last / lastN: in trang cuối / N trang cuối (N>0). Cần biết số trang của file.
        if (raw.StartsWith("last", StringComparison.OrdinalIgnoreCase))
        {
            if (PageCount <= 0)
                return Result<int[]>.Fail(new PrintError
                {
                    Code = ErrorCodes.InvalidPageRange,
                    Category = PrintErrorCategory.Config,
                    Message = $"Không in được \"{raw}\": chưa biết số trang của file này.",
                    Hint = "Macro 'last' cần biết số trang — dùng 'All' hoặc nhập range số cụ thể."
                });
            var n = raw.Equals("last", StringComparison.OrdinalIgnoreCase) ? 1
                : int.TryParse(raw["last".Length..], out var v) && v > 0 ? v
                : -1;
            if (n <= 0)
                return Result<int[]>.Fail(new PrintError
                {
                    Code = ErrorCodes.InvalidPageRange,
                    Category = PrintErrorCategory.Config,
                    Message = $"Page range \"{raw}\" không hợp lệ.",
                    Hint = "Định dạng đúng: last (trang cuối) · lastN (N trang cuối) · All · 2,5 · 3-4 · S2:1-3."
                });
            var start = Math.Max(1, PageCount - n + 1);
            return Result<int[]>.Ok(Enumerable.Range(start, Math.Min(n, PageCount)).ToArray());
        }

        // Plain pages: 2,5 | 3-4 | 1-2,7
        return ParseRange(raw, 1, Math.Max(PageCount, 1), $"File có {PageCount} trang.");
    }

    /// <summary>Parse page range "All | 2,5 | 3-4 | 1-2,7" KHÔNG cần PageCount — cho engine in theo
    /// From/To per sheet (Excel/PowerPoint in theo TỪ-TỚI của từng sheet; app không đếm trang Excel/PowerPoint
    /// đáng tin — GET.DOCUMENT trả sai khi đổi sheet, queue mặc định PageCount=1 → ResolvePhysicalPages fail
    /// → range LUÔN bị bỏ qua in all). Lỗi định dạng → Fail.</summary>
    public static Result<int[]> ParsePageRange(string? spec)
    {
        var raw = string.IsNullOrWhiteSpace(spec) ? "All" : spec.Trim();
        if (raw.Equals("All", StringComparison.OrdinalIgnoreCase)) return Result<int[]>.Ok([]);
        return ParseRange(raw, 1, 10000, "Nhiều nhất 10000 trang.");
    }

    private static Result<int[]> ParseRange(string spec, int minPage, int maxPage, string boundHint)
    {
        var pages = new List<int>();
        try
        {
            foreach (var part in spec.Split(',', StringSplitOptions.TrimEntries))
            {
                if (part.Contains('-'))
                {
                    var b = part.Split('-');
                    if (b.Length != 2 || !int.TryParse(b[0], out var s) || !int.TryParse(b[1], out var e))
                        return Fail(spec, boundHint);
                    if (s > e) (s, e) = (e, s);
                    for (var p = s; p <= e; p++) pages.Add(p);
                }
                else if (int.TryParse(part, out var single))
                {
                    pages.Add(single);
                }
                else return Fail(spec, boundHint);
            }
        }
        catch { return Fail(spec, boundHint); }

        var bad = pages.Where(p => p < minPage || p > maxPage).ToList();
        if (bad.Any())
            return Result<int[]>.Fail(new PrintError
            {
                Code = ErrorCodes.InvalidPageRange,
                Category = PrintErrorCategory.Config,
                Message = $"Trang {string.Join(",", bad.Take(5))} nằm ngoài phạm vi ({minPage}-{maxPage}).",
                Hint = boundHint
            });
        return Result<int[]>.Ok(pages.Distinct().OrderBy(p => p).ToArray());
    }

    private static Result<int[]> Fail(string spec, string hint) =>
        Result<int[]>.Fail(new PrintError
        {
            Code = ErrorCodes.InvalidPageRange,
            Category = PrintErrorCategory.Config,
            Message = $"Page range \"{spec}\" không hợp lệ.",
            Hint = $"Định dạng đúng: All | 2,5 | 3-4 | 1-2,7 | S2:1-3. {hint}"
        });
}

/// <summary>Một section trong DOCX — ánh xạ giữa trang section và trang vật lý PDF.</summary>
public sealed record SectionMap
{
    public required int Index { get; init; }             // Section 1, 2, 3...
    public required int FirstPhysicalPage { get; init; }
    public required int LastPhysicalPage { get; init; }
    public int PageCount => LastPhysicalPage - FirstPhysicalPage + 1;
}

/// <summary>Kết quả trả về đơn giản: Ok(value) hoặc Fail(error).</summary>
public sealed record Result<T>
{
    public T? Value { get; init; }
    public PrintError? Error { get; init; }
    public bool IsSuccess => Error is null;
    public static Result<T> Ok(T value) => new() { Value = value };
    public static Result<T> Fail(PrintError error) => new() { Error = error };

    /// <summary>Biến đổi value nếu thành công, giữ nguyên error nếu thất bại.</summary>
    public Result<TOut> Map<TOut>(Func<T, TOut> fn) =>
        IsSuccess ? Result<TOut>.Ok(fn(Value!)) : Result<TOut>.Fail(Error!);
}