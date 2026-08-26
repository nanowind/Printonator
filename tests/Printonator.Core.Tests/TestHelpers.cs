using Printonator.Core;
using Printonator.Core.Models;
using Xunit;

namespace Printonator.Core.Tests;

/// <summary>
/// Helper test dùng chung — tránh copy trong từng file test.
/// LƯU Ý: fake engine tạo instance MỚI mỗi test (biến đếm Calls theo test)
/// vì xUnit chạy song song các test class — KHÔNG dùng shared static.
/// </summary>
public static class TestHelpers
{
    public static async Task WaitUntilAsync(Func<bool> cond, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!cond() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.True(cond(), $"Condition not met within {timeoutMs}ms");
    }

    /// <summary>Engine in giả OK — nhận tuỳ chọn canHandle(format).</summary>
    public sealed class FakeEngine : IPrintEngine
    {
        private readonly Func<string, bool> _can;

        public FakeEngine(Func<string, bool>? canHandle = null)
            => _can = canHandle ?? (_ => true);

        public int Calls;
        public bool CanHandle(string format) => _can(format);
        public Task<Result<bool>> PrintAsync(PrintJob job, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Result<bool>.Ok(true));
        }
    }

    /// <summary>Engine luôn FAIL — test đường lỗi.</summary>
    public sealed class FailingEngine : IPrintEngine
    {
        public bool CanHandle(string format) => true;
        public Task<Result<bool>> PrintAsync(PrintJob job, CancellationToken ct)
            => Task.FromResult(Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.SpoolerFailed,
                Category = PrintErrorCategory.App,
                Message = "Fail",
                Hint = "Hint",
            }));
    }
}