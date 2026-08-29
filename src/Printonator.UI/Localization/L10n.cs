using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Printonator.UI.Localization;

/// <summary>
/// Truy cập chuỗi UI theo ngôn ngữ đã chọn. Dữ liệu: 1 JSON catalog nhúng (Localization/Strings.json):
///   { "_meta": {...}, "vi": { "Key": "Giá trị", ... }, "en": {...}, "zh": {...}, "ru": {...}, "ja": {...} }
/// L10n.Load() gọi trong App.OnStartup SAU khi CultureResolver.Resolve() → nạp dictionary ngôn ngữ đó.
/// Lookup fallback: ngôn ngữ chọn → tiếng Việt (vi là nguồn sự thật, luôn có) → tên key.
/// Ngôn ngữ cố định từ khởi động (chọn lúc cài, restart khi đổi) nên không cần runtime-switch.
/// JSON 1 file = key-set 5 ngôn ngữ luôn khớp nhau; gate script đọc Params.Json quét thiếu key.
/// </summary>
public static class L10n
{
    public const string AssetName = "Printonator.UI.Localization.Strings.json";
    private static readonly IReadOnlyDictionary<string, string> _empty = new Dictionary<string, string>(StringComparer.Ordinal);
    private static Dictionary<string, Dictionary<string, string>> _catalog = new();
    private static string _langCode = "vi";

    /// <summary>Culture UI hiện tại (set trong App.OnStartup → L10n.ApplyCulture).</summary>
    public static CultureInfo CurrentCulture { get; private set; } = CultureResolver.DefaultCulture;

    /// <summary>Mã 2-chữ ngôn ngữ đang hoạt động (vi/en/zh/ru/ja) — dùng cho lookup chính.</summary>
    public static string LangCode => _langCode;

    /// <summary>Toàn bộ chuỗi ngôn ngữ VI (nguồn sự thật — dùng cho consistency gate, glossary).</summary>
    public static IReadOnlyDictionary<string, string> VietnameseStrings
        => _catalog.TryGetValue("vi", out var v) ? v : _empty;

    /// <summary>Nạp catalog nhúng một lần (idempotent — gọi lại vô hại). Gọi trong App.OnStartup trước window đầu.</summary>
    public static void Initialize()
    {
        if (_catalog.Count > 0) return;
        try
        {
            using var s = typeof(L10n).Assembly.GetManifestResourceStream(AssetName);
            if (s is null) { _catalog = new(); return; }
            using var r = new StreamReader(s);
            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(r.ReadToEnd())
                      ?? new();
            _catalog = raw.Where(kv => kv.Value.ValueKind == JsonValueKind.Object)
                          .ToDictionary(kv => kv.Key, kv => kv.Value.EnumerateObject()
                              .ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : ""));
        }
        catch { _catalog = new(); }
    }

    /// <summary>Đặt culture + chọn ngôn ngữ dictionary. Gọi trong App.OnStartup TRƯỚC khi dựng window.</summary>
    public static void ApplyCulture(CultureInfo culture)
    {
        CurrentCulture = culture;
        _langCode = culture.TwoLetterISOLanguageName.ToLowerInvariant();
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
    }

    /// <summary>Lấy chuỗi theo key — fallback: ngôn ngữ → vi → key. Không bao giờ null.</summary>
    public static string S(string key)
    {
        if (_catalog.TryGetValue(_langCode, out var lang) && lang.TryGetValue(key, out var v))
            return v;
        return VietnameseStrings.TryGetValue(key, out var f) ? f : key;
    }

    /// <summary>Lấy chuỗi + format placeholder {0}... theo CurrentCulture.</summary>
    public static string F(string key, params object?[] args) => string.Format(CurrentCulture, S(key), args);

    /// <summary>Format số theo culture (vi "1.234" / en "1,234" / ru "1 234").</summary>
    public static string N(long value, string format = "N0") => value.ToString(format, CurrentCulture);
}