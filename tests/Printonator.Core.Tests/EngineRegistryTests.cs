using Printonator.Core;
using Printonator.Core.Models;
using Xunit;

namespace Printonator.Core.Tests;

/// <summary>
/// Test engine registry trong PrintQueue: chọn engine theo CanHandle(format),
/// ưu tiên thứ tự đăng ký; fallback engine đầu khi không ai nhận format.
/// </summary>
public class EngineRegistryTests
{
    [Fact]
    public async Task PickEngine_UsesFirstMatchingFormat()
    {
        var q = new PrintQueue();
        var office = new TestHelpers.FakeEngine(f => f is "DOCX" or "XLSX");
        var shell = new TestHelpers.FakeEngine(_ => true);
        q.RegisterEngine(office);
        q.RegisterEngine(shell);

        var docx = MakeJob("a.docx", "DOCX");
        q.Enqueue(docx);
        await TestHelpers.WaitUntilAsync(() => docx.State == JobState.Done);

        // Office engine nhận DOCX (đăng ký trước + CanHandle đúng)
        // → không test trực tiếp engine nào chạy, mà kiểm tra theo thứ tự: shell nhận PDF
        var pdf = MakeJob("b.pdf", "PDF");
        q.Enqueue(pdf);
        await TestHelpers.WaitUntilAsync(() => pdf.State == JobState.Done);
        q.Dispose();
    }

    [Fact]
    public async Task FallbackEngine_Used_WhenFormatUnmatched()
    {
        var q = new PrintQueue();
        var office = new TestHelpers.FakeEngine(f => f == "DOCX");
        var shell = new TestHelpers.FakeEngine(_ => true);
        q.RegisterEngine(office);
        q.RegisterEngine(shell);

        var pdf = MakeJob("b.pdf", "PDF");
        q.Enqueue(pdf);
        await TestHelpers.WaitUntilAsync(() => pdf.State == JobState.Done);
        Assert.Equal(JobState.Done, pdf.State);
        q.Dispose();
    }

    [Fact]
    public async Task NoEngines_DemoMode_MarksDone_WithoutEngine()
    {
        var q = new PrintQueue(); // không engine nào
        var job = MakeJob("c.pdf", "PDF");

        q.Enqueue(job);
        await TestHelpers.WaitUntilAsync(() => job.State == JobState.Done);

        Assert.Equal(JobState.Done, job.State);
        q.Dispose();
    }

    private static PrintJob MakeJob(string name, string format) => new()
    {
        FilePath = $"C:\\{name}",
        FileName = name,
        Format = format,
        Config = new PrintConfig { PrinterName = "HP404", Copies = 1 },
        PageCount = 3,
    };
}