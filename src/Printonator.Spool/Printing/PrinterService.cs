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
    /// <summary>StatusDetail khi máy in không phản hồi trong hạn chờ — UI dùng để nhận diện & báo "bị chặn".</summary>
    public const string TimeoutStatusDetail = "Không phản hồi (quá thời gian chờ — có thể bị firewall chặn)";

    /// <summary>Liệt kê máy in kèm trạng thái + khả năng đầy đủ.</summary>
    public Result<List<PrinterInfo>> ListPrinters()
    {
        var printers = new List<PrinterInfo>();
        string? defaultName = null;
        try
        {
            using var server = new LocalPrintServer();
            defaultName = SafeQueueName(server.DefaultPrintQueue);
            // Chỉ lấy TÊN máy in trên thread enum (rẻ — không gọi driver/device). MỖI máy được MỞ
            // FRESH trên worker thread của nó. QUAN TRỌNG: PrintQueue/GetPrintCapabilities có THREAD
            // AFFINITY — dùng PrintQueue từ thread KHÁC với thread tạo nó sẽ ném lỗi trên MỌI máy
            // (regression v0.1.3: chuyển Describe sang Task.Run nhưng vẫn dùng PrintQueue cũ của enum).
            var names = new List<string>();
            foreach (var q in server.GetPrintQueues())
            {
                var n = SafeName(q);
                if (!string.IsNullOrWhiteSpace(n)) names.Add(n);
            }

            // Budget TỔNG cho cả scan: máy in MẠNG bị firewall (vd Avast) chặn có thể treo mở queue
            // vô hạn — không để 1 máy làm đứng cả danh sách. Mỗi máy chạy watchdog 4s; hết budget 15s
            // → hủy toàn bộ, trả danh sách đã quét được.
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            foreach (var name in names)
            {
                try
                {
                    // Mở LocalPrintServer + PrintQueue MỚI trên CHÍNH worker thread → đúng thread
                    // affinity. DescribeFresh không bao giờ ném → watchdog treo thì thread bị bỏ rơi
                    // nhưng không fault (tránh unobserved task exception) và không đụng state chung.
                    var task = Task.Run(() => DescribeFresh(name, defaultName));
                    printers.Add(task.WaitAsync(TimeSpan.FromSeconds(4), budget.Token).GetAwaiter().GetResult());
                }
                catch (OperationCanceledException)
                {
                    break; // hết budget — trả danh sách đã quét được
                }
                catch (TimeoutException)
                {
                    // Máy không phản hồi trong 4s (firewall chặn / offline) → VẪN hiện để user thấy
                    // + bấm Retry; không nuốt im (quy ước "mỗi máy hiện rõ, không nuốt lỗi").
                    printers.Add(new PrinterInfo
                    {
                        Name = name,
                        IsAvailable = false,
                        StatusDetail = TimeoutStatusDetail,
                    });
                }
                catch (Exception ex)
                {
                    // Một máy lỗi không được làm chết cả danh sách — nhưng vẫn báo lỗi rõ trên máy đó
                    printers.Add(new PrinterInfo
                    {
                        Name = name,
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

    private static string? SafeQueueName(PrintQueue? q)
    {
        try { return q?.Name; } catch { return null; }
    }

    private static PrinterInfo Describe(PrintQueue q, string? defaultName)
    {
        var caps = q.GetPrintCapabilities();
        var status = TryGetStatus(q);
        var available = string.IsNullOrEmpty(status);
        var trays = Trays(caps.InputBinCapability);

        return new PrinterInfo
        {
            Name = q.Name,
            IsAvailable = available,
            IsDefault = !string.IsNullOrEmpty(defaultName)
                       && q.Name.Equals(defaultName, StringComparison.OrdinalIgnoreCase),
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

    /// <summary>Mở 1 máy in FRESH trên worker thread (đúng thread affinity của System.Printing) + describe.
    /// KHÔNG bao giờ ném — thread watchdog bị bỏ rơi sau timeout không được fault (tránh unobserved
    /// task exception). Trả placeholder riêng khi lỗi đọc.</summary>
    private static PrinterInfo DescribeFresh(string name, string? defaultName)
    {
        try
        {
            using var server = new LocalPrintServer();
            var q = server.GetPrintQueue(name);
            return Describe(q, defaultName);
        }
        catch (Exception ex)
        {
            return new PrinterInfo
            {
                Name = name,
                IsAvailable = false,
                StatusDetail = $"Lỗi đọc thông tin: {ex.Message}",
            };
        }
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