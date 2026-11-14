namespace Printonator.Core.Models;

/// <summary>
/// Danh mục khổ giấy chuẩn kèm kích thước mm — dùng cho UI hiện "A4 (210 × 297 mm)"
/// và cho engine app gốc (Word/Excel) đặt PageSetup.PaperSize.
/// </summary>
public static class PaperCatalog
{
    /// <summary>Giá trị đặc biệt của PrintConfig.PaperSize = "theo tài liệu" (in đúng khổ gốc từng trang file).</summary>
    public const string AsDocument = "AsDocument";

    /// <summary>Tên khổ giấy (chuẩn tên máy in hay dùng) → kích thước mm.
    /// LƯU Ý B5: theo đúng những gì Windows và Office dùng, "B5" = **JIS 182×257**, không phải
    /// ISO 176×250. Đã đo trên máy thật: cả 10 driver báo B5 (DMPAPER 13) = 182×257, và
    /// WdPaperSize.wdPaperB5 / XlPaperSize.xlPaperB5 đều trỏ vào tờ đó. B5 ISO không có mã Win32
    /// riêng (chỉ có dạng phong bì ISOB5Envelope) nên không đưa vào danh mục.</summary>
    private static readonly IReadOnlyDictionary<string, (int W, int H)> Known =
        new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase)
        {
            ["A0"] = (841, 1189),
            ["A1"] = (594, 841),
            ["A2"] = (420, 594),
            ["A3"] = (297, 420),
            ["A4"] = (210, 297),
            ["A5"] = (148, 210),
            ["A6"] = (105, 148),
            ["B4"] = (250, 353),
            ["B5"] = (182, 257),
            ["B6"] = (125, 176),
            ["Letter"] = (216, 279),
            ["Legal"] = (216, 356),
            ["Ledger"] = (279, 432),
            ["Tabloid"] = (279, 432),
            ["Statement"] = (140, 216),
            ["Executive"] = (184, 267),
            ["DL"] = (110, 220),
            ["DLEnvelope"] = (110, 220),
            ["C5"] = (162, 229),
            ["C5Envelope"] = (162, 229),
            ["C6"] = (114, 162),
            ["C6Envelope"] = (114, 162),
            ["C3Envelope"] = (324, 458),
            ["C4Envelope"] = (229, 324),
            ["Com10"] = (105, 241),
        };

    /// <summary>Thứ tự hiển thị chuẩn (theo mức dùng phổ biến trước) — cũng là danh sách tên hợp lệ
    /// để gợi ý cho caller. "Tabloid" = cùng tờ 279×432 với "Ledger" (chỉ khác chiều), liệt kê để tên
    /// gọi nào cũng dùng được — bảng mã Office dùng "Tabloid" còn DEVMODE dùng mã Ledger.</summary>
    public static IReadOnlyList<string> StandardOrder { get; } =
        new[]
        {
            "A4", "A3", "A5", "A6", "A2", "A1", "A0",
            "B4", "B5", "B6",
            "Letter", "Legal", "Ledger", "Tabloid", "Executive", "Statement",
            "DL", "C5", "C6", "Com10",
        };

    /// <summary>MỌI tên khổ tra được (kể cả phong bì chỉ có trong Known như DLEnvelope) —
    /// dùng cho thông báo lỗi để gợi ý không bị thiếu.</summary>
    public static IReadOnlyList<string> AllNames => Known.Keys.ToList();

    /// <summary>Kích thước mm của khổ (null nếu không biết).</summary>
    public static (int W, int H)? Dimensions(string name)
        => Known.TryGetValue(Normalize(name), out var d) ? d : null;

    /// <summary>"A4" → "A4 (210 × 297 mm)"; khổ lạ → trả nguyên tên.</summary>
    public static string Describe(string name)
    {
        var n = Normalize(name);
        return Known.TryGetValue(n, out var d) ? $"{n} ({d.W} × {d.H} mm)" : name;
    }

    /// <summary>"A4 (210 × 297 mm)" → "A4".</summary>
    public static string SizeName(string display)
    {
        var s = display.Trim();
        var i = s.IndexOf('(');
        return i > 0 ? s[..i].Trim() : s;
    }

    /// <summary>Danh sách khổ chuẩn (thứ tự ưu tiên) dưới dạng "A4 (210 × 297 mm)".</summary>
    public static IReadOnlyList<string> StandardSizes() => StandardOrder.Select(Describe).ToList();

    /// <summary>
    /// Lọc danh sách khổ của máy in theo thứ tự chuẩn, thêm kích thước mm.
    /// Khổ máy in không nằm trong catalog vẫn giữ nguyên tên (có thứ tự sau).
    /// </summary>
    public static IReadOnlyList<string> FromPrinter(IEnumerable<string> printerSizes)
        => OrderForDisplay(printerSizes).Select(Describe).ToList();

    /// <summary>
    /// Máy in báo những khổ nào mà app ĐẶT ĐƯỢC — giao của khả năng driver với danh mục.
    /// Dùng cho MCP: AI đọc list_printers rồi truyền lại đúng một tên trong danh sách này thì
    /// print_files chắc chắn nhận. Tên riêng của driver (Invoice, SuperA, B5Extra...) bị LOẠI vì
    /// không engine nào đặt được khổ đó — chúng âm thầm rơi về A4 (hoặc chỉ đúng ở đường N-up),
    /// nên quảng cáo chúng cho AI là hứa điều app không làm được.
    /// </summary>
    public static IReadOnlyList<string> SupportedFromPrinter(IEnumerable<string>? printerSizes)
        => (printerSizes ?? []).Where(s => Dimensions(s) is not null)
                       .Select(Normalize)
                       .Where(s => s.Length > 0)
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .ToList();

    /// <summary>
    /// Số cột/hàng của lưới N-up sao cho ĐỦ CHỖ cho mọi trang của tờ (cols × rows ≥ perSheet).
    /// MỘT nguồn cho cả engine in (GdiPrintEngine) lẫn preview (PrintPreviewModel) — trước đây hai
    /// bên tự tính, engine đúng còn preview dùng switch theo ngưỡng nên 10/12/15 bị cắt còn 9 ô.
    /// Cho đúng các lưới quen thuộc: 2→2×1, 4→2×2, 6→3×2, 9→3×3, 16→4×4.
    /// ponytail: Math.Sqrt trên double sai với perSheet > 2^26 — UI/MCP đều clamp ≤16 nên không tới.
    /// </summary>
    public static (int Cols, int Rows) NUpGrid(int perSheet)
    {
        if (perSheet <= 1) return (1, 1);
        var cols = (int)Math.Ceiling(Math.Sqrt(perSheet));
        return (cols, (int)Math.Ceiling(perSheet / (double)cols));
    }

    /// <summary>Xếp danh sách tên khổ theo thứ tự chuẩn, để nguyên tên (không kèm mm).</summary>
    private static IReadOnlyList<string> OrderForDisplay(IEnumerable<string> names)
    {
        var pruned = names.Select(Normalize).Where(s => !string.IsNullOrEmpty(s))
                          .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // So khớp KHÔNG phân biệt hoa thường cho đồng bộ với Known/Dimensions: driver báo "a4" phải
        // xếp đúng vị trí A4, không bị đẩy xuống cuối danh sách như khổ lạ.
        var ordered = StandardOrder.Where(n => pruned.Contains(n, StringComparer.OrdinalIgnoreCase));
        var rest = pruned.Where(p => !StandardOrder.Contains(p, StringComparer.OrdinalIgnoreCase));
        return ordered.Concat(rest).ToList();
    }

    private static string Normalize(string name)
        => name?.Trim().Replace(" ", "") ?? "";
}