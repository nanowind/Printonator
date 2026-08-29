using System.Globalization;
using Microsoft.Win32;

namespace Printonator.UI.Localization;

/// <summary>
/// Xác định ngôn ngữ giao diện khi app khởi động. Thứ tự ưu tiên:
///   1. Biến môi trường PRINTONATOR_LANGUAGE (test hook + escape cho user bị kẹt ngôn ngữ sai)
///   2. Registry HKCU\Software\Printonator\Language (do Inno Setup ghi lúc cài đặt)
///   3. Mặc định: tiếng Việt (vi-VN)
/// Bất kỳ giá trị sai/thiếu đều rơi về vi-VN an toàn — KHÔNG bao giờ crash lúc startup.
/// </summary>
public static class CultureResolver
{
    public const string RegistrySubKey = @"Software\Printonator";
    public const string RegistryValueName = "Language";
    public const string EnvVarName = "PRINTONATOR_LANGUAGE";

    /// <summary>Các ngôn ngữ app hỗ trợ — culture tag chuẩn .NET.</summary>
    public static readonly IReadOnlyList<string> SupportedCultures = new[] { "vi-VN", "en-US", "zh-CN", "ru-RU", "ja-JP" };

    /// <summary>Ngôn ngữ mặc định khi không cấu hình gì.</summary>
    public static readonly CultureInfo DefaultCulture = CultureInfo.GetCultureInfo("vi-VN");

    /// <summary>Giải culture cho UI: env → registry → vi-VN. LUÔN trả culture hợp lệ, không ném.</summary>
    public static CultureInfo Resolve()
    {
        var raw = ReadEnv() ?? ReadRegistry();
        return TryParseSafe(raw) ?? DefaultCulture;
    }

    /// <summary>Map mã 2-chữ ISO → culture tag chuẩn app hỗ trợ (vi→vi-VN, zh→zh-CN, ru→ru-RU, ja→ja-JP, en→en-US).
    /// Cho phép env/registry ghi cả 2 dạng: "zh" hoặc "zh-CN" đều resolve đúng.</summary>
    private static readonly IReadOnlyDictionary<string, string> TwoLetterToFull = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["vi"] = "vi-VN",
        ["en"] = "en-US",
        ["zh"] = "zh-CN",
        ["ru"] = "ru-RU",
        ["ja"] = "ja-JP",
    };

    /// <summary>Giải culture từ chuỗi bất kỳ (dùng trong test). Trả null nếu không hợp lệ.</summary>
    public static CultureInfo? TryParseSafe(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var trimmed = raw.Trim();
            // Chuẩn hóa 2-chữ ISO → full tag (Inno ghi full tag qua GetLangTag; env có thể dùng 2-chữ)
            if (TwoLetterToFull.TryGetValue(trimmed, out var full)) trimmed = full;
            // Chỉ chấp nhận culture app thực sự hỗ trợ — tránh vô tình chấp nhận fr-FR rồi thiếu catalog
            var c = CultureInfo.GetCultureInfo(trimmed);
            if (!SupportedCultures.Contains(c.Name, StringComparer.OrdinalIgnoreCase)) return null;
            return c;
        }
        catch (CultureNotFoundException) { return null; }
        catch (ArgumentException) { return null; }
        catch (System.Runtime.InteropServices.ExternalException) { return null; }
    }

    private static string? ReadEnv()
        => Environment.GetEnvironmentVariable(EnvVarName);

    private static string? ReadRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistrySubKey);
            return key?.GetValue(RegistryValueName) as string;
        }
        catch (System.Security.SecurityException) { return null; }
        catch (System.IO.IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
