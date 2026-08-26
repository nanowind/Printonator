using System.Text.Json;
using Printonator.Core.Models;
using Printonator.Core.Safety;
using Xunit;

namespace Printonator.Core.Tests;

/// <summary>
/// Unit test PrintGuard — an toàn AI in: allowlist, quota trang (cộng dồn + ngân sách file-chưa-rõ),
/// max file, approve/standalone fail-closed, config từ file/env.
/// </summary>
public class PrintGuardTests
{
    private static PrintJob Job(int pageCount, int copies = 1, string printer = "HP404") => new()
    {
        FilePath = $"C:\\f_{Guid.NewGuid():N}.pdf",
        FileName = "f.pdf",
        Format = "PDF",
        Config = new PrintConfig { PrinterName = printer, Copies = copies, PageRange = "All" },
        PageCount = pageCount,
    };

    private static PrintGuard Guard(McpGuardConfig cfg) => new(cfg, null);

    private static McpGuardConfig Cfg(
        string[]? allowed = null,
        int maxPages = 100,
        int maxFiles = 10,
        bool requireApprove = true) => new()
        {
            AllowedPrinters = allowed ?? [],
            MaxPagesPerBatch = maxPages,
            MaxFilesPerBatch = maxFiles,
            RequireApprove = requireApprove,
        };

    [Fact]
    public void Allowlist_CaseInsensitive_Matches()
    {
        var g = Guard(Cfg(allowed: ["HP LaserJet Pro M404"]));
        Assert.Null(g.Validate("hp laserjet pro m404", [Job(1)]));
        Assert.Equal(ErrorCodes.PrinterNoPermission, g.Validate("Canon LBP", [Job(1)])!.Code);
    }

    [Fact]
    public void NullPrinter_Passes_WhenAllowedEmpty()
    {
        var g = Guard(Cfg(allowed: [], requireApprove: false));
        Assert.Null(g.Validate(null, [Job(1)]));
    }

    [Fact]
    public void Quota_Counts_Pages_Times_Copies()
    {
        // 4 trang × 3 bản × 2 file = 24 > 20 → chặn
        var g = Guard(Cfg(allowed: ["HP404"], maxPages: 20));
        var jobs = new[] { Job(4, copies: 3), Job(4, copies: 3) };
        Assert.Equal(ErrorCodes.MaxBatchExceeded, g.Validate("HP404", jobs)!.Code);
    }

    [Fact]
    public void Quota_Accumulates_Pending_And_Batch()
    {
        var g = Guard(Cfg(allowed: ["HP404"], maxPages: 10));
        // lô mới 4 trang + 5 đang chờ = 9 ≤ 10 → cho
        Assert.Null(g.Validate("HP404", [Job(4)], alreadyPendingPages: 5));
        // lô mới 4 + 8 đang chờ = 12 > 10 → chặn
        Assert.Equal(ErrorCodes.MaxBatchExceeded, g.Validate("HP404", [Job(4)], alreadyPendingPages: 8)!.Code);
    }

    [Fact]
    public void UnknownPageCount_Uses_ConservativeBudget()
    {
        // File PageCount=0 → ước lượng 50 trang/file (fail-closed, không "1 trang")
        var small = Guard(Cfg(allowed: ["HP404"], maxPages: 60));
        Assert.Null(small.Validate("HP404", [Job(0)]));          // 50 ≤ 60
        Assert.Equal(ErrorCodes.MaxBatchExceeded, small.Validate("HP404", [Job(0), Job(0)])!.Code); // 100 > 60

        var unlimited = Guard(Cfg(allowed: ["HP404"], maxPages: 0));
        Assert.Null(unlimited.Validate("HP404", [Job(0)]));
    }

    [Fact]
    public void MaxFiles_PerBatch_Blocks_TooMany()
    {
        var g = Guard(Cfg(allowed: ["HP404"], maxPages: 0, maxFiles: 2));
        Assert.Null(g.Validate("HP404", [Job(1), Job(1)]));
        Assert.Equal(ErrorCodes.MaxBatchExceeded, g.Validate("HP404", [Job(1), Job(1), Job(1)])!.Code);
    }

    [Fact]
    public void EmptyJobs_Returns_NoFiles()
    {
        var g = Guard(Cfg());
        Assert.Equal(ErrorCodes.NoFilesSelected, g.Validate("HP404", [])!.Code);
    }

    [Theory]
    [InlineData(true, true)]   // có duyệt → tự in OK (người duyệt)
    [InlineData(false, false)] // không duyệt + allowlist rỗng → TỪ CHỐI (fail-closed)
    [InlineData(false, true)]  // không duyệt + có allowlist → cho tự in
    public void StandaloneAutoPrint_Requires_Approve_Or_Allowlist(bool requireApprove, bool allowed)
    {
        var cfg = new McpGuardConfig
        {
            RequireApprove = requireApprove,
            AllowedPrinters = allowed ? ["HP404"] : [],
        };
        Assert.Equal(allowed, cfg.IsStandaloneAutoPrintAllowed());
    }

    [Fact]
    public void Load_FromFile_Parses()
    {
        var path = Path.Combine(Path.GetTempPath(), $"guard_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new McpGuardConfig
        {
            AllowedPrinters = ["HP404"],
            RequireApprove = false,
            MaxPagesPerBatch = 42,
        }));
        try
        {
            var cfg = McpGuardConfig.Load(path);
            Assert.False(cfg.RequireApprove);
            Assert.Equal(["HP404"], cfg.AllowedPrinters);
            Assert.Equal(42, cfg.MaxPagesPerBatch);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_CorruptFile_FallsBack_Safe()
    {
        var path = Path.Combine(Path.GetTempPath(), $"guard_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "not json {{{");
        try
        {
            var cfg = McpGuardConfig.Load(path);
            Assert.True(cfg.RequireApprove); // an toàn mặc định — không fail-open
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AuditLogger_Writes_JsonLine_NoSecretInExercise()
    {
        var path = Path.Combine(Path.GetTempPath(), $"audit_{Guid.NewGuid():N}.log");
        try
        {
            var logger = new AuditLogger(path);
            logger.Log("print_files", "blocked", new Dictionary<string, object?>
            {
                ["fileCount"] = 2,
                ["printer"] = "HP404",
            });
            var line = File.ReadAllText(path);
            var entry = JsonSerializer.Deserialize<JsonElement>(line);
            Assert.Equal("print_files", entry.GetProperty("tool").GetString());
            Assert.Equal("blocked", entry.GetProperty("outcome").GetString());
            Assert.Equal(2, entry.GetProperty("fileCount").GetInt32());
            Assert.Equal("HP404", entry.GetProperty("printer").GetString());
            // Không lộ đường dẫn đầy đủ/username trong dòng audit
            Assert.DoesNotContain("C:\\\\", line);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Copies_Clamp_IsConfigurable()
    {
        var cfg = Cfg(allowed: ["HP404"]);
        Assert.Equal(100, cfg.MaxCopiesPerFile);
        // Tool (PrintTools) dùng MaxCopiesPerFile để chặn copies lớn trước khi vào queue — đây chỉ là ceil guard
        Assert.True(cfg.MaxCopiesPerFile > 0);
    }
}