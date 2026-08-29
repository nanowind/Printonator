using Printonator.Core;
using Printonator.Core.Models;
using Xunit;

namespace Printonator.Core.Tests;

/// <summary>
/// Route lỗi engine qua Core: PrintErrorException (lỗi đã phân loại) phải GIỮ NGUYÊN mã lỗi
/// (PRINTER_OFFLINE, FILE_LOCKED...) qua boundary async — KHÔNG bị WrapError nuốt thành SPOOLER_FAILED.
/// Exception trần vẫn bọc SPOOLER_FAILED (regression guard).
/// </summary>
public class PrintQueueErrorRoutingTests
{
    private static PrintJob MakeJob(string name = "a.pdf")
        => new()
        {
            FilePath = $"C:\\{name}",
            FileName = name,
            Format = "PDF",
            Config = new PrintConfig { PrinterName = "Microsoft Print to PDF", Copies = 1 },
            PageCount = 1,
        };

    [Fact]
    public async Task EngineThrowsPrintErrorException_PreservesSpecificCode()
    {
        var q = new PrintQueue();
        var specific = new PrintError
        {
            Code = ErrorCodes.PrinterOffline,
            Category = PrintErrorCategory.Printer,
            Message = "Máy in đang offline.",
            Hint = "Bật máy in lên rồi thử lại.",
        };
        q.RegisterEngine(new TestHelpers.ThrowingPrintErrorEngine(specific));
        var job = MakeJob();

        q.Enqueue(job);
        await TestHelpers.WaitUntilAsync(() => job.State == JobState.Error);

        Assert.Equal(JobState.Error, job.State);
        Assert.NotNull(job.Error);
        Assert.Equal(ErrorCodes.PrinterOffline, job.Error!.Code);   // giữ nguyên — không SPOOLER_FAILED
        Assert.Equal(PrintErrorCategory.Printer, job.Error.Category);
        Assert.Equal("Máy in đang offline.", job.Error.Message);
        Assert.Equal("Bật máy in lên rồi thử lại.", job.Error.Hint);
        q.Dispose();
    }

    [Fact]
    public async Task EngineThrowsRawException_StillWrapsSpoolerFailed()
    {
        var q = new PrintQueue();
        q.RegisterEngine(new TestHelpers.ThrowingRawEngine());
        var job = MakeJob();

        q.Enqueue(job);
        await TestHelpers.WaitUntilAsync(() => job.State == JobState.Error);

        Assert.Equal(JobState.Error, job.State);
        Assert.NotNull(job.Error);
        Assert.Equal(ErrorCodes.SpoolerFailed, job.Error!.Code);   // exception trần → bọc chung như cũ
        q.Dispose();
    }
}