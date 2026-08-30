using Printonator.Core.Models;
using Printonator.Core.Persistence;
using Xunit;

namespace Printonator.Core.Tests;

/// <summary>
/// Unit test QueueStore (persist hàng đợi qua restart — JSON cục bộ):
/// round-trip save/load, file hỏng → rỗng + rename .corrupt, chỉ lưu job ĐANG CHỜ.
/// Dùng path tạm qua overload Save(path)/Load(path).
/// </summary>
public class QueueStoreTests
{
    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), $"queue_{Guid.NewGuid():N}.json");

    private static PrintJob MakeJob(string name, JobSource source = JobSource.User, string printer = "HP404")
        => new()
        {
            FilePath = $"C:\\docs\\{name}",
            FileName = name,
            Format = "PDF",
            Source = source,
            Config = new PrintConfig { PrinterName = printer, Copies = 3, Duplex = true },
            PageCount = 5,
        };

    [Fact]
    public void Save_Load_Roundtrip()
    {
        var path = TempPath();
        var userJob = MakeJob("a.pdf");           // State = Queued (mặc định)
        var mcpJob = MakeJob("b.pdf", JobSource.Mcp);

        QueueStore.Save(path, new[] { userJob, mcpJob });
        var loaded = QueueStore.Load(path);

        Assert.Equal(2, loaded.Count);
        var a = loaded[0];
        Assert.Equal("C:\\docs\\a.pdf", a.FilePath);
        Assert.Equal("a.pdf", a.FileName);
        Assert.Equal("PDF", a.Format);
        Assert.Equal(JobSource.User, a.Source);
        Assert.Equal("HP404", a.Config.PrinterName);
        Assert.Equal(3, a.Config.Copies);
        Assert.True(a.Config.Duplex);

        var b = loaded[1];
        Assert.Equal(JobSource.Mcp, b.Source);
        Assert.Equal("b.pdf", b.FileName);

        if (File.Exists(path)) File.Delete(path);
    }

    [Fact]
    public void MissingFile_ReturnsEmpty()
    {
        Assert.Empty(QueueStore.Load(TempPath()));
    }

    [Fact]
    public void CorruptFile_ReturnsEmpty_AndBacksUp()
    {
        var path = TempPath();
        File.WriteAllText(path, "{ not valid json ]]");

        Assert.Empty(QueueStore.Load(path));

        // File hỏng được rename dự phòng (không ghi đè mất), file chính có thể lưu lại bình thường
        QueueStore.Save(path, new[] { MakeJob("a.pdf") });
        Assert.Single(QueueStore.Load(path));
        Assert.Contains(Directory.GetFiles(Path.GetDirectoryName(path)!, "queue_*.json.corrupt-*"), _ => true);

        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!)
                     .Where(f => f.StartsWith(Path.GetFileNameWithoutExtension(path))))
            File.Delete(f);
    }

    [Fact]
    public void Save_Skips_NonWaitingJobs()
    {
        var path = TempPath();
        var queued = MakeJob("q.pdf");
        var done = MakeJob("d.pdf");
        done.State = JobState.Done;
        var err = MakeJob("e.pdf");
        err.State = JobState.Error;
        var cancelled = MakeJob("c.pdf");
        cancelled.State = JobState.Cancelled;
        var converting = MakeJob("conv.pdf");
        converting.State = JobState.Converting;
        var spooling = MakeJob("s.pdf");
        spooling.State = JobState.Spooling;

        QueueStore.Save(path, new[] { queued, done, err, cancelled, converting, spooling });
        var loaded = QueueStore.Load(path);

        Assert.Single(loaded);
        Assert.Equal("q.pdf", loaded[0].FileName);

        if (File.Exists(path)) File.Delete(path);
    }

    [Fact]
    public void Save_NoWaitingJobs_DeletesFile()
    {
        var path = TempPath();
        var done = MakeJob("d.pdf");
        done.State = JobState.Done;
        QueueStore.Save(path, new[] { done });
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Save_AwaitingApproval_IsKept()
    {
        var path = TempPath();
        var mcp = MakeJob("m.pdf", JobSource.Mcp);
        mcp.State = JobState.AwaitingApproval;   // job AI chưa duyệt — phải giữ lại sau restart

        QueueStore.Save(path, new[] { mcp });
        var loaded = QueueStore.Load(path);

        Assert.Single(loaded);
        Assert.Equal(JobSource.Mcp, loaded[0].Source);

        if (File.Exists(path)) File.Delete(path);
    }
}
