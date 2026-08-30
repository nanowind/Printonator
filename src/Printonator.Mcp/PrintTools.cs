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
        "In hàng loạt các file (pdf/image/docx...). AI KHÔNG cần chỉ máy in — bỏ trống printer = hệ thống tự chọn máy in vật lý sẵn sàng (xem pick_printer). Trả job_id cho từng file + ước lượng trang. " +
        "Nếu cấu hình yêu cầu duyệt (mặc định), jobs vào trạng thái awaitingapproval — dùng approve_job để duyệt (get_guard_config xem có cần duyệt không).")]
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
            if (ok)
            {
                AppServices.GuardInstance.Audit("save_preset", "ok", new Dictionary<string, object?> { ["presetName"] = name, ["ok"] = true });
                return new Dictionary<string, object?> { ["ok"] = true, ["preset"] = PresetDto(preset) };
            }
            return Fail(new PrintError
            {
                Code = ErrorCodes.InvalidPreset,
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
                Code = ErrorCodes.PresetNotFound,
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
            return Fail(JobNotFound(jobId));
        return new Dictionary<string, object?> { ["ok"] = true, ["job"] = JobDto(job) };
    }

    [McpServerTool, Description("Hủy 1 job đang chờ hoặc đang in (job đang in sẽ dừng engine thật, chuyển trạng thái Cancelled).")]
    public static object CancelJob([Description("job_id (UUID)")] string jobId)
    {
        var job = FindJob(jobId);
        if (job is null)
            return Fail(JobNotFound(jobId));
        if (AppServices.Queue.CancelJob(job))
        {
            AppServices.GuardInstance.Audit("cancel_job", "ok", new Dictionary<string, object?> { ["jobId"] = jobId, ["ok"] = true });
            return new Dictionary<string, object?> { ["ok"] = true, ["jobId"] = jobId, ["state"] = "Cancelled" };
        }
        return Fail(new PrintError
        {
            Code = ErrorCodes.SpoolerBusy,
            Category = PrintErrorCategory.Config,
            Message = $"Job {jobId} đã kết thúc hoặc đang chờ duyệt — không hủy bằng cancel_job được.",
            Hint = "Job chờ duyệt: dùng reject_job. Job đang chờ in giữa lô: dùng cancel_job khi nó còn ở trạng thái chờ.",
        });
    }

    // ============ Tự chọn máy in ============

    [McpServerTool, Description(
        "Tự chọn máy in phù hợp nhất: ưu tiên máy VẬT LÝ đang sẵn sàng (không máy ảo PDF/XPS/OneNote/Fax). " +
        "Có thể lọc theo khổ giấy / duplex / màu. Dùng trước print_files khi AI không biết nên in máy nào.")]
    public static object PickPrinter(
        [Description("Bắt buộc khổ giấy (vd A4/A3/Letter) — bỏ trống = mọi khổ")] string? paper = null,
        [Description("Bắt buộc in 2 mặt")] bool requireDuplex = false,
        [Description("Bắt buộc in màu")] bool requireColor = false)
    {
        try
        {
            var printers = new PrinterService().ListPrinters();
            if (!printers.IsSuccess) return Fail(printers.Error!);
            var picked = PickBestPrinter(printers.Value, paper, requireDuplex, requireColor);
            if (picked is null)
                return Fail(new PrintError
                {
                    Code = ErrorCodes.PrinterNotFound,
                    Category = PrintErrorCategory.Printer,
                    Message = "Không có máy in đáp ứng yêu cầu.",
                    Hint = "Chạy list_printers để xem máy khả dụng + yêu cầu của bạn (khổ/duplex/màu); hoặc đặt PRINTONATOR_ALLOWED_PRINTERS nếu máy bị chặn.",
                });

            // Top 5 ứng viên xếp hạng — để AI tự cân nhắc nếu không đồng ý máy đầu
            var candidates = printers.Value
                .Where(p => AppServices.GuardConfig.IsPrinterAllowed(p.Name))
                .OrderByDescending(p => p.IsAvailable)
                .ThenBy(p => p.IsVirtual)
                .ThenByDescending(p => p.IsDefault)
                .Take(5)
                .Select(p => new Dictionary<string, object?>
                {
                    ["name"] = p.Name,
                    ["available"] = p.IsAvailable,
                    ["virtual"] = p.IsVirtual,
                    ["paper"] = p.SupportedPaperSizes,
                    ["duplex"] = p.SupportsDuplex,
                    ["color"] = p.SupportsColor,
                    ["trays"] = p.TrayInfo,
                });
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["printer"] = picked.Name,
                ["available"] = picked.IsAvailable,
                ["physical"] = !picked.IsVirtual,
                ["isDefault"] = picked.IsDefault,
                ["reason"] = picked.IsAvailable
                    ? (picked.IsVirtual ? "Máy ảo khả dụng (không có máy vật lý) — hãy xác nhận trước khi in." : "Máy vật lý đang sẵn sàng.")
                    : "Máy vật lý duy nhất khả dụng (có thể offline — kiểm tra trước khi in).",
                ["candidates"] = candidates,
            };
        }
        catch (Exception ex)
        {
            return Fail(Err(ex, "Lỗi khi chọn máy in."));
        }
    }

    /// <summary>Xếp hạng máy in tốt nhất: available > máy vật lý > default; lọc theo allowlist + khổ/duplex/màu.
    /// Helper THUẦN (nhận List&lt;PrinterInfo&gt;) — test được không cần spooler thật.</summary>
    internal static PrinterInfo? PickBestPrinter(
        IReadOnlyList<PrinterInfo> printers, string? paper, bool requireDuplex, bool requireColor)
    {
        if (printers is null || printers.Count == 0) return null;
        return printers
            .Where(p => AppServices.GuardConfig.IsPrinterAllowed(p.Name))
            .Where(p => string.IsNullOrWhiteSpace(paper) || ContainsPaper(p, paper))
            .Where(p => !requireDuplex || p.SupportsDuplex)
            .Where(p => !requireColor || p.SupportsColor)
            .OrderByDescending(p => p.IsAvailable)
            .ThenBy(p => p.IsVirtual)       // máy vật lý trước máy ảo
            .ThenByDescending(p => p.IsDefault)
            .FirstOrDefault();
    }

    private static bool ContainsPaper(PrinterInfo p, string paper)
        => (p.SupportedPaperSizes?.Contains(paper, StringComparer.OrdinalIgnoreCase) ?? false)
           || (p.SupportedPaperSizes?.Any(s => s.Contains(paper, StringComparison.OrdinalIgnoreCase)) ?? false);

    // ============ Duyệt job ============

    [McpServerTool, Description(
        "Duyệt 1 job từ AI đang chờ duyệt (state=awaitingapproval) để cho in. " +
        "Chỉ duyệt được job nguồn AI đang chờ. Xem list_jobs status=awaitingapproval.")]
    public static object ApproveJob([Description("job_id (UUID)")] string jobId)
    {
        var job = FindJob(jobId);
        if (job is null) return Fail(JobNotFound(jobId));
        if (job.State != JobState.AwaitingApproval || job.Source != JobSource.Mcp) return Fail(JobNotApprovable(job));
        if (!AppServices.Queue.ApproveJob(job))
            return Fail(JobNotApprovable(job));
        AppServices.GuardInstance.Audit("approve_job", "ok", new Dictionary<string, object?> { ["jobId"] = jobId, ["ok"] = true });
        return new Dictionary<string, object?> { ["ok"] = true, ["jobId"] = jobId, ["state"] = "Queued" };
    }

    [McpServerTool, Description(
        "Từ chối 1 job đang chờ duyệt (state=awaitingapproval) — job chuyển cancelled, không in.")]
    public static object RejectJob([Description("job_id (UUID)")] string jobId)
    {
        var job = FindJob(jobId);
        if (job is null) return Fail(JobNotFound(jobId));
        if (job.State != JobState.AwaitingApproval || job.Source != JobSource.Mcp) return Fail(JobNotApprovable(job));
        if (!AppServices.Queue.RejectJob(job))
            return Fail(JobNotApprovable(job));
        AppServices.GuardInstance.Audit("reject_job", "ok", new Dictionary<string, object?> { ["jobId"] = jobId, ["ok"] = true });
        return new Dictionary<string, object?> { ["ok"] = true, ["jobId"] = jobId, ["state"] = "Cancelled" };
    }

    // ============ Cấu hình an toàn + tra cứu lỗi ============

    [McpServerTool, Description(
        "Xem cấu hình an toàn đang áp dụng: máy được phép (allowlist), có bắt buộc duyệt không, giới hạn trang/file/bản. " +
        "Gọi trước khi in để biết AI có tự in được không.")]
    public static object GetGuardConfig()
    {
        var cfg = AppServices.GuardConfig;
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["requireApprove"] = cfg.RequireApprove,
            ["canAutoPrint"] = !cfg.RequireApprove && cfg.AllowedPrinters.Length > 0,
            ["allowedPrinters"] = cfg.AllowedPrinters,
            ["maxPagesPerBatch"] = cfg.MaxPagesPerBatch,
            ["maxFilesPerBatch"] = cfg.MaxFilesPerBatch,
            ["maxCopiesPerFile"] = cfg.MaxCopiesPerFile,
        };
    }

    [McpServerTool, Description(
        "Tra cứu bảng mã lỗi Printonator: mỗi mã → nghĩa là gì + AI nên làm gì để khắc phục. " +
        "Gọi khi gặp lỗi {ok:false, error:{code}} để biết cách xử lý.")]
    public static object GetErrorReference(
        [Description("Lọc theo mã lỗi (bỏ trống = trả toàn bộ)")] string? code = null)
    {
        IEnumerable<(string Code, string Meaning, string AiAction)> rows = ErrorReference;
        if (!string.IsNullOrWhiteSpace(code))
            rows = rows.Where(r => r.Code.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase));
        var list = rows.Select(r => new Dictionary<string, object?>
        {
            ["code"] = r.Code,
            ["meaning"] = r.Meaning,
            ["aiAction"] = r.AiAction,
        });
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["codes"] = list,
            ["note"] = rows.Any() ? null : "Không có mã này — xem danh sách đầy đủ (bỏ trống tham số code).",
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
                // LUỒNG DUYỆT THẬT qua MCP: jobs vào trạng thái AwaitingApproval (không hard-block nữa).
                // AI/người dùng duyệt bằng approve_job (từ chối: reject_job). ApproveJob/RejectJob đã có trong Core.
                foreach (var j in jobs) j.Config.PrinterName = string.IsNullOrWhiteSpace(printer) ? "mặc định" : printer;
                AppServices.Queue.AddForApproval(jobs);

                var pendingIds = jobs.Select(j => j.Id.ToString()).ToArray();
                var pendingTotal = jobs.Sum(PrintQueue.EstimatedPages);
                guard.Audit(tool, "pending_approval", new Dictionary<string, object?>(SafeArgs(jobs, printer))
                {
                    ["jobIds"] = pendingIds,
                    ["totalPages"] = pendingTotal,
                });

                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["pendingApproval"] = true,
                    ["jobIds"] = pendingIds,
                    ["estimatedPages"] = pendingTotal,
                    ["printer"] = jobs[0].Config.PrinterName,
                    ["note"] = "Jobs đang CHỜ DUYỆT — dùng approve_job để cho in, hoặc reject_job để từ chối (list_jobs status=awaitingapproval).",
                };
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

            // TỰ CHỌN MÁY IN khi AI không chỉ rõ — nhưng CHỈ khi có allowlist (còn allowlist rỗng + không duyệt
            // thì để guard chặn đúng PRINTER_NO_PERMISSION ở trên — không che lỗi cấu hình fail-closed).
            if (string.IsNullOrWhiteSpace(printer) && AppServices.GuardConfig.AllowedPrinters.Length > 0)
            {
                var picked = PickBestPrinter(printerList(), null, false, false);
                if (picked is not null)
                    printer = picked.Name;
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

    /// <summary>Lấy danh sách máy in cho auto-pick (best-effort) — lỗi đọc máy thì bỏ qua auto-pick.</summary>
    private static IReadOnlyList<PrinterInfo>? printerList()
    {
        try
        {
            var r = new PrinterService().ListPrinters();
            return r.IsSuccess ? r.Value : null;
        }
        catch { return null; }
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

    private static PrintError JobNotFound(string jobId) => new()
    {
        Code = ErrorCodes.JobNotFound,
        Category = PrintErrorCategory.Config,
        Message = $"Không tìm thấy job {jobId}.",
        Hint = "Dùng list_jobs để lấy job_id đúng.",
    };

    private static PrintError JobNotApprovable(PrintJob job) => new()
    {
        Code = ErrorCodes.JobNotApprovable,
        Category = PrintErrorCategory.Config,
        Message = $"Job {job.Id} không ở trạng thái chờ duyệt (hiện: {job.State}).",
        Hint = "Chỉ job nguồn AI (Mcp) đang awaitingapproval mới duyệt/từ chối được.",
    };

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
            ["suggestedAction"] = SuggestedAction(j.Error.Code),
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

    /// <summary>
    /// Bảng tra cứu mã lỗi cho AI — single source of truth cho get_error_reference + suggestedAction của job_status.
    /// Giữ đồng bộ với mọi hằng số trong ErrorCodes (test reflection chống lệch).
    /// </summary>
    private static readonly (string Code, string Meaning, string AiAction)[] ErrorReference =
    [
        (ErrorCodes.PrinterNotFound, "Không tìm thấy máy in.",
            "Chạy list_printers để lấy đúng tên máy, thử lại với tên chính xác."),
        (ErrorCodes.PrinterOffline, "Máy in đang offline / không phản hồi.",
            "Chạy list_printers xem máy nào available:true; gọi pick_printer chọn máy khác rồi in lại."),
        (ErrorCodes.PrinterNoPermission, "Máy không nằm trong allowlist (hoặc allowlist rỗng + không duyệt = cấm tự in).",
            "Gọi get_guard_config xem allowedPrinters; chọn máy trong danh sách, hoặc báo người dùng thêm máy vào PRINTONATOR_ALLOWED_PRINTERS."),
        (ErrorCodes.SpoolerBusy, "Spooler / job đang bận — không hủy hoặc thao tác được ngay.",
            "Chờ vài giây rồi gọi job_status lại; nếu cần hủy thì đợi job về Done/Error."),
        (ErrorCodes.SpoolerFailed, "Lỗi không xác định khi gửi in.",
            "Báo người dùng xem log; thử pick_printer chọn máy khác và in lại 1 file nhỏ để kiểm tra."),
        (ErrorCodes.FileNotFound, "File không tồn tại ở đường dẫn.",
            "Kiểm tra lại đường dẫn file; báo người dùng xác nhận file còn ở đúng chỗ."),
        (ErrorCodes.FileLocked, "File đang bị khóa (đang mở bởi app khác).",
            "Yêu cầu người dùng đóng app đang giữ file rồi in lại."),
        (ErrorCodes.FileCorrupted, "File hỏng / không đọc được.",
            "Báo người dùng mở file kiểm tra; thử file khác."),
        (ErrorCodes.UnsupportedFormat, "Định dạng không có engine in.",
            "Báo người dùng chuyển file sang PDF rồi in lại."),
        (ErrorCodes.InvalidPageRange, "Page range sai cú pháp.",
            "Xem job_status để biết số trang của file; gọi print_files với range đúng (All, 2,5, 3-4, S2:1-3)."),
        (ErrorCodes.SectionNotFound, "File không có section được chỉ định.",
            "Hỏi người dùng số section đúng hoặc dùng page range All."),
        (ErrorCodes.NoFilesSelected, "Không có file nào được truyền.",
            "Gọi print_files với tham số paths không rỗng."),
        (ErrorCodes.MaxBatchExceeded, "Vượt giới hạn trang / file / bản in.",
            "Chia nhỏ lô (giảm số file/trang/bản), hoặc báo người dùng tăng PRINTONATOR_MAX_* nếu cần."),
        (ErrorCodes.ApprovalRequired, "Cấu hình yêu cầu duyệt — AI chưa được tự in.",
            "Nếu job đã vào hàng đợi: list_jobs status=awaitingapproval + approve_job. Muốn AI tự in: báo người dùng đặt PRINTONATOR_REQUIRE_APPROVE=false + PRINTONATOR_ALLOWED_PRINTERS."),
        (ErrorCodes.JobNotFound, "job_id không tồn tại.",
            "Chạy list_jobs để lấy job_id đúng rồi thử lại."),
        (ErrorCodes.JobNotApprovable, "Job không ở trạng thái chờ duyệt (đã in/xong/lỗi, hoặc không phải nguồn AI).",
            "Gọi job_status để xem state hiện tại; nếu đã Done/Error thì không cần duyệt nữa."),
        (ErrorCodes.PresetNotFound, "Preset chưa tồn tại.",
            "Chạy get_presets để xem tên đúng, dùng print_with_preset với tên đó."),
        (ErrorCodes.InvalidPreset, "Tên preset không hợp lệ (vd trống).",
            "Gọi save_preset với tên không rỗng."),
        (ErrorCodes.EngineNotFound, "Không có engine in cho định dạng.",
            "Chuyển file sang PDF rồi in lại."),
        (ErrorCodes.EngineFailed, "Engine in gặp lỗi không xác định (vd không gộp được file).",
            "Thử lại với ít file hơn hoặc in từng file riêng; báo người dùng xem log nếu lặp lại."),
        (ErrorCodes.EngineTimeout, "Engine in không phản hồi trong hạn.",
            "Thử lại sau vài giây; nếu lặp lại, báo người dùng kiểm tra Word/Excel/LibreOffice."),
        (ErrorCodes.OfficeAppBusy, "Word/Excel/PowerPoint đang bận.",
            "Chờ app đóng rồi thử lại."),
        (ErrorCodes.Unauthorized, "Thiếu quyền hệ thống.",
            "Báo người dùng chạy với quyền đúng."),
        (ErrorCodes.DiskFull, "Đĩa đầy.",
            "Báo người dùng dọn đĩa rồi thử lại."),
        (ErrorCodes.BuildUntrusted, "Bản build không đáng tin.",
            "Báo người dùng build lại từ source hoặc dùng installer chính thức."),
    ];

    /// <summary>Hành động AI nên làm khi gặp mã lỗi (map từ ErrorReference) — job_status trả kèm.</summary>
    private static string? SuggestedAction(string? code)
        => code is null ? null : ErrorReference.FirstOrDefault(r => r.Code == code).AiAction;
}