namespace Printonator.Core.Models;

/// <summary>
/// Danh mục khổ giấy chuẩn kèm kích thước mm — dùng cho UI hiện "A4 (210 × 297 mm)"
/// và cho engine app gốc (Word/Excel) đặt PageSetup.PaperSize.
/// </summary>
public static class PaperCatalog
{
    /// <summary>Giá trị đặc biệt của PrintConfig.PaperSize = "theo tài liệu" (in đúng khổ gốc từng trang file).</summary>
    public const string AsDocument = "AsDocument";

    /// <summary>Tên khổ giấy (chuẩn tên máy in hay dùng) → kích thước mm.</summary>
    private static readonly IReadOnlyDictionary<string, (int W, int H)> Known = new Dictionary<string, (int, int)>
    {
        ["A0"] = (841, 1189),
        ["A1"] = (594, 841),
        ["A2"] = (420, 594),
        ["A3"] = (297, 420),
        ["A4"] = (210, 297),
        ["A5"] = (148, 210),
        ["A6"] = (105, 148),
        ["B4"] = (250, 353),
        ["B5"] = (176, 250),
        ["B6"] = (125, 176),
        ["Letter"] = (216, 279),
        ["Legal"] = (216, 356),
        ["Ledger"] = (279, 432),
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

    /// <summary>Thứ tự hiển thị chuẩn (theo mức dùng phổ biến trước).</summary>
    private static readonly string[] StandardOrder =
    {
        "A4", "A3", "A5", "A6", "A2", "A1", "A0",
        "B4", "B5", "B6",
        "Letter", "Legal", "Ledger", "Executive",
        "DL", "C5", "C6", "Com10",
    };

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
    {
        var pruned = printerSizes.Select(Normalize).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
        var ordered = StandardOrder.Where(pruned.Contains);
        var rest = pruned.Where(p => !StandardOrder.Contains(p));
        return ordered.Concat(rest).Select(Describe).ToList();
    }

    private static string Normalize(string name)
        => name?.Trim().Replace(" ", "") ?? "";
}