using System.Text.Json;
using System.Text.Json.Serialization;
using Printonator.Core.Models;

namespace Printonator.Core.Presets;

/// <summary>
/// Xuất/nhập danh sách preset ra file JSON (.printonator) — để dời cấu hình giữa máy.
/// Định dạng giống presets.json nhưng enum ghi THEO TÊN (LongEdge, ByDocuments… thay vì số)
/// cho file đọc được bằng mắt. Import đọc file ngoài: file hỏng → đổi tên .corrupt + trả rỗng.
/// </summary>
public static class PresetExporter
{
    private static JsonSerializerOptions Options() => new()
    {
        WriteIndented = true,
        // Đọc + ghi enum theo tên để file xuất ra đọc được; vẫn đọc được số (legacy) vì AllowIntegerValues mặc định true.
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Xuất toàn bộ preset trong store ra file JSON.</summary>
    public static void Export(PresetStore store, string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(store.Load(), Options()));

    /// <summary>Xuất danh sách preset cụ thể ra file JSON (dùng khi chỉ xuất preset đang chọn).</summary>
    public static void Export(IEnumerable<Preset> presets, string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(presets.ToList(), Options()));

    /// <summary>
    /// Đọc danh sách preset từ file ngoài (JSON array). File hỏng hoặc không phải array
    /// → trả về rỗng, không ném; file lỗi được đổi tên .corrupt để không ghi đè mất dữ liệu gốc.
    /// </summary>
    public static List<Preset> Import(string path)
    {
        try
        {
            if (!File.Exists(path)) return new List<Preset>();
            return JsonSerializer.Deserialize<List<Preset>>(File.ReadAllText(path), Options()) ?? new List<Preset>();
        }
        catch
        {
            try
            {
                if (File.Exists(path))
                    File.Move(path, $"{path}.corrupt-{DateTimeOffset.Now:yyyyMMddHHmmss}");
            }
            catch { }
            return new List<Preset>();
        }
    }
}