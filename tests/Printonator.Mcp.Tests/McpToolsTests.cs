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
            DuplexSupport = duplex ? CapabilitySupport.Supported : CapabilitySupport.NotSupported,
            ColorSupport = color ? CapabilitySupport.Supported : CapabilitySupport.NotSupported,
            SupportedPaperSizes = paper ?? ["A4", "A3"],
            IsVirtual = virtualPrinter,
            IsDefault = isDefault,
        };

    /// <summary>
    /// list_printers KHÔNG được quảng cáo khổ mà print_files sẽ từ chối: AI đọc danh sách rồi
    /// truyền lại đúng tên đó, nhận INVALID_PAPER_SIZE là hai tool tự mâu thuẫn. Đây là test khoá
    /// đúng dòng emission (revert `PrinterDto` về p.SupportedPaperSizes là test này đỏ).
    /// </summary>
    [Fact]
    public void PrinterDto_chi_quang_cao_kho_dat_duoc()
    {
        var printer = MakePrinter("Label-1", paper: ["A4", "A3", "Invoice", "4x6", "B5Extra", "SuperA"]);

        var dto = PrintTools.PrinterDto(printer);
        var paper = (IReadOnlyList<string>)dto["paper"]!;

        Assert.Contains("A4", paper);
        Assert.Contains("A3", paper);
        Assert.DoesNotContain("Invoice", paper);
        Assert.DoesNotContain("4x6", paper);
        Assert.DoesNotContain("B5Extra", paper);
        Assert.DoesNotContain("SuperA", paper);
        // Bất biến: mọi tên quảng cáo phải qua được kiểm tra của print_files.
        Assert.All(paper, n => Assert.NotNull(PaperCatalog.Dimensions(n)));
    }

    /// <summary>Máy in chỉ báo khổ riêng (label printer) → danh sách RỖNG, không bịa "A4".
    /// Bịa A4 là hứa in được trên máy chỉ có 4x6.</summary>
    [Fact]
    public void PrinterDto_may_in_chi_co_kho_rieng_tra_danh_sach_rong()
    {
        var dto = PrintTools.PrinterDto(MakePrinter("Label-2", paper: ["4x6", "Invoice"]));

        Assert.Empty((IReadOnlyList<string>)dto["paper"]!);
    }

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
        var rowList = rows.Cast<Dictionary<string, object?>>().Select(r => (string)r["code"]!).ToList();
        var listed = rowList.ToHashSet();

        foreach (var code in codes)
            Assert.True(listed.Contains(code), $"ErrorReference thiếu mã {code}");

        // Mã KHÔNG được trùng: SuggestedAction tra bằng FirstOrDefault → dòng trùng làm gợi ý thành tùy tiện.
        var dupes = rowList.GroupBy(c => c).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dupes.Count == 0, $"ErrorReference trùng mã: {string.Join(", ", dupes)}");
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

    // ============ print_files khi RequireApprove=true (mặc định) ============

    /// <summary>
    /// Mặc định guard bắt buộc duyệt. MCP server chạy ở tiến trình riêng, không chia hàng đợi với
    /// app Printonator → job chờ duyệt sẽ kẹt vĩnh viễn. Vì vậy print_files PHẢI trả lỗi
    /// APPROVAL_REQUIRED thay vì ok:true — nếu trả ok:true thì AI tưởng đã in và KHÔNG in gì ra giấy.
    /// Test này chạy với env mặc định (không đặt PRINTONATOR_REQUIRE_APPROVE=false).
    /// </summary>
    [Fact]
    public async Task PrintFiles_RequireApproveDefault_ReturnsApprovalRequired()
    {
        if (AppServices.GuardConfig.RequireApprove == false)
            return;   // máy dev có env riêng → bỏ qua, test này chỉ có nghĩa với cấu hình mặc định

        var file = Path.Combine(Path.GetTempPath(), $"printonator-mcptest-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "x");
        try
        {
            var result = (Dictionary<string, object?>)await PrintTools.PrintFiles([file]);

            Assert.False((bool)result["ok"]!);
            var err = (Dictionary<string, object?>)result["error"]!;
            Assert.Equal(ErrorCodes.ApprovalRequired, err["code"]);
            Assert.NotEmpty((string)err["hint"]!);
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    /// <summary>
    /// Khổ giấy lạ KHÔNG được lọt xuống engine: PaperCatalog.Dimensions trả null → engine bỏ qua khổ
    /// và in theo khổ driver, còn AI nhận ok:true và tưởng đã in đúng khổ nó yêu cầu. Phải trả lỗi
    /// có mã + gợi ý danh sách khổ hợp lệ. Test chạy TRƯỚC cổng duyệt/allowlist nên không cần env.
    /// </summary>
    [Theory]
    [InlineData("A4x")]            // gõ sai
    [InlineData("khổ gì cũng được")] // AI nói mơ hồ
    [InlineData("A99")]            // tên không tồn tại
    public async Task PrintFiles_PaperKhongCoTrongDanhMuc_TraLoiInvalidPaperSize(string paper)
    {
        var file = Path.Combine(Path.GetTempPath(), $"printonator-mcptest-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "x");
        try
        {
            var result = (Dictionary<string, object?>)await PrintTools.PrintFiles([file], paper: paper);

            Assert.False((bool)result["ok"]!);
            var err = (Dictionary<string, object?>)result["error"]!;
            Assert.Equal(ErrorCodes.InvalidPaperSize, err["code"]);
            // Hint phải liệt kê khổ hợp lệ để AI tự sửa ở lần gọi sau.
            Assert.Contains("A4", (string)err["hint"]!);
            Assert.Contains("A3", (string)err["hint"]!);
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    /// <summary>Tên khổ hợp lệ và "AsDocument" phải đi qua — không được chặn oan.
    /// CHỈ chạy khi guard chắc chắn chặn (RequireApprove=true): nếu môi trường dev cho phép tự in thì
    /// test sẽ gửi file thật tới máy in. Khi guard chặn, khẳng định ĐÚNG mã lỗi để khoá cả THỨ TỰ
    /// (kiểm tra khổ phải chạy TRƯỚC cổng duyệt) — nếu ai dời nó ra sau, test này đỏ ngay.</summary>
    [Theory]
    [InlineData("A4")]
    [InlineData("a4")]           // danh mục không phân biệt hoa thường
    [InlineData("Letter")]
    [InlineData("AsDocument")]
    [InlineData("asdocument")]   // phải chuẩn hoá về hằng số, không giữ chuỗi thô
    public async Task PrintFiles_PaperHopLe_KhongBiChan(string paper)
    {
        if (!AppServices.GuardConfig.RequireApprove)
            return;   // môi trường cho phép tự in → bỏ qua, tránh in thật

        var file = Path.Combine(Path.GetTempPath(), $"printonator-mcptest-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "x");
        try
        {
            var result = (Dictionary<string, object?>)await PrintTools.PrintFiles([file], paper: paper);

            Assert.False((bool)result["ok"]!);
            var err = (Dictionary<string, object?>)result["error"]!;
            // Bị chặn bởi CỔNG DUYỆT chứ không phải bởi kiểm tra khổ giấy.
            Assert.Equal(ErrorCodes.ApprovalRequired, err["code"]);
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }
}