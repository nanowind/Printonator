using Printonator.Core;
using Printonator.Core.Models;
using Xunit;

namespace Printonator.Core.Tests;

/// <summary>
/// Unit test PrintQueue: Enqueue/AddOnly/ProcessExisting/RemoveJob.
/// Đặc biệt: KHÔNG duplicate khi bấm Print (bug a từng gặp).
/// </summary>
public class PrintQueueTests
{
    private static PrintJob MakeJob(string name = "a.pdf", string printer = "Microsoft Print to PDF")
        => new()
        {
            FilePath = $"C:\\{name}",
            FileName = name,
            Format = "PDF",
            Config = new PrintConfig { PrinterName = printer, Copies = 1 },
            PageCount = 3,
        };

    [Fact]
    public async Task Enqueue_Processes_AndMarksDone()
    {
        var q = new PrintQueue();
        var engine = new TestHelpers.FakeEngine();
        q.RegisterEngine(engine);
        var job = MakeJob();

        q.Enqueue(job);
        await TestHelpers.WaitUntilAsync(() => job.State == JobState.Done);

        Assert.Equal(JobState.Done, job.State);
        Assert.Single(q.Jobs);
        Assert.Equal(1, engine.Calls);
        q.Dispose();
    }

    [Fact]
    public async Task AddOnly_DoesNot_AutoPrint()
    {
        var q = new PrintQueue();
        var engine = new TestHelpers.FakeEngine();
        q.RegisterEngine(engine);
        var job = MakeJob();

        q.AddOnly(job);
        await Task.Delay(300); // cho drain chạy nếu có

        Assert.Single(q.Jobs);
        Assert.Equal(JobState.Queued, job.State);  // vẫn Queued — chưa in
        Assert.Equal(0, engine.Calls);
        q.Dispose();
    }

    [Fact]
    public async Task ProcessExisting_DoesNot_DuplicateRow()
    {
        var q = new PrintQueue();
        var engine = new TestHelpers.FakeEngine();
        q.RegisterEngine(engine);
        var job = MakeJob();
        q.AddOnly(job);

        q.ProcessExisting(job);
        await TestHelpers.WaitUntilAsync(() => job.State == JobState.Done);

        Assert.Single(q.Jobs);                       // KHÔNG thêm dòng mới
        Assert.Equal(1, engine.Calls);
        q.Dispose();
    }

    [Fact]
    public async Task ProcessExisting_Reprints_DoneJob()
    {
        var q = new PrintQueue();
        var engine = new TestHelpers.FakeEngine();
        q.RegisterEngine(engine);
        var job = MakeJob();

        q.Enqueue(job);
        await TestHelpers.WaitUntilAsync(() => job.State == JobState.Done);
        Assert.Equal(1, engine.Calls);

        q.ProcessExisting(job);  // in lại job đã done
        await TestHelpers.WaitUntilAsync(() => job.State == JobState.Done);

        Assert.Single(q.Jobs);
        Assert.Equal(2, engine.Calls);
        q.Dispose();
    }

    [Fact]
    public void RemoveJob_Removes_FromList()
    {
        var q = new PrintQueue();
        q.RegisterEngine(new TestHelpers.FakeEngine());
        var job = MakeJob();
        q.AddOnly(job);

        var removed = q.RemoveJob(job);

        Assert.True(removed);
        Assert.Empty(q.Jobs);
        q.Dispose();
    }

    [Fact]
    public void RemoveJob_Nonexistent_ReturnsFalse()
    {
        var q = new PrintQueue();
        q.RegisterEngine(new TestHelpers.FakeEngine());
        var job = MakeJob();
        q.AddOnly(job);
        var other = MakeJob("b.pdf");

        var removed = q.RemoveJob(other);

        Assert.False(removed);
        Assert.Single(q.Jobs);
        q.Dispose();
    }

    [Fact]
    public async Task ErrorJob_SetsError_WithPrintError()
    {
        var q = new PrintQueue();
        q.RegisterEngine(new TestHelpers.FailingEngine());
        var job = MakeJob();

        q.Enqueue(job);
        await TestHelpers.WaitUntilAsync(() => job.State == JobState.Error);

        Assert.Equal(JobState.Error, job.State);
        Assert.NotNull(job.Error);
        Assert.Equal(ErrorCodes.SpoolerFailed, job.Error!.Code);
        Assert.Equal(PrintErrorCategory.App, job.Error.Category);
        q.Dispose();
    }
}