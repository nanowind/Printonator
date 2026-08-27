namespace Printonator.Core.Models;

/// <summary>
/// Preset = bộ cấu hình in có tên ("Hợp đồng 2 mặt", "Nháp 1 mặt"...).
/// Chính là "printer profile template" trong giao diện: chọn 1 preset là
/// nạp lại toàn bộ option (màu, khay giấy, scale, N-up, collation...).
/// Dùng cho UI (màn settings) và MCP (get_presets / print_with_preset).
/// Lưu dạng JSON cục bộ (PresetStore), không cloud.
/// </summary>
public sealed record Preset
{
    public required string Name { get; init; }
    public int Copies { get; init; } = 1;
    public bool Duplex { get; init; }

    /// <summary>
    /// Chế độ 2 mặt (mới). Lưu JSON dạng số như PrintColorMode (enum) — preset cũ
    /// chỉ có bool Duplex vẫn nạp được (khuyết → AsPrinter → map từ bool).
    /// </summary>
    public PrintDuplexMode DuplexMode { get; init; } = PrintDuplexMode.AsPrinter;
    public string PaperSize { get; init; } = "A4";
    public PrintColorMode ColorMode { get; init; } = PrintColorMode.AsPrinter;
    public string? PrinterName { get; init; }
    public string PageRange { get; init; } = "All";
    public string? PaperSource { get; init; }
    public PrintScaleMode ScaleMode { get; init; } = PrintScaleMode.AsDocument;
    public int ScalePercent { get; init; } = 100;
    public int PagesPerSheet { get; init; } = 1;
    public bool Booklet { get; init; }
    public PrintCollation Collation { get; init; } = PrintCollation.AsPrinter;
    public PageParityFilter Parity { get; init; } = PageParityFilter.All;
    public PrintQuality Quality { get; init; } = PrintQuality.AsPrinter;

    /// <summary>Clone cấu hình từ preset sang PrintConfig của một job.</summary>
    public PrintConfig ToPrintConfig() => new()
    {
        Copies = Copies,
        // Preset mới có enum rõ → giữ nguyên; legacy JSON chỉ có bool Duplex → true = LongEdge;
        // TRƯỜNG HỢP CÒN LẠI giữ AsPrinter ("theo máy in") — KHÔNG ép thành Simplex:
        // nếu không làm vậy, profile "Theo máy in" mở lại sẽ bị đổi thành "1 mặt" (Major #1 fix).
        DuplexMode = DuplexMode != PrintDuplexMode.AsPrinter
            ? DuplexMode
            : (Duplex ? PrintDuplexMode.LongEdge : DuplexMode),
        PaperSize = PaperSize,
        ColorMode = ColorMode,
        PrinterName = PrinterName,
        PageRange = PageRange,
        PaperSource = PaperSource,
        ScaleMode = ScaleMode,
        ScalePercent = ScalePercent,
        PagesPerSheet = PagesPerSheet,
        Booklet = Booklet,
        Collation = Collation,
        Parity = Parity,
        Quality = Quality,
        ProfileName = Name,
    };
}

/// <summary>Tạo preset từ một PrintConfig (dùng khi "lưu cấu hình hiện tại thành profile").</summary>
public static class PresetExtensions
{
    public static Preset ToPreset(this PrintConfig cfg, string name) => new()
    {
        Name = name,
        Copies = cfg.Copies,
        Duplex = cfg.Duplex,           // giữ bool để app cũ đọc được JSON
        DuplexMode = cfg.DuplexMode,   // lưu đủ enum cho app mới
        PaperSize = cfg.PaperSize,
        ColorMode = cfg.ColorMode,
        PrinterName = cfg.PrinterName,
        PageRange = cfg.PageRange,
        PaperSource = cfg.PaperSource,
        ScaleMode = cfg.ScaleMode,
        ScalePercent = cfg.ScalePercent,
        PagesPerSheet = cfg.PagesPerSheet,
        Booklet = cfg.Booklet,
        Collation = cfg.Collation,
        Parity = cfg.Parity,
        Quality = cfg.Quality,
    };
}