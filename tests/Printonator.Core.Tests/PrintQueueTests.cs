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
    public async Task ProcessExisting_AllReachDone()
    {
        // Đường in THẬT dùng ProcessExisting (không qua DrainAsync). Toàn bộ job lô phải về
        // trạng thái cuối (Done) — không phụ thuộc AllJobsCompleted (completion giờ do UI
        // WaitBatchDoneAsync điều khiển, không phải Core event).
        var q = new PrintQueue();
        var engine = new TestHelpers.FakeEngine();
        q.RegisterEngine(engine);

        var j1 = MakeJob("a.pdf");
        var j2 = MakeJob("b.pdf");
        q.AddOnly(j1);
        q.AddOnly(j2);

        q.ProcessExisting(j1);
        q.ProcessExisting(j2);
        await TestHelpers.WaitUntilAsync(() => j1.State == JobState.Done && j2.State == JobState.Done);

        Assert.Equal(JobState.Done, j1.State);
        Assert.Equal(JobState.Done, j2.State);
        q.Dispose();
    }

    [Fact]
    public async Task RemoveDoneJob_NoDeadlock()
    {
        // RemoveJob gỡ job Done không được deadlock/treo (bug cũ khi AllJobsCompleted còn giữ _sync).
        var q = new PrintQueue();
        var engine = new TestHelpers.FakeEngine();
        q.RegisterEngine(engine);

        var j1 = MakeJob("a.pdf");
        var j2 = MakeJob("b.pdf");
        q.AddOnly(j1);
        q.AddOnly(j2);

        q.ProcessExisting(j1);
        q.ProcessExisting(j2);
        await TestHelpers.WaitUntilAsync(() => j1.State == JobState.Done && j2.State == JobState.Done);

        // UI làm đúng: xóa các job Done. Nếu RemoveJob bị chặn (deadlock) → test fail sau 4s.
        foreach (var d in q.Jobs.Where(j => j.State == JobState.Done).ToList()) q.RemoveJob(d);
        await TestHelpers.WaitUntilAsync(() => q.Jobs.Count == 0, timeoutMs: 4000);
        Assert.Empty(q.Jobs);
        await Task.Delay(100);  // chờ drain thoát hẳn trước khi Dispose (tránh CTS đã dispose)
        q.Dispose();
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

    // ================= In TUẦN TỰ + DỪNG LÔ KHI LỖI (Print All / In file này) =================
    // Bug v0.1.8: ProcessExisting chạy MỖI job một DrainOnceAsync riêng → cả lô "Converting" đồng
    // loạt, in chồng lên nhau, Pause vô nghĩa. Fix: 1 vòng drain duy nhất (tuần tự) + stop-on-error.

    /// <summary>Engine chặn (giả in thật chậm) — test tuần tự: job sau phải đợi job trước xong.</summary>
    private sealed class BlockingEngine : IPrintEngine
    {
        public TaskCompletionSource<bool> Started = new();
        public TaskCompletionSource<bool> Release = new();
        public int Calls;
        public bool CanHandle(string format) => true;
        public async Task<Result<bool>> PrintAsync(PrintJob job, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult(true);
            await Release.Task;   // giữ "đang in" cho tới khi test thả
            return Result<bool>.Ok(true);
        }
    }

    /// <summary>Engine fail đúng file chỉ định (các file khác OK) — test stop-on-error + resume.</summary>
    private sealed class ConditionalEngine : IPrintEngine
    {
        private readonly string _failFile;
        public ConditionalEngine(string failFile) => _failFile = failFile;
        public int Calls;
        public bool CanHandle(string format) => true;
        public Task<Result<bool>> PrintAsync(PrintJob job, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (job.FileName == _failFile)
                return Task.FromResult(Result<bool>.Fail(new PrintError
                {
                    Code = ErrorCodes.SpoolerFailed,
                    Category = PrintErrorCategory.App,
                    Message = "Fail",
                    Hint = "x",
                }));
            return Task.FromResult(Result<bool>.Ok(true));
        }
    }

    [Fact]
    public async Task ProcessBatch_Prints_Sequentially_NotConcurrently()
    {
        var q = new PrintQueue();
        var engine = new BlockingEngine();
        q.RegisterEngine(engine);
        var j1 = MakeJob("a.pdf");
        var j2 = MakeJob("b.pdf");
        q.AddOnly(j1);
        q.AddOnly(j2);

        q.ProcessBatch(new[] { j1, j2 });
        await engine.Started.Task;   // j1 đang in (block)
        await Task.Delay(200);       // đủ thời gian — nếu CHẠY ĐỒNG LOẠT thì j2 cũng đã Converting

        // TUẦN TỰ: chỉ j1 in; j2 vẫn Queued (không Converting/Done như lỗi cũ)
        Assert.Equal(JobState.Converting, j1.State);
        Assert.Equal(JobState.Queued, j2.State);
        Assert.Equal(1, engine.Calls);

        engine.Release.SetResult(true);   // j1 xong → j2 mới tới lượt
        await TestHelpers.WaitUntilAsync(() => j2.State == JobState.Done);
        Assert.Equal(2, engine.Calls);
        q.Dispose();
    }

    [Fact]
    public async Task ProcessBatch_StopsBatch_WhenFirstJobErrors()
    {
        var q = new PrintQueue();
        var engine = new ConditionalEngine("bad.pdf");
        q.RegisterEngine(engine);
        var j1 = MakeJob("bad.pdf");
        var j2 = MakeJob("good.pdf");
        q.AddOnly(j1);
        q.AddOnly(j2);

        q.ProcessBatch(new[] { j1, j2 });
        await TestHelpers.WaitUntilAsync(() => j1.State == JobState.Error);
        await Task.Delay(300);   // đủ thời gian — nếu KHÔNG dừng thì j2 đã in

        // Stop-on-error: j2 KHÔNG in — giữ Queued, queue tự pause chờ Resume
        Assert.Equal(JobState.Queued, j2.State);
        Assert.Equal(1, engine.Calls);
        Assert.True(q.IsPaused);
        Assert.True(q.StoppedByError);
        q.Dispose();
    }

    [Fact]
    public async Task ProcessBatch_Resume_ContinuesRemainingAfterError()
    {
        var q = new PrintQueue();
        var engine = new ConditionalEngine("bad.pdf");
        q.RegisterEngine(engine);
        var j1 = MakeJob("bad.pdf");
        var j2 = MakeJob("good.pdf");
        q.AddOnly(j1);
        q.AddOnly(j2);

        q.ProcessBatch(new[] { j1, j2 });
        await TestHelpers.WaitUntilAsync(() => j1.State == JobState.Error);
        Assert.Equal(1, engine.Calls);   // chỉ j1 chạy

        q.Resume();   // sửa lỗi xong → in tiếp các file còn lại
        await TestHelpers.WaitUntilAsync(() => j2.State == JobState.Done);
        Assert.Equal(2, engine.Calls);
        Assert.False(q.StoppedByError);
        q.Dispose();
    }

    [Fact]
    public async Task ProcessBatch_Pause_StopsBetweenJobs_ResumeContinues()
    {
        var q = new PrintQueue();
        var engine = new BlockingEngine();
        q.RegisterEngine(engine);
        var j1 = MakeJob("a.pdf");
        var j2 = MakeJob("b.pdf");
        q.AddOnly(j1);
        q.AddOnly(j2);

        q.ProcessBatch(new[] { j1, j2 });
        await engine.Started.Task;   // j1 đang in
        q.Pause();                   // bấm Pause trong khi j1 đang in
        engine.Release.SetResult(true);  // j1 xong → loop gặp Pause → j2 KHÔNG được in
        await TestHelpers.WaitUntilAsync(() => j1.State == JobState.Done);
        await Task.Delay(200);

        Assert.Equal(JobState.Queued, j2.State);
        Assert.Equal(1, engine.Calls);

        q.Resume();
        await TestHelpers.WaitUntilAsync(() => j2.State == JobState.Done);
        Assert.Equal(2, engine.Calls);
        q.Dispose();
    }

    // ================= RACE FIX (v0.2.5) — spam click Resume/Pause không được kẹt "Converting mãi" =================

    [Fact]
    public async Task PauseResume_SpamClick_DoesNotStickJobInConverting()
    {
        // Bug: spam Pause/Resume trong khi job đang Converting KHÔNG được làm job sau kẹt "Converting mãi"
        // (triệu chứng user gặp: "cứ báo converting mãi, ko in tiếp được").
        var q = new PrintQueue();
        var engine = new BlockingEngine();
        q.RegisterEngine(engine);
        var j1 = MakeJob("a.pdf");
        var j2 = MakeJob("b.pdf");
        q.AddOnly(j1);
        q.AddOnly(j2);

        q.ProcessBatch(new[] { j1, j2 });       // j1 Converting
        await engine.Started.Task;

        // Spam click: Pause/Resume liên tục trong lúc j1 đang in
        for (var i = 0; i < 10; i++)
        {
            q.Pause();
            q.Resume();
        }

        engine.Release.SetResult(true);          // j1 xong
        await TestHelpers.WaitUntilAsync(() => j1.State == JobState.Done);

        // Spam tiếp giữa j1 xong và j2 bắt đầu
        q.Pause();
        q.Resume();
        q.Pause();
        q.Resume();

        await TestHelpers.WaitUntilAsync(() => j2.State == JobState.Done, timeoutMs: 5000);
        Assert.Equal(JobState.Done, j2.State);   // KHÔNG kẹt Converting
        Assert.Equal(2, engine.Calls);
        q.Dispose();
    }

    [Fact]
    public async Task SubscriberThrows_DoesNotKillDrain_NextJobStillPrints()
    {
        // Bug: JobStateChanged?.Invoke chạy SYNCHRONOUS trong drain thread — subscriber ném exception
        // → drain loop chết → _drainRunning kẹt true → MỌI KickDrain sau no-op → job "Converting mãi".
        // Fix: SetState bọc invoke trong try/catch — subscriber lỗi không phá drain.
        var q = new PrintQueue();
        var engine = new TestHelpers.FakeEngine();
        q.RegisterEngine(engine);

        // Subscriber NÉM exception mỗi lần fire — trước fix: drain chết ngay job đầu.
        q.JobStateChanged += _ => throw new InvalidOperationException("subscriber boom");

        var j1 = MakeJob("a.pdf");
        var j2 = MakeJob("b.pdf");
        q.AddOnly(j1);
        q.AddOnly(j2);

        q.ProcessBatch(new[] { j1, j2 });
        await TestHelpers.WaitUntilAsync(() => j1.State == JobState.Done);
        await TestHelpers.WaitUntilAsync(() => j2.State == JobState.Done, timeoutMs: 5000);

        Assert.Equal(JobState.Done, j1.State);
        Assert.Equal(JobState.Done, j2.State);   // drain KHÔNG chết — job 2 vẫn in
        Assert.Equal(2, engine.Calls);
        q.Dispose();
    }

    [Fact]
    public async Task RemoveJob_ConvertingJob_QueueStillProcessesNext()
    {
        // Bug: RemoveJob job đang Converting (cancel token) — job sau phải vẫn được in (drain không chết).
        var q = new PrintQueue();
        var cancelable = new CancelableBlockingEngine();
        q.RegisterEngine(cancelable);
        var j1 = MakeJob("a.pdf");
        var j2 = MakeJob("b.pdf");
        q.AddOnly(j1);
        q.AddOnly(j2);

        q.ProcessBatch(new[] { j1, j2 });
        await cancelable.Started.Task;           // j1 Converting

        var removed = q.RemoveJob(j1);           // gỡ job đang in — cancel token thật
        Assert.True(removed);
        Assert.DoesNotContain(j1, q.Jobs);

        // Engine quan sát token → OCE → DrainLoopAsync chuyển j1 Cancelled (KHÔNG Done)
        await TestHelpers.WaitUntilAsync(() => j1.State == JobState.Cancelled, timeoutMs: 2000);
        await TestHelpers.WaitUntilAsync(() => j2.State == JobState.Done, timeoutMs: 5000);

        Assert.Equal(JobState.Cancelled, j1.State);
        Assert.Equal(JobState.Done, j2.State);   // job sau VẪN in — drain không kẹt
        Assert.Equal(2, cancelable.Calls);
        q.Dispose();
    }

    /// <summary>Engine block lần GỌI ĐẦU (j1) cho tới khi cancel; các lần sau trả Ok ngay (j2 in bình thường).
    /// Mô phỏng đúng engine thật: quan sát token (cancel → OCE) nhưng chỉ job đang in bị block.</summary>
    private sealed class CancelableBlockingEngine : IPrintEngine
    {
        public TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public bool CanHandle(string format) => true;
        public async Task<Result<bool>> PrintAsync(PrintJob job, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref Calls);
            if (n > 1) return Result<bool>.Ok(true);       // lần 2+ (j2) — in ngay
            Started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, ct);         // lần 1 (j1) — giữ Converting cho tới khi cancel → OCE
            return Result<bool>.Ok(true);
        }
    }
}