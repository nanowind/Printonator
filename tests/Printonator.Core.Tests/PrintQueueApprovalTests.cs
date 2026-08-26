using Printonator.Core;
using Printonator.Core.Models;
using Xunit;

namespace Printonator.Core.Tests;

/// <summary>
/// Unit test luồng duyệt (approve) cho AI in qua MCP:
/// AddForApproval/ApproveJob/RejectJob/CancelJob/CountPendingPages + chống in kép.
/// </summary>
public class PrintQueueApprovalTests
{
    private static PrintJob McpJob(string name = "a.pdf", int pages = 3)
        => new()
        {
            FilePath = $"C:\\{name}",
            FileName = name,
            Format = "PDF",
            Source = JobSource.Mcp,
            Config = new PrintConfig { PrinterName = "HP404", Copies = 1, PageRange = "All" },
            PageCount = pages,
        };

    [Fact]
    public void AddForApproval_SetsAwaiting_DoesNotPrint()
    {
        var q = new PrintQueue();
        var e = new TestHelpers.FakeEngine();
        q.RegisterEngine(e);
        var job = McpJob();

        q.AddForApproval([job]);

        Assert.Single(q.Jobs);
        Assert.Equal(JobState.AwaitingApproval, job.State);
        Assert.Equal(0, e.Calls);
        q.Dispose();
    }

    [Fact]
    public void AddForApproval_Ignores_NonMcpJob()
    {
        var q = new PrintQueue();
        var job = new PrintJob
        {
            FilePath = "C:\\u.pdf",
            FileName = "u.pdf",
            Format = "PDF",
            Source = JobSource.User, // không từ AI → không cần duyệt
            Config = new PrintConfig { PrinterName = "HP404" },
            PageCount = 1,
        };

        q.AddForApproval([job]);

        Assert.Empty(q.Jobs); // bị bỏ qua
        q.Dispose();
    }

    [Fact]
    public async Task ApproveJob_Prints_Once()
    {
        var q = new PrintQueue();
        var e = new TestHelpers.FakeEngine();
        q.RegisterEngine(e);
        var job = McpJob();
        q.AddForApproval([job]);

        Assert.True(q.ApproveJob(job));
        // Approve lần 2 ngay lập tức → job không còn AwaitingApproval → false (chống in kép)
        Assert.False(q.ApproveJob(job));

        await TestHelpers.WaitUntilAsync(() => job.State == JobState.Done);
        Assert.Equal(1, e.Calls);
        q.Dispose();
    }

    [Fact]
    public void RejectJob_MarksCancelled_DoesNotPrint()
    {
        var q = new PrintQueue();
        var e = new TestHelpers.FakeEngine();
        q.RegisterEngine(e);
        var job = McpJob();
        q.AddForApproval([job]);

        Assert.True(q.RejectJob(job));
        Assert.Equal(JobState.Cancelled, job.State);
        Assert.Equal(0, e.Calls);
        q.Dispose();
    }

    [Fact]
    public void Approve_Rejects_NonAwaiting()
    {
        var q = new PrintQueue();
        q.RegisterEngine(new TestHelpers.FakeEngine());
        var job = McpJob();
        q.AddOnly(job); // User-job Queued, không phải AwaitingApproval

        Assert.False(q.ApproveJob(job));
        Assert.False(q.RejectJob(job));
        q.Dispose();
    }

    [Fact]
    public void CancelJob_Cancels_Queued()
    {
        var q = new PrintQueue();
        var e = new TestHelpers.FakeEngine();
        q.RegisterEngine(e);
        var job = McpJob();
        q.AddOnly(job); // Queued (UI thêm, chưa in)

        Assert.True(q.CancelJob(job));
        Assert.Equal(JobState.Cancelled, job.State);
        Assert.Equal(0, e.Calls);
        q.Dispose();
    }

    [Fact]
    public async Task ProcessExisting_DoesNot_DoublePrint_WhileConverting()
    {
        var q = new PrintQueue();
        var e = new SlowEngine(300); // engine có async-gap → job giữ Converting giữa 2 lệnh
        q.RegisterEngine(e);
        var job = McpJob();
        q.AddOnly(job);

        // Lệnh 1: drain bắt đầu → job thành Converting (chưa xong)
        q.ProcessExisting(job);
        // Lệnh 2 ngay khi còn Converting → phải no-op (không in kép)
        q.ProcessExisting(job);

        await TestHelpers.WaitUntilAsync(() => job.State == JobState.Done);
        Assert.Equal(1, e.Calls);
        q.Dispose();
    }

    private sealed class SlowEngine : IPrintEngine
    {
        private readonly int _delayMs;
        public int Calls;
        public SlowEngine(int delayMs) => _delayMs = delayMs;
        public bool CanHandle(string format) => true;
        public async Task<Result<bool>> PrintAsync(PrintJob job, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(_delayMs, ct); // tạo async-gap để job vẫn Converting
            return Result<bool>.Ok(true);
        }
    }

    [Fact]
    public void CountPendingPages_Sums_AwaitingAndQueued()
    {
        var q = new PrintQueue();
        q.AddForApproval([McpJob(pages: 3), McpJob(pages: 2)]);

        Assert.Equal(5, q.CountPendingPages());
        q.Dispose();
    }
}