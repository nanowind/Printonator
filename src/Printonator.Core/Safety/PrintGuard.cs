using System.Text.Json;
using Printonator.Core.Models;

namespace Printonator.Core.Safety;

/// <summary>
/// Cấu hình an toàn cho AI in (MCP) — đọc từ file JSON hoặc env.
/// Mục tiêu CONCEPT §6: chặn AI in bậy (tốn giấy/mực thật).
/// Nguyên tắc: FAIL-CLOSED — cấu hình thiếu/hỏng → an toàn, không tự mở.
/// </summary>
public sealed record McpGuardConfig
{
    /// <summary>Chỉ cho phép in vào các máy trong danh sách (rỗng = cho phép mọi máy, KHI có duyệt).</summary>
    public string[] AllowedPrinters { get; init; } = [];

    /// <summary>Max số trang cho 1 lô in (0 = không giới hạn).</summary>
    public int MaxPagesPerBatch { get; init; } = 200;

    /// <summary>Max số file cho 1 lô in.</summary>
    public int MaxFilesPerBatch { get; init; } = 50;

    /// <summary>
    /// True = job từ AI vào trạng thái chờ duyệt, phải duyệt mới in.
    /// Standalone (không UI) bắt buộc để false để in được; để true → print_files trả lỗi duyệt.
    /// </summary>
    public bool RequireApprove { get; init; } = true;

    /// <summary>Max số bản in cho 1 file (chống AI in 999 bản).</summary>
    public int MaxCopiesPerFile { get; init; } = 100;

    /// <summary>Đường dẫn audit log (null = tắt).</summary>
    public string? AuditLogPath { get; init; }

    public static McpGuardConfig Load(string? path = null)
    {
        var file = path ?? Environment.GetEnvironmentVariable("PRINTONATOR_GUARD_FILE");
        if (!string.IsNullOrEmpty(file) && File.Exists(file))
        {
            try
            {
                return JsonSerializer.Deserialize<McpGuardConfig>(File.ReadAllText(file)) ?? new McpGuardConfig();
            }
            catch
            {
                // Cấu hình hỏng → QUAY VỀ MẶC ĐỊNH AN TOÀN (RequireApprove=true, allowlist rỗng có duyệt)
            }
        }

        // Env fallback: PRINTONATOR_ALLOWED_PRINTERS="HP404,Canon LBP"
        var envPrinters = Environment.GetEnvironmentVariable("PRINTONATOR_ALLOWED_PRINTERS");
        var allowed = string.IsNullOrWhiteSpace(envPrinters)
            ? []
            : envPrinters.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        var requireApprove = Environment.GetEnvironmentVariable("PRINTONATOR_REQUIRE_APPROVE");
        var maxPages = Environment.GetEnvironmentVariable("PRINTONATOR_MAX_PAGES_PER_BATCH");
        var maxCopies = Environment.GetEnvironmentVariable("PRINTONATOR_MAX_COPIES_PER_FILE");
        var maxFiles = Environment.GetEnvironmentVariable("PRINTONATOR_MAX_FILES_PER_BATCH");

        return new McpGuardConfig
        {
            AllowedPrinters = allowed,
            RequireApprove = string.IsNullOrEmpty(requireApprove) || !bool.TryParse(requireApprove, out var ra) || ra,
            MaxPagesPerBatch = int.TryParse(maxPages, out var mp) && mp > 0 ? mp : 200,
            MaxCopiesPerFile = int.TryParse(maxCopies, out var mc) && mc > 0 ? mc : 100,
            MaxFilesPerBatch = int.TryParse(maxFiles, out var mf) && mf > 0 ? mf : 50,
        };
    }

    /// <summary>Máy in có nằm trong allowlist không.</summary>
    public bool IsPrinterAllowed(string printerName) =>
        AllowedPrinters.Length == 0 ||
        AllowedPrinters.Any(p => p.Equals(printerName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Cấu hình có cho phép MCP STANDALONE tự in không?
    /// False (an toàn mặc định) nếu allowlist rỗng mà không cần duyệt — chặn "mở hết + tự in".
    /// </summary>
    public bool IsStandaloneAutoPrintAllowed() =>
        RequireApprove || AllowedPrinters.Length > 0;
}

/// <summary>Audit log cho AI in — mỗi dòng JSON, chỉ ghi field an toàn (whitelist), không lộ path/secret.</summary>
public sealed class AuditLogger
{
    private readonly string _path;
    private readonly object _sync = new();

    public AuditLogger(string? path = null)
    {
        _path = path ?? Environment.GetEnvironmentVariable("PRINTONATOR_AUDIT_LOG")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Printonator", "audit.log");
    }

    /// <summary>
    /// Ghi audit. args CHỈ gồm các field an toàn (GUID, số lượng, tên máy) — tool phải tự build,
    /// tuyệt đối không truyền PrintJob/FileInfo/exception (lộ đường dẫn + username).
    /// </summary>
    public void Log(string tool, string outcome, IReadOnlyDictionary<string, object?>? args = null)
    {
        try
        {
            lock (_sync)
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var entry = new Dictionary<string, object?>
                {
                    ["ts"] = DateTimeOffset.Now,
                    ["tool"] = tool,
                    ["outcome"] = outcome,
                };
                if (args is not null)
                    foreach (var (k, v) in args)
                        entry[k] = v;
                File.AppendAllText(_path, JsonSerializer.Serialize(entry) + Environment.NewLine);
            }
        }
        catch
        {
            // Audit không được làm hỏng luồng in — lỗi ghi log thì bỏ qua
        }
    }
}

/// <summary>Hệ quy chiếu an toàn cho một lô file do AI yêu cầu in.</summary>
public sealed class PrintGuard
{
    private readonly McpGuardConfig _cfg;
    private readonly AuditLogger? _audit;

    public PrintGuard(McpGuardConfig? config = null, AuditLogger? audit = null)
    {
        _cfg = config ?? McpGuardConfig.Load();
        _audit = audit;
    }

    public McpGuardConfig Config => _cfg;
    public AuditLogger? AuditLog => _audit;

    /// <summary>
    /// Kiểm tra lô file trước khi cho in. Trả PrintError nếu vi phạm.
    /// alreadyPendingPages = queue.CountPendingPages() đã chờ (để quota KHÔNG per-request, cộng dồn).
    /// </summary>
    public PrintError? Validate(string? printerName, IReadOnlyList<PrintJob> jobs, int alreadyPendingPages = 0)
    {
        if (jobs.Count == 0)
            return Err(ErrorCodes.NoFilesSelected, "Không có file nào để in.");

        if (_cfg.MaxFilesPerBatch > 0 && jobs.Count > _cfg.MaxFilesPerBatch)
            return Err(ErrorCodes.MaxBatchExceeded,
                $"Lô in quá lớn: {jobs.Count} file (giới hạn {_cfg.MaxFilesPerBatch}).",
                "Chia nhỏ lô in hoặc tăng PRINTONATOR_MAX_FILES_PER_BATCH.");

        if (!string.IsNullOrEmpty(printerName) && !_cfg.IsPrinterAllowed(printerName))
            return Err(ErrorCodes.PrinterNoPermission,
                $"Máy in \"{printerName}\" KHÔNG nằm trong danh sách được duyệt.",
                "Chọn máy in trong allowlist hoặc bổ sung vào PRINTONATOR_ALLOWED_PRINTERS.");

        if (_cfg.MaxPagesPerBatch > 0)
        {
            // File chưa rõ số trang (PDF chưa probe, DOCX...) = dùng ngân sách bảo thủ 50 trang/file
            // → không bao giờ "1 file 500 trang tính là 1 trang" lọt quota, nhưng lô DOCX nhỏ vẫn in được
            const int UnknownPageBudgetPerFile = 50;
            var total = Math.Max(alreadyPendingPages, 0)
                        + jobs.Sum(j => j.PageCount <= 0
                            ? UnknownPageBudgetPerFile * Math.Max(j.Config.Copies, 1)
                            : EstimatedPages(j));
            if (total > _cfg.MaxPagesPerBatch)
                return Err(ErrorCodes.MaxBatchExceeded,
                    $"Ước tính {total} trang (lô mới + đang chờ) — vượt giới hạn {_cfg.MaxPagesPerBatch}.",
                    "Giảm số file/bản in hoặc tăng PRINTONATOR_MAX_PAGES_PER_BATCH.");
        }

        return null;
    }

    private static int EstimatedPages(PrintJob job)
    {
        var r = job.ResolvePhysicalPages();
        var pages = r.IsSuccess ? Math.Max(r.Value!.Length, 1) : 1;
        return pages * Math.Max(job.Config.Copies, 1);
    }

    public void Audit(string tool, string outcome, IReadOnlyDictionary<string, object?>? args = null)
        => _audit?.Log(tool, outcome, args);

    private static PrintError Err(string code, string message, string hint = "") => new()
    {
        Code = code,
        Category = PrintErrorCategory.Config,
        Message = message,
        Hint = string.IsNullOrEmpty(hint) ? "Kiểm tra lại yêu cầu in." : hint,
    };
}