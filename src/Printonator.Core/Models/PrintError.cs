namespace Printonator.Core.Models;

/// <summary>Loại lỗi in — phân loại rõ để UI + MCP xử lý đúng.</summary>
public enum PrintErrorCategory
{
    /// <summary>Lỗi do app (bug, file hỏng cấu trúc, không đọc được).</summary>
    App,
    /// <summary>Lỗi do người dùng cấu hình sai (page range không hợp lệ, thiếu máy in...).</summary>
    Config,
    /// <summary>Lỗi do máy in / driver / spooler (offline, hết giấy, mất kết nối).</summary>
    Printer,
    /// <summary>Lỗi do hệ thống (thiếu quyền, file bị khóa, disk đầy).</summary>
    System,
}

/// <summary>
/// Lỗi in đầy đủ — KHÔNG bao giờ nuốt exception. Mỗi lỗi có:
/// code (máy đọc được), message tiếng Việt cho người dùng, gợi ý khắc phục,
/// và category để UI hiện đúng kiểu (banner/pill/toast).
/// </summary>
public sealed record PrintError
{
    public required string Code { get; init; }                 // vd: PRINTER_OFFLINE
    public required PrintErrorCategory Category { get; init; }
    public required string Message { get; init; }              // hiện trên UI
    public required string Hint { get; init; }                 // khuyến nghị thao tác gỡ lỗi
    public string? Detail { get; init; }                       // chi tiết kỹ thuật (log)
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.Now;

    public override string ToString() => $"[{Code}] ({Category}) {Message} — {Hint}";
}

/// <summary>Các mã lỗi chuẩn của app.</summary>
public static class ErrorCodes
{
    // Printer / spooler
    public const string PrinterOffline = "PRINTER_OFFLINE";
    public const string PrinterNotFound = "PRINTER_NOT_FOUND";
    public const string PrinterNoPermission = "PRINTER_NO_PERMISSION";
    public const string SpoolerBusy = "SPOOLER_BUSY";
    public const string SpoolerFailed = "SPOOLER_FAILED";
    // File / format
    public const string FileNotFound = "FILE_NOT_FOUND";
    public const string FileLocked = "FILE_LOCKED";
    public const string FileCorrupted = "FILE_CORRUPTED";
    public const string UnsupportedFormat = "UNSUPPORTED_FORMAT";
    // Config
    public const string InvalidPageRange = "INVALID_PAGE_RANGE";
    public const string SectionNotFound = "SECTION_NOT_FOUND";
    public const string NoFilesSelected = "NO_FILES_SELECTED";
    public const string MaxBatchExceeded = "MAX_BATCH_EXCEEDED";
    public const string ApprovalRequired = "APPROVAL_REQUIRED";
    public const string PresetNotFound = "PRESET_NOT_FOUND";
    public const string InvalidPreset = "INVALID_PRESET";
    public const string JobNotFound = "JOB_NOT_FOUND";
    public const string JobNotApprovable = "JOB_NOT_APPROVABLE";
    // Engine
    public const string EngineNotFound = "ENGINE_NOT_FOUND";
    public const string EngineTimeout = "ENGINE_TIMEOUT";
    public const string EngineFailed = "ENGINE_FAILED";   // gộp file / việc dựng bản in thất bại (merge fallback)
    public const string OfficeAppBusy = "OFFICE_APP_BUSY";
    // System
    public const string Unauthorized = "UNAUTHORIZED";
    public const string DiskFull = "DISK_FULL";
    public const string BuildUntrusted = "BUILD_UNTRUSTED";
}