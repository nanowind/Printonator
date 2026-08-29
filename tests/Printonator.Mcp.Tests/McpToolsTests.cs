using System.Reflection;
using Printonator.Core.Models;
using Printonator.Mcp;
using Xunit;

namespace Printonator.Mcp.Tests;

/// <summary>
/// Test các tool MCP (qua thư mục Printonator.Mcp) — KHÔNG đụng spooler thật.
/// Test phần logic thuần + shape lỗi + đăng ký tool.
/// </summary>
public class McpToolsTests
{
    private static readonly string[] VirtualNames = ["Microsoft Print to PDF", "Microsoft XPS Document Writer"];

    private static PrinterInfo MakePrinter(
        string name, bool available = true, bool virtualPrinter = false,
        bool duplex = false, bool color = false, string[]? paper = null, bool isDefault = false)
        => new()
        {
            Name = name,
            IsAvailable = available,
            SupportsDuplex = duplex,
            SupportsColor = color,
            SupportedPaperSizes = paper ?? ["A4", "A3"],
            IsVirtual = virtualPrinter,
            IsDefault = isDefault,
        };

    // ============ PickBestPrinter (logic thuần — không cần spooler) ============

    [Fact]
    public void PickBestPrinter_Prefers_AvailablePhysical()
    {
        var printers = new[]
        {
            MakePrinter("PDF", virtualPrinter: true, available: true),
            MakePrinter("Canon LBP", available: true),
            MakePrinter("HP Offline", available: false),
        };

        var picked = PrintTools.PickBestPrinter(printers, null, false, false);

        Assert.NotNull(picked);
        Assert.Equal("Canon LBP", picked.Name);   // vật lý available thắng máy ảo
    }

    [Fact]
    public void PickBestPrinter_FiltersByDuplexColorPaper()
    {
        var printers = new[]
        {
            MakePrinter("Mono", available: true, duplex: false, color: false),
            MakePrinter("DuplexColor", available: true, duplex: true, color: true),
        };

        Assert.Equal("DuplexColor", PrintTools.PickBestPrinter(printers, "A4", requireDuplex: true, requireColor: true)!.Name);
        Assert.Equal("Mono", PrintTools.PickBestPrinter(printers, "A4", requireDuplex: false, requireColor: false)!.Name);
    }

    [Fact]
    public void PickBestPrinter_NoCandidate_ReturnsNull()
    {
        var printers = new[] { MakePrinter("Only", available: true, duplex: false, color: false) };
        Assert.Null(PrintTools.PickBestPrinter(printers, "A4", requireDuplex: true, requireColor: false));
    }

    // ============ GetErrorReference (tra cứu lỗi cho AI) ============

    [Fact]
    public void GetErrorReference_HasRowForEveryErrorCode()
    {
        // Mọi hằng số ErrorCodes đều có dòng tra cứu — chống lệch bảng lỗi cho AI.
        var codes = typeof(ErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

        var result = (Dictionary<string, object?>)PrintTools.GetErrorReference();
        var rows = (IEnumerable<object>)result["codes"]!;
        var listed = rows.Cast<Dictionary<string, object?>>().Select(r => (string)r["code"]!).ToHashSet();

        foreach (var code in codes)
            Assert.True(listed.Contains(code), $"ErrorReference thiếu mã {code}");
    }

    [Fact]
    public void GetErrorReference_FiltersByCode()
    {
        var result = (Dictionary<string, object?>)PrintTools.GetErrorReference("JOB_NOT_FOUND");
        var rows = ((IEnumerable<object>)result["codes"]!).Cast<Dictionary<string, object?>>().ToList();

        Assert.Single(rows);
        Assert.Equal("JOB_NOT_FOUND", rows[0]["code"]);
        Assert.NotEmpty((string)rows[0]["aiAction"]!);
    }

    // ============ GetGuardConfig ============

    [Fact]
    public void GetGuardConfig_ReturnsShape()
    {
        var result = (Dictionary<string, object?>)PrintTools.GetGuardConfig();

        Assert.True((bool)result["ok"]!);
        Assert.True(result.ContainsKey("requireApprove"));
        Assert.True(result.ContainsKey("canAutoPrint"));
        Assert.True(result.ContainsKey("allowedPrinters"));
        Assert.True(result.ContainsKey("maxPagesPerBatch"));
        Assert.True(result.ContainsKey("maxFilesPerBatch"));
        Assert.True(result.ContainsKey("maxCopiesPerFile"));
    }
}