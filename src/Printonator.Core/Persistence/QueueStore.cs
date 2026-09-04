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
    public static string FilePath => Path.Combine(JsonFileStore.AppDataDir, "queue.json");

    /// <summary>DTO phẳng cho serialize — tránh phụ thuộc vào init-only properties của PrintJob.
    /// HasPerFilePrinter mặc định false để file queue.json CŨ (thiếu field) vẫn đọc được.</summary>
    public record QueueEntry(
        string FilePath,
        string FileName,
        string Format,
        JobSource Source,
        PrintConfig Config,
        DateTimeOffset CreatedAt,
        bool HasPerFilePrinter = false);

    /// <summary>Lưu job chờ (đường dẫn mặc định). Xóa file nếu không có gì để lưu.</summary>
    public static void Save(IEnumerable<PrintJob> jobs) => Save(FilePath, jobs);

    /// <summary>Lưu job chờ vào path cụ thể (test dùng).</summary>
    public static void Save(string path, IEnumerable<PrintJob> jobs)
    {
        var entries = jobs
            .Where(j => j.State is JobState.Queued or JobState.AwaitingApproval)
            .Select(j => new QueueEntry(j.FilePath, j.FileName, j.Format, j.Source, j.Config, j.CreatedAt, j.HasPerFilePrinter))
            .ToList();

        JsonFileStore.Save(path, entries);
    }

    /// <summary>Đọc danh sách job chờ từ file (đường dẫn mặc định). Trả rỗng nếu không có / file hỏng.</summary>
    public static List<QueueEntry> Load() => Load(FilePath);

    /// <summary>Đọc từ path cụ thể (test dùng). File hỏng → rename .corrupt-ts, trả rỗng.</summary>
    public static List<QueueEntry> Load(string path)
    {
        return JsonFileStore.Load<QueueEntry>(path);
    }
}