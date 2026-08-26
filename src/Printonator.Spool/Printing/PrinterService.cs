using System.Printing;
using Printonator.Core.Models;

namespace Printonator.Spool.Printing;

/// <summary>
/// Đọc danh sách máy in thật qua Windows Print Spooler:
/// LocalPrintServer.GetPrintQueues + GetPrintCapabilities (khổ giấy, duplex, màu, khay).
/// Không nuốt lỗi — lỗi nặng trả Result.Fail(PrintError), máy lỗi nhẹ ghi StatusDetail.
/// </summary>
public sealed class PrinterService
{
    /// <summary>Liệt kê máy in kèm trạng thái + khả năng đầy đủ.</summary>
    public Result<List<PrinterInfo>> ListPrinters()
    {
        var printers = new List<PrinterInfo>();
        try
        {
            using var server = new LocalPrintServer();
            var queues = server.GetPrintQueues();
            foreach (var q in queues)
            {
                try
                {
                    printers.Add(Describe(q));
                }
                catch (Exception ex)
                {
                    // Một máy lỗi không được làm chết cả danh sách — nhưng vẫn báo lỗi rõ trên máy đó
                    printers.Add(new PrinterInfo
                    {
                        Name = SafeName(q) ?? "(không đọc được tên)",
                        IsAvailable = false,
                        StatusDetail = $"Lỗi đọc thông tin: {ex.Message}",
                    });
                }
            }
        }
        catch (Exception ex)
        {
            return Result<List<PrinterInfo>>.Fail(new PrintError
            {
                Code = ErrorCodes.PrinterNotFound,
                Category = PrintErrorCategory.Printer,
                Message = "Không đọc được danh sách máy in từ Windows Spooler.",
                Hint = "Kiểm tra dịch vụ Print Spooler đang chạy (services.msc → Print Spooler).",
                Detail = ex.ToString(),
            });
        }
        return Result<List<PrinterInfo>>.Ok(printers);
    }

    /// <summary>Trạng thái 1 máy in — dùng để hiện dấu chấm xanh/đỏ trong UI và cho MCP.</summary>
    public Result<PrinterInfo> GetPrinter(string name)
    {
        var list = ListPrinters();
        if (!list.IsSuccess) return Result<PrinterInfo>.Fail(list.Error!);
        var found = list.Value!.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return found is null
            ? Result<PrinterInfo>.Fail(new PrintError
            {
                Code = ErrorCodes.PrinterNotFound,
                Category = PrintErrorCategory.Printer,
                Message = $"Không tìm thấy máy in \"{name}\".",
                Hint = "Bấm Scan printers để nạp lại danh sách máy in hiện có.",
            })
            : Result<PrinterInfo>.Ok(found);
    }

    private static string? SafeName(PrintQueue q)
    {
        try { return q.Name; } catch { return null; }
    }

    private static PrinterInfo Describe(PrintQueue q)
    {
        var caps = q.GetPrintCapabilities();
        var status = TryGetStatus(q);
        var available = string.IsNullOrEmpty(status);
        var trays = Trays(caps.InputBinCapability);

        return new PrinterInfo
        {
            Name = q.Name,
            IsAvailable = available,
            StatusDetail = status,
            SupportsDuplex = caps.DuplexingCapability.Contains(Duplexing.TwoSidedLongEdge)
                          || caps.DuplexingCapability.Contains(Duplexing.TwoSidedShortEdge),
            SupportsColor = caps.OutputColorCapability.Contains(OutputColor.Color),
            SupportedPaperSizes = PaperNames(caps.PageMediaSizeCapability),
            TrayInfo = trays.Length == 0 ? null : string.Join(", ", trays),
            Trays = trays,
            IsVirtual = IsVirtualPrinter(q.Name),
        };
    }

    private static string? TryGetStatus(PrintQueue q)
    {
        try
        {
            if (q.IsOffline) return "Offline";
            if (q.IsNotAvailable) return "Không khả dụng";
            var s = q.QueueStatus;
            if (s.HasFlag(PrintQueueStatus.DoorOpen)) return "Cửa mở";
            if (s.HasFlag(PrintQueueStatus.NoToner)) return "Hết mực/toner";
            if (s.HasFlag(PrintQueueStatus.PaperOut) || s.HasFlag(PrintQueueStatus.PaperProblem)) return "Hết giấy/vấn đề giấy";
            if (s.HasFlag(PrintQueueStatus.PaperJam)) return "Kẹt giấy";
            if (s.HasFlag(PrintQueueStatus.Error)) return "Lỗi máy in";
            return null; // sẵn sàng
        }
        catch
        {
            return null;
        }
    }

    private static string[] PaperNames(IEnumerable<PageMediaSize>? sizes)
    {
        var names = new List<string>();
        if (sizes is null) return ["A4"];
        foreach (var ps in sizes)
        {
            if (ps.PageMediaSizeName is not { } name) continue; // kích thước tự do — bỏ qua
            var friendly = FriendlyPaperName(name);
            if (friendly is not null && !names.Contains(friendly)) names.Add(friendly);
        }
        if (names.Count == 0) names.Add("A4");
        return names.ToArray();
    }

    /// <summary>Enum PageMediaSizeName → tên hiển thị (ISOA4 → A4, NALetter → Letter...).</summary>
    private static string? FriendlyPaperName(PageMediaSizeName name)
    {
        var s = name.ToString(); // vd ISOA4, NALetter, NALegal, JISOB5...
        if (s.StartsWith("ISO", StringComparison.Ordinal)) return s[3..];     // A4, A3, B5...
        if (s.StartsWith("NA", StringComparison.Ordinal)) return s[2..];      // Letter, Legal...
        if (s.StartsWith("JIS", StringComparison.Ordinal)) return s[3..];     // B5...
        if (s.StartsWith("OtherMetric", StringComparison.Ordinal) && s.Length > 11)
            return s[11..];
        return s.Length <= 5 ? s : null; // quá dài/custom → không hiện
    }

    /// <summary>
    /// Danh sách khay giấy (tên thân thiện tiếng Việt khi biết, ngược lại tên enum).
    /// Dùng cho UI chọn "Paper source" và cho MCP list_printers.
    /// </summary>
    private static string[] Trays(IEnumerable<InputBin>? bins)
    {
        if (bins is null) return [];
        return bins
            .Select(FriendlyTrayName)
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct()
            .ToArray();
    }

    /// <summary>InputBin → tên hiển thị (chỉ có 6 giá trị trong System.Printing .NET Core).</summary>
    internal static string FriendlyTrayName(InputBin bin) => bin switch
    {
        InputBin.AutoSelect => "Tự chọn (Auto Select)",
        InputBin.AutoSheetFeeder => "Nạp tự động (ASF)",
        InputBin.Cassette => "Khay cassette",
        InputBin.Tractor => "Tractor (giấy liên tục)",
        InputBin.Manual => "Nạp tay (Manual)",
        InputBin.Unknown => "",
        _ => bin.ToString(),
    };

    private static bool IsVirtualPrinter(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("pdf") || n.Contains("xps") || n.Contains("onenote")
            || n.Contains("fax") || n.Contains("adobe");
    }
}