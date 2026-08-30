using Printonator.Core;
using Printonator.Core.Models;
using Xunit;

namespace Printonator.Core.Tests;

/// <summary>
/// T0.3 — CancelJob/RemoveJob DỪNG engine thật qua real CancellationToken.
/// Trước fix: PrintOnceAsync truyền CancellationToken.None → cancel job đang in vô hiệu
/// (engine chạy tới xong / treo vô thời hạn). Sau fix: per-job CTS được cancel → engine
/// (đang chờ ở điểm chờ) ném OCE → DrainLoopAsync chuyển job sang Cancelled, KHÔNG retry,
/// KHÔNG đánh dấu Done/Error. Các test dùng engine fake block vô thời hạn: nếu cancel không
/// hoạt động, test fail (không bao giờ về trạng thái cuối trong timeout).
/// </summary>
public class PrintQueueCancelTests
{
    private static PrintJob MakeJob(string name = "a.pdf")
        => new()
        {
            FilePath = $"C:\\{name}",
            FileName = name,
            Format = "PDF",
            Config = new PrintConfig { PrinterName = "Microsoft Print to PDF", Copies = 1 },
            PageCount = 3,
        };

    /// <summary>Engine block vô thời hạn cho tới khi token bị cancel — mô phỏng in THẬT lâu.
    /// Nếu không được cancel: in vô hạn → test fail sau timeout (không "chờ xong" ảo).</summary>
    private sealed class HangUntilCancelledEngine : IPrintEngine
    {
        public readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public bool CanHandle(string format) => true;
        public async Task<Result<bool>> PrintAsync(PrintJob job, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, ct);   // đợi cancel — ném TaskCanceledException khi bị hủy
            return Result<bool>.Ok(true);
        }
    }

    [Fact]
    public async Task CancelJob_Stops_RunningEngine_JobGoesCancelled()
    {
        var q = new PrintQueue();
        var engine = new HangUntilCancelledEngine();
        q.RegisterEngine(engine);
        var job = MakeJob();

        q.Enqueue(job);
        await engine.Started.Task;                    // engine ĐANG in (block vô hạn)

        var cancelled = q.CancelJob(job);             // hủy → phải cancel token thật
        Assert.True(cancelled);

        await TestHelpers.WaitUntilAsync(() => job.State == JobState.Cancelled, timeoutMs: 2000);
        Assert.Equal(JobState.Cancelled, job.State);  // KHÔNG Done, KHÔNG Error
        Assert.Equal(1, engine.Calls);                // cancel KHÔNG retry (đúng 1 lần in)
        Assert.Contains(job, q.Jobs);                 // cancel ≠ remove — job còn trong list
        await Task.Delay(100);                        // chờ drain thoát hẳn trước khi Dispose
        q.Dispose();
    }

    [Fact]
    public async Task CancelJob_JobStillConverting_EngineReceivesCancellation()
    {
        // Variant: bấm hủy NGAY khi job mới chuyển Converting (token per-job phải sẵn sàng
        // trước khi engine chạy — CancelJobLocked đọc _jobCts trong lock, ta ghi trong lock).
        var q = new PrintQueue();
        var engine = new HangUntilCancelledEngine();
        q.RegisterEngine(engine);
        var job = MakeJob();

        q.Enqueue(job);
        await engine.Started.Task;
        Assert.Equal(JobState.Converting, job.State);

        q.CancelJob(job);
        await TestHelpers.WaitUntilAsync(() => job.State == JobState.Cancelled, timeoutMs: 2000);
        Assert.Equal(JobState.Cancelled, job.State);
        q.Dispose();
    }

    [Fact]
    public async Task RemoveJob_WhilePrinting_CancelsEngine_RemovesFromList()
    {
        var q = new PrintQueue();
        var engine = new HangUntilCancelledEngine();
        q.RegisterEngine(engine);
        var job = MakeJob();

        q.Enqueue(job);
        await engine.Started.Task;                    // đang in → RemoveJob phải cancel token thật

        var removed = q.RemoveJob(job);
        Assert.True(removed);
        Assert.DoesNotContain(job, q.Jobs);           // gỡ khỏi list

        // Về Cancelled = OCE từ engine (token bị cancel thật) → DrainLoopAsync đánh dấu Cancelled,
        // KHÔNG Done. Nếu cancel không hoạt động, engine block vô hạn → test fail sau timeout.
        await TestHelpers.WaitUntilAsync(() => job.State == JobState.Cancelled, timeoutMs: 2000);
        Assert.Equal(1, engine.Calls);                // không retry
        await Task.Delay(100);                        // chờ drain thoát hẳn trước khi Dispose
        q.Dispose();
    }
}
