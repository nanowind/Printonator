using System.ComponentModel;
using ModelContextProtocol.Server;
using Printonator.Core;
using Printonator.Core.Models;
using Printonator.Core.Presets;
using Printonator.Core.Safety;
using Printonator.Mcp.Probing;
using Printonator.Spool.Printing;

namespace Printonator.Mcp;

/// <summary>
/// Các tool "AI in giùm" qua MCP. Mọi tool trả JSON {ok, ...} / {ok:false, error:{code,...}} —
/// không bao giờ ném exception, không lộ đường dẫn/Detail cho AI.
/// An toàn: tất cả đi qua PrintGuard (allowlist, quota, approve, audit).
/// </summary>
[McpServerToolType]
public static class PrintTools
{
    private static readonly string[] ImageFormats = ["PNG", "JPG", "JPEG", "TIFF", "BMP", "GIF", "WEBP"];

    /// <summary>
    /// Cổng duyệt tuần tự — chống TOCTOU: 2 request print_files song song cùng đọc quota 0 rồi cùng Enqueue.
    /// </summary>
    private static readonly SemaphoreSlim AdmitGate = new(1, 1);

    // ============ Máy in ============

    [McpServerTool, Description("Liệt kê máy in kèm trạng thái (available/offline), khổ giấy, duplex, màu, khay giấy.")]
    public static object ListPrinters()
    {
        try
        {
            var r = new PrinterService().ListPrinters();
            if (!r.IsSuccess) return Fail(r.Error!);
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["printers"] = r.Value!.Select(p => new Dictionary<string, object?>
                {
                    ["name"] = p.Name,
                    ["available"] = p.IsAvailable,
                    ["status"] = p.StatusDetail,
                    ["paper"] = p.SupportedPaperSizes,
                    ["duplex"] = p.SupportsDuplex,
                    ["color"] = p.SupportsColor,
                    ["trays"] = p.TrayInfo,
                    ["virtual"] = p.IsVirtual,
                }),
            };
        }
        catch (Exception ex)
        {
            return Fail(Err(ex, "Lỗi khi đọc máy in."));
        }
    }

    // ============ In file ============

    [McpServerTool, Description(
        "In hàng loạt các file (pdf/image/docx...). AI phải chỉ rõ máy in. Trả job_id cho từng file + ước lượng trang. " +
        "Nếu cấu hình yêu cầu duyệt (mặc định), tool trả lỗi APPROVAL_REQUIRED.")]
    public static async Task<object> PrintFiles(
        [Description("Đường dẫn các file cần in")] string[] paths,
        [Description("Tên máy in (để trống hoặc 'mặc định' = máy in mặc định Windows)")] string? printer = null,
        [Description("Số bản in (1-100)")] int copies = 1,
        [Description("In 2 mặt")] bool duplex = false,
        [Description("Khổ giấy: A4/A3/A5/Letter... (bỏ trống = theo máy/tài liệu)")] string? paper = null,
        [Description("Trang cần in: All, 2,5, 3-4, S2:1-3")] string? pageRange = null,
        [Description("In màu")] bool color = false,
        [Description("Chế độ màu: AsPrinter/AsDocument/Color/Grayscale (bỏ trống = As in printer)")] string? colorMode = null,
        [Description("Khay giấy (Paper source) — ví dụ 'Khay 1', 'Nạp tay (Manual)'; bỏ trống = máy tự chọn")] string? paperSource = null,
        [Description("Scale mode: AsDocument/ShrinkToPrintable/FitToPrintable/Original/Fill/Zoom")] string? scaleMode = null,
        [Description("Số trang trên mỗi tờ (N-up): 1 = không gom, 2/4/6/9/16")] int pagesPerSheet = 1,
        [Description("Chỉ in trang lẻ/chẵn: All/Odd/Even (bỏ trống = All)")] string? parity = null,
        [Description("Độ phân giải: AsPrinter/High/Medium/Low/Draft (bỏ trống = driver quyết)")] string? quality = null)
    {
        return await BuildAndQueue(paths, printer, cfg =>
        {
            cfg.Copies = copies;
            cfg.Duplex = duplex;
            // colorMode (mới) ưu tiên hơn bool color (cũ, giữ tương thích)
            cfg.ColorMode = colorMode is null
                ? (color ? PrintColorMode.Color : PrintColorMode.Grayscale)
                : ParseColorMode(colorMode);
            if (!string.IsNullOrWhiteSpace(paper)) cfg.PaperSize = paper;
            if (!string.IsNullOrWhiteSpace(pageRange)) cfg.PageRange = pageRange;
            if (!string.IsNullOrWhiteSpace(paperSource)) cfg.PaperSource = paperSource;
            cfg.ScaleMode = ParseScaleMode(scaleMode);
            if (pagesPerSheet > 1) cfg.PagesPerSheet = Math.Clamp(pagesPerSheet, 2, 16);
            cfg.Parity = ParseParity(parity);
            cfg.Quality = ParseQuality(quality);
        }, "print_files");
    }

    // ============ Preset ============

    [McpServerTool, Description("Liệt kê các preset cấu hình in đã lưu (tên + cấu hình).")]
    public static object GetPresets()
    {
        try
        {
            var presets = new PresetStore().Load();
            return new Dictionary<string, object?> { ["ok"] = true, ["presets"] = presets.Select(PresetDto) };
        }
        catch (Exception ex) { return Fail(Err(ex, "Không đọc được danh sách preset.")); }
    }

    [McpServerTool, Description("Lưu một preset cấu hình in để dùng lại (print_with_preset).")]
    public static object SavePreset(
        [Description("Tên preset (ví dụ 'Hợp đồng 2 mặt')")] string name,
        int copies = 1,
        bool duplex = false,
        string? paper = null,
        [Description("Máy in mặc định của preset")] string? printer = null,
        bool color = false)
    {
        try
        {
            var preset = new Preset
            {
                Name = name.Trim(),
                Copies = Math.Clamp(copies, 1, 100),
                Duplex = duplex,
                // Set ĐỦ enum để PresetDto (get_presets) không tự vả mặt: duplex:true + duplexMode:"AsPrinter".
                // Ngữ nghĩa giống bool cũ: true = LongEdge (2 mặt), false = Simplex (1 mặt).
                DuplexMode = duplex ? PrintDuplexMode.LongEdge : PrintDuplexMode.Simplex,
                PaperSize = string.IsNullOrWhiteSpace(paper) ? "A4" : paper,
                PrinterName = printer,
                ColorMode = color ? PrintColorMode.Color : PrintColorMode.Grayscale,
            };
            var ok = new PresetStore().Save(preset);
            return ok
                ? new Dictionary<string, object?> { ["ok"] = true, ["preset"] = PresetDto(preset) }
                : Fail(new PrintError
                {
                    Code = ErrorCodes.InvalidPageRange,
                    Category = PrintErrorCategory.Config,
                    Message = "Tên preset không hợp lệ.",
                    Hint = "Tên không được để trống.",
                });
        }
        catch (Exception ex) { return Fail(Err(ex, "Lưu preset thất bại.")); }
    }

    [McpServerTool, Description("In các file theo một preset đã lưu.")]
    public static async Task<object> PrintWithPreset(
        [Description("Tên preset")] string presetName,
        [Description("Đường dẫn các file cần in")] string[] paths,
        [Description("Ghi đè máy in (bỏ trống = dùng máy trong preset)")] string? printer = null)
    {
        var preset = new PresetStore().Load().FirstOrDefault(p => p.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase));
        if (preset is null)
            return Fail(new PrintError
            {
                Code = ErrorCodes.InvalidPageRange,
                Category = PrintErrorCategory.Config,
                Message = $"Không tìm thấy preset \"{presetName}\".",
                Hint = "Xem danh sách preset bằng get_presets.",
            });
        var effectivePrinter = string.IsNullOrWhiteSpace(printer) ? preset.PrinterName : printer;
        return await BuildAndQueue(paths, effectivePrinter, cfg =>
        {
            cfg.Copies = preset.Copies;
            // Prefer enum khi preset mới đã lưu chiều lật; legacy chỉ có bool Duplex → true = LongEdge;
            // còn lại giữ AsPrinter ("theo máy in") — KHÔNG ép Simplex (Major #1 fix, khớp Preset.ToPrintConfig)
            cfg.DuplexMode = preset.DuplexMode != PrintDuplexMode.AsPrinter
                ? preset.DuplexMode
                : (preset.Duplex ? PrintDuplexMode.LongEdge : preset.DuplexMode);
            cfg.PaperSize = preset.PaperSize;
            cfg.ColorMode = preset.ColorMode;
            cfg.PageRange = preset.PageRange;
            cfg.PaperSource = preset.PaperSource;
            cfg.ScaleMode = preset.ScaleMode;
            cfg.ScalePercent = preset.ScalePercent;
            cfg.PagesPerSheet = preset.PagesPerSheet;
            cfg.Booklet = preset.Booklet;
            cfg.Collation = preset.Collation;
            cfg.Parity = preset.Parity;
            cfg.Quality = preset.Quality;
            if (!string.IsNullOrWhiteSpace(printer)) cfg.PrinterName = printer;
        }, "print_with_preset");
    }

    // ============ Hàng đợi / trạng thái ============

    [McpServerTool, Description("Liệt kê jobs trong hàng đợi (lọc theo trạng thái nếu cần: queued/awaitingapproval/converting/done/error/cancelled).")]
    public static object ListJobs(
        [Description("Lọc theo trạng thái (bỏ trống = tất cả)")] string? status = null)
    {
        var jobs = AppServices.Queue.Jobs.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(status))
        {
            var target = status.Trim();
            jobs = jobs.Where(j => j.State.ToString().Equals(target, StringComparison.OrdinalIgnoreCase));
        }
        return new Dictionary<string, object?> { ["ok"] = true, ["jobs"] = jobs.Select(JobDto) };
    }

    [McpServerTool, Description("Xem chi tiết 1 job (trạng thái, lỗi nếu có, cấu hình).")]
    public static object JobStatus([Description("job_id (UUID)")] string jobId)
    {
        var job = FindJob(jobId);
        if (job is null)
            return Fail(new PrintError
            {
                Code = ErrorCodes.NoFilesSelected,
                Category = PrintErrorCategory.Config,
                Message = $"Không tìm thấy job {jobId}.",
                Hint = "Dùng list_jobs để lấy job_id đúng.",
            });
        return new Dictionary<string, object?> { ["ok"] = true, ["job"] = JobDto(job) };
    }

    [McpServerTool, Description("Hủy 1 job đang chờ in (chưa gửi máy in).")]
    public static object CancelJob([Description("job_id (UUID)")] string jobId)
    {
        var job = FindJob(jobId);
        if (job is null)
            return new Dictionary<string, object?> { ["ok"] = false, ["reason"] = "not_found" };
        if (AppServices.Queue.CancelJob(job))
            return new Dictionary<string, object?> { ["ok"] = true, ["jobId"] = jobId, ["state"] = "Cancelled" };
        return new Dictionary<string, object?>
        {
            ["ok"] = false,
            ["jobId"] = jobId,
            ["state"] = job.State.ToString(),
            ["reason"] = "Job đang in (Converting/Spooling) hoặc đã kết thúc — không hủy được.",
        };
    }

    // ============ Nội bộ ============

    private static async Task<object> BuildAndQueue(string[] paths, string? printer, Action<PrintConfig> configure, string tool)
    {
        AppServices.EnsureEngine();

        if (paths is null || paths.Length == 0)
            return Fail(new PrintError
            {
                Code = ErrorCodes.NoFilesSelected,
                Category = PrintErrorCategory.Config,
                Message = "Không có file nào được chỉ định.",
                Hint = "Truyền đầy đủ tham số paths.",
            });

        // Dựng job + validate file tồn tại
        var jobs = new List<PrintJob>();
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p) || !File.Exists(p))
                return Fail(new PrintError
                {
                    Code = ErrorCodes.FileNotFound,
                    Category = PrintErrorCategory.System,
                    Message = $"File không tồn tại: {p}",
                    Hint = "Kiểm tra lại đường dẫn file.",
                });

            var fmt = Path.GetExtension(p).TrimStart('.').ToUpperInvariant();
            var job = new PrintJob
            {
                FilePath = p,
                FileName = Path.GetFileName(p),
                Format = fmt,
                Source = JobSource.Mcp,
                Config = new PrintConfig { PrinterName = printer },
                PageCount = EstimatePageCount(fmt, p),
            };
            configure(job.Config);
            // Clamp bản in theo guard (chống AI in 999 bản)
            var maxCopies = AppServices.GuardConfig.MaxCopiesPerFile;
            if (job.Config.Copies > maxCopies)
                return Fail(new PrintError
                {
                    Code = ErrorCodes.MaxBatchExceeded,
                    Category = PrintErrorCategory.Config,
                    Message = $"Số bản in {job.Config.Copies} vượt giới hạn {maxCopies}.",
                    Hint = $"Giảm copies hoặc tăng PRINTONATOR_MAX_COPIES_PER_FILE.",
                });
            if (job.Config.Copies < 1) job.Config.Copies = 1;
            jobs.Add(job);
        }

        // Cổng tuần tự: kiểm tra + ghi vào queue là 1 khối nguyên tử — chống 2 request song song vượt quota
        await AdmitGate.WaitAsync();
        try
        {
            // Guard: allowlist + quota (cộng dồn pending) + approve
            var guard = AppServices.GuardInstance;
            var pendingPages = AppServices.Queue.CountPendingPages();
            var block = guard.Validate(printer, jobs, pendingPages);
            if (block is not null)
            {
                guard.Audit(tool, "blocked", new Dictionary<string, object?>(SafeArgs(jobs, printer))
                {
                    ["error"] = block.Code,
                });
                return Fail(block);
            }

            if (guard.Config.RequireApprove)
            {
                var approval = new PrintError
                {
                    Code = ErrorCodes.ApprovalRequired,
                    Category = PrintErrorCategory.Config,
                    Message = "Cấu hình yêu cầu NGƯỜI duyệt trước khi in (an toàn mặc định).",
                    Hint = "MCP đang chạy độc lập (không có màn hình duyệt). Muốn AI tự in: đặt PRINTONATOR_REQUIRE_APPROVE=false + PRINTONATOR_ALLOWED_PRINTERS.",
                };
                guard.Audit(tool, "blocked", new Dictionary<string, object?>(SafeArgs(jobs, printer)) { ["error"] = approval.Code });
                return Fail(approval);
            }

            if (!guard.Config.IsStandaloneAutoPrintAllowed())
            {
                var vpn = new PrintError
                {
                    Code = ErrorCodes.PrinterNoPermission,
                    Category = PrintErrorCategory.Config,
                    Message = "Allowlist máy in đang rỗng + không duyệt = từ chối tự in (fail-closed).",
                    Hint = "Đặt PRINTONATOR_ALLOWED_PRINTERS (vd \"HP LaserJet Pro M404,MFP\") để cho phép AI tự in.",
                };
                guard.Audit(tool, "blocked", new Dictionary<string, object?>(SafeArgs(jobs, printer)) { ["error"] = vpn.Code });
                return Fail(vpn);
            }

            // Chuyển sang in thật
            foreach (var j in jobs) j.Config.PrinterName = string.IsNullOrWhiteSpace(printer) ? "mặc định" : printer;
            AppServices.Queue.Enqueue(jobs);

            var jobIds = jobs.Select(j => j.Id.ToString()).ToArray();
            var totalPages = jobs.Sum(PrintQueue.EstimatedPages);
            guard.Audit(tool, "ok", new Dictionary<string, object?>(SafeArgs(jobs, printer))
            {
                ["jobIds"] = jobIds,
                ["totalPages"] = totalPages,
            });

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["jobIds"] = jobIds,
                ["estimatedPages"] = totalPages,
                ["printer"] = jobs[0].Config.PrinterName,
            };
        }
        finally
        {
            AdmitGate.Release();
        }
    }

    private static Dictionary<string, object?> SafeArgs(IReadOnlyList<PrintJob> jobs, string? printer) => new()
    {
        ["fileCount"] = jobs.Count,
        ["printer"] = printer,
    };

    private static int EstimatePageCount(string format, string path) => format switch
    {
        "PDF" => PdfPageCountProbe.TryCount(path),
        _ when ImageFormats.Contains(format) => 1,
        _ => 0, // chưa biết → guard dùng ngân sách bảo thủ
    };

    private static PrintColorMode ParseColorMode(string? s)
        => Enum.TryParse<PrintColorMode>(s, ignoreCase: true, out var m) ? m : PrintColorMode.AsPrinter;

    private static PrintScaleMode ParseScaleMode(string? s)
        => Enum.TryParse<PrintScaleMode>(s, ignoreCase: true, out var m) ? m : PrintScaleMode.AsDocument;

    private static PageParityFilter ParseParity(string? s)
        => Enum.TryParse<PageParityFilter>(s, ignoreCase: true, out var m) ? m : PageParityFilter.All;

    private static PrintQuality ParseQuality(string? s)
        => Enum.TryParse<PrintQuality>(s, ignoreCase: true, out var m) ? m : PrintQuality.AsPrinter;

    private static PrintJob? FindJob(string jobId)
        => Guid.TryParse(jobId, out var id)
            ? AppServices.Queue.Jobs.FirstOrDefault(j => j.Id == id)
            : null;

    private static object JobDto(PrintJob j) => new Dictionary<string, object?>
    {
        ["id"] = j.Id.ToString(),
        ["fileName"] = j.FileName,
        ["format"] = j.Format,
        ["state"] = j.State.ToString(),
        ["source"] = j.Source.ToString(),
        ["pages"] = j.PageCount,
        ["copies"] = j.Config.Copies,
        ["duplex"] = j.Config.Duplex,
        ["paper"] = j.Config.PaperSize,
        ["printer"] = j.Config.PrinterName,
        ["error"] = j.Error is null ? null : new Dictionary<string, object?>
        {
            ["code"] = j.Error.Code,
            ["category"] = j.Error.Category.ToString(),
            ["message"] = j.Error.Message,
            ["hint"] = j.Error.Hint,
        },
    };

    private static object PresetDto(Preset p) => new Dictionary<string, object?>
    {
        ["name"] = p.Name,
        ["copies"] = p.Copies,
        ["duplex"] = p.Duplex,
        ["duplexMode"] = p.DuplexMode.ToString(),
        ["paper"] = p.PaperSize,
        ["colorMode"] = p.ColorMode.ToString(),
        ["color"] = p.ColorMode == PrintColorMode.Color,
        ["printer"] = p.PrinterName,
        ["paperSource"] = p.PaperSource,
        ["scaleMode"] = p.ScaleMode.ToString(),
        ["scalePercent"] = p.ScalePercent,
        ["pagesPerSheet"] = p.PagesPerSheet,
        ["booklet"] = p.Booklet,
        ["collation"] = p.Collation.ToString(),
        ["parity"] = p.Parity.ToString(),
        ["quality"] = p.Quality.ToString(),
    };

    private static object Fail(PrintError e) => new Dictionary<string, object?>
    {
        ["ok"] = false,
        ["error"] = new Dictionary<string, object?>
        {
            ["code"] = e.Code,
            ["category"] = e.Category.ToString(),
            ["message"] = e.Message,
            ["hint"] = e.Hint,
        },
    };

    private static PrintError Err(Exception ex, string message) => new()
    {
        Code = ErrorCodes.SpoolerFailed,
        Category = PrintErrorCategory.App,
        Message = message,
        Hint = "Xem thông báo lỗi chi tiết trong terminal.",
        Detail = ex.Message, // chỉ log/local; không đưa vào response AI
    };
}