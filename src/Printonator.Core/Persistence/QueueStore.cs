using System.Text.Json;
using System.Text.Json.Serialization;
using Printonator.Core.Models;

namespace Printonator.Core.Persistence;

/// <summary>
/// Lưu/đọc hàng đợi in qua restart dạng JSON cục bộ.
/// Chỉ lưu job ĐANG CHỜ (Queued / AwaitingApproval) — bỏ qua Converting/Spooling/Done/Error/Cancelled.
/// File hỏng → rename .corrupt-ts (giữ dữ liệu cũ, không ghi đè).
/// Overload path cho test dùng temp path.
/// </summary>
public static class QueueStore
{
    /// <summary>Đường dẫn mặc định: %APPDATA%\Printonator\queue.json</summary>
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Printonator", "queue.json");

    /// <summary>DTO phẳng cho serialize — tránh phụ thuộc vào init-only properties của PrintJob.</summary>
    public record QueueEntry(
        string FilePath,
        string FileName,
        string Format,
        JobSource Source,
        PrintConfig Config,
        DateTimeOffset CreatedAt);

    /// <summary>Lưu job chờ (đường dẫn mặc định). Xóa file nếu không có gì để lưu.</summary>
    public static void Save(IEnumerable<PrintJob> jobs) => Save(FilePath, jobs);

    /// <summary>Lưu job chờ vào path cụ thể (test dùng).</summary>
    public static void Save(string path, IEnumerable<PrintJob> jobs)
    {
        var entries = jobs
            .Where(j => j.State is JobState.Queued or JobState.AwaitingApproval)
            .Select(j => new QueueEntry(j.FilePath, j.FileName, j.Format, j.Source, j.Config, j.CreatedAt))
            .ToList();

        if (entries.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        });
        File.WriteAllText(path, json);
    }

    /// <summary>Đọc danh sách job chờ từ file (đường dẫn mặc định). Trả rỗng nếu không có / file hỏng.</summary>
    public static List<QueueEntry> Load() => Load(FilePath);

    /// <summary>Đọc từ path cụ thể (test dùng). File hỏng → rename .corrupt-ts, trả rỗng.</summary>
    public static List<QueueEntry> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new List<QueueEntry>();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<QueueEntry>>(json, new JsonSerializerOptions
            {
                Converters = { new JsonStringEnumConverter() },
            }) ?? new List<QueueEntry>();
        }
        catch
        {
            // File hỏng → đổi tên dự phòng (không ghi đè mất dữ liệu), trả rỗng
            try
            {
                if (File.Exists(path))
                    File.Move(path, $"{path}.corrupt-{DateTimeOffset.Now:yyyyMMddHHmmss}");
            }
            catch { }
            return new List<QueueEntry>();
        }
    }
}