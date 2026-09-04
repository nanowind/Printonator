using System.Text.Json;
using System.Text.Json.Serialization;
using Printonator.Core.Models;

namespace Printonator.Core.Persistence;

/// <summary>
/// Lưu/đọc LỊCH SỬ IN (các job đã về trạng thái cuối) dạng JSON cục bộ %APPDATA%\Printonator\history.json.
/// Mỗi job Done/Error/Cancelled → Append 1 HistoryEntry. Giữ tối đa <see cref="MaxEntries"/> bản mới nhất
/// (bỏ bản CŨ nhất khi vượt). File hỏng → rename .corrupt-ts (giữ dữ liệu cũ, không ghi đè — pattern QueueStore).
/// Overload path cho test dùng temp path.
/// </summary>
public static class HistoryStore
{
    /// <summary>Số bản lịch sử tối đa giữ lại (bỏ bản cũ nhất khi vượt).</summary>
    public const int MaxEntries = 1000;

    /// <summary>Đường dẫn mặc định: %APPDATA%\Printonator\history.json</summary>
    public static string FilePath => Path.Combine(JsonFileStore.AppDataDir, "history.json");

    /// <summary>Thêm 1 dòng lịch sử (đường dẫn mặc định). Giữ tối đa MaxEntries bản mới nhất.</summary>
    public static void Append(HistoryEntry entry) => Append(FilePath, entry);

    /// <summary>Thêm 1 dòng lịch sử vào path cụ thể (test dùng).</summary>
    public static void Append(string path, HistoryEntry entry)
    {
        if (entry is null) return;
        var list = JsonFileStore.Load<HistoryEntry>(path);
        list.Add(entry);
        if (list.Count > MaxEntries)
            list.RemoveRange(0, list.Count - MaxEntries);   // bỏ N bản cũ nhất giữ 1000 mới nhất
        JsonFileStore.Save(path, list);
    }

    /// <summary>Đọc toàn bộ lịch sử (đường dẫn mặc định). File mất/hỏng → rỗng.</summary>
    public static List<HistoryEntry> Load() => Load(FilePath);

    /// <summary>Đọc từ path cụ thể (test dùng). File hỏng → rename .corrupt-ts, trả rỗng.</summary>
    public static List<HistoryEntry> Load(string path)
    {
        return JsonFileStore.Load<HistoryEntry>(path);
    }

    /// <summary>Xóa toàn bộ lịch sử (đường dẫn mặc định).</summary>
    public static void Clear() => Clear(FilePath);

    /// <summary>Xóa lịch sử ở path cụ thể (test dùng).</summary>
    public static void Clear(string path)
    {
        JsonFileStore.Delete(path);
    }
}

/// <summary>Một dòng lịch sử in — ghi lại kết quả cuối của job (kể cả lỗi/hủy).</summary>
public sealed record HistoryEntry(
    string FileName,
    string FilePath,
    JobState State,
    string? ErrorCode,
    DateTimeOffset FinishedAt,
    DateTimeOffset? StartedAt,
    int Copies,
    int PageCount,
    JobSource Source = JobSource.User);