using Printonator.Core.Models;
using Printonator.Core.Persistence;
using Xunit;

namespace Printonator.Core.Tests;

/// <summary>
/// Unit test HistoryStore (lịch sử in — JSON cục bộ):
/// round-trip append/load, giới hạn MaxEntries (bỏ bản cũ nhất), file hỏng → rỗng + rename .corrupt, Clear.
/// Dùng path tạm qua overload Append(path)/Load(path)/Clear(path).
/// </summary>
public class HistoryStoreTests
{
    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), $"history_{Guid.NewGuid():N}.json");

    private static HistoryEntry MakeEntry(string name, JobState state = JobState.Done, int copies = 1)
        => new(
            FileName: name,
            FilePath: $"C:\\docs\\{name}",
            State: state,
            ErrorCode: state == JobState.Error ? "SPOOLER_FAILED" : null,
            FinishedAt: new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(7)),
            StartedAt: new DateTimeOffset(2026, 1, 1, 11, 59, 0, TimeSpan.FromHours(7)),
            Copies: copies,
            PageCount: 5);

    [Fact]
    public void Append_Load_Roundtrip()
    {
        var path = TempPath();

        HistoryStore.Append(path, MakeEntry("a.pdf"));
        HistoryStore.Append(path, MakeEntry("b.docx", JobState.Error));
        HistoryStore.Append(path, MakeEntry("c.png", JobState.Cancelled));

        var loaded = HistoryStore.Load(path);

        Assert.Equal(3, loaded.Count);
        var a = loaded[0];
        Assert.Equal("a.pdf", a.FileName);
        Assert.Equal("C:\\docs\\a.pdf", a.FilePath);
        Assert.Equal(JobState.Done, a.State);
        Assert.Null(a.ErrorCode);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(7)), a.FinishedAt);
        Assert.NotNull(a.StartedAt);
        Assert.Equal(1, a.Copies);
        Assert.Equal(5, a.PageCount);

        var b = loaded[1];
        Assert.Equal(JobState.Error, b.State);
        Assert.Equal("SPOOLER_FAILED", b.ErrorCode);

        var c = loaded[2];
        Assert.Equal(JobState.Cancelled, c.State);

        if (File.Exists(path)) File.Delete(path);
    }

    [Fact]
    public void Append_TrimsOldest_OverLimit()
    {
        var path = TempPath();

        // Ghi quá MaxEntries (1000) → chỉ giữ 1000 bản MỚI NHẤT, bỏ các bản cũ nhất ở đầu
        const int total = HistoryStore.MaxEntries + 2;
        for (var i = 0; i < total; i++)
            HistoryStore.Append(path, MakeEntry($"f{i:0000}.pdf"));

        var loaded = HistoryStore.Load(path);

        Assert.Equal(HistoryStore.MaxEntries, loaded.Count);
        // Bản cũ nhất còn lại là bản số 2 (0 và 1 bị cắt) — xác nhận cắt ĐẦU list, không phải cuối
        Assert.Equal("f0002.pdf", loaded[0].FileName);
        Assert.Equal($"f{total - 1:0000}.pdf", loaded[^1].FileName);

        if (File.Exists(path)) File.Delete(path);
    }

    [Fact]
    public void MissingFile_ReturnsEmpty()
    {
        Assert.Empty(HistoryStore.Load(TempPath()));
    }

    [Fact]
    public void CorruptFile_ReturnsEmpty_AndBacksUp()
    {
        var path = TempPath();
        File.WriteAllText(path, "{ not valid json ]]");

        Assert.Empty(HistoryStore.Load(path));

        // File hỏng được rename dự phòng (không ghi đè mất); file chính có thể ghi lại bình thường
        HistoryStore.Append(path, MakeEntry("a.pdf"));
        Assert.Single(HistoryStore.Load(path));
        Assert.Contains(Directory.GetFiles(Path.GetDirectoryName(path)!, "history_*.json.corrupt-*"), _ => true);

        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!)
                     .Where(f => f.StartsWith(Path.GetFileNameWithoutExtension(path))))
            File.Delete(f);
    }

    [Fact]
    public void Clear_RemovesAll()
    {
        var path = TempPath();
        HistoryStore.Append(path, MakeEntry("a.pdf"));
        HistoryStore.Append(path, MakeEntry("b.pdf"));

        HistoryStore.Clear(path);

        Assert.False(File.Exists(path));
        Assert.Empty(HistoryStore.Load(path));
    }
}