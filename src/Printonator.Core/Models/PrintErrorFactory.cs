using System;

namespace Printonator.Core.Models;

/// <summary>
/// Centralized factory for common print errors.
/// Use these static methods instead of constructing PrintError inline.
/// </summary>
public static class PrintErrorFactory
{
    public static PrintError FileNotFound(string path) =>
        new()
        {
            Code = ErrorCodes.FileNotFound,
            Category = PrintErrorCategory.System,
            Message = $"File không tồn tại: {path}",
            Hint = "File bị xóa hoặc di chuyển — kiểm tra lại đường dẫn.",
        };

    public static PrintError PrinterNotFound(string? printer = null) =>
        new()
        {
            Code = ErrorCodes.PrinterNotFound,
            Category = PrintErrorCategory.Config,
            Message = string.IsNullOrWhiteSpace(printer)
                ? "Chưa chọn máy in."
                : $"Không tìm thấy máy in: {printer}",
            Hint = "Chọn máy in (hoặc để 'mặc định').",
        };

    public static PrintError EngineNotFound(string engine) =>
        new()
        {
            Code = ErrorCodes.EngineNotFound,
            Category = PrintErrorCategory.App,
            Message = $"Không tìm thấy engine {engine} để in file này.",
            Hint = "Kiểm tra phần mềm cần thiết đã cài (MS Office, LibreOffice, Edge/Chrome).",
        };

    public static PrintError InvalidPageRange(string spec, string hint = "") =>
        new()
        {
            Code = ErrorCodes.InvalidPageRange,
            Category = PrintErrorCategory.Config,
            Message = $"Page range \"{spec}\" không hợp lệ.",
            Hint = string.IsNullOrEmpty(hint)
                ? "Định dạng đúng: All | 2,5 | 3-4 | 1-2,7 | S2:1-3."
                : hint,
        };

    public static PrintError SpoolerFailed(string detail) =>
        new()
        {
            Code = ErrorCodes.SpoolerFailed,
            Category = PrintErrorCategory.Printer,
            Message = "Lỗi hệ thống in (spooler) hoặc máy in.",
            Hint = "Kiểm tra máy in còn hoạt động, khởi động lại spooler nếu cần.",
            Detail = detail,
        };

    public static PrintError EngineTimeout(string fileName, int seconds) =>
        new()
        {
            Code = ErrorCodes.EngineTimeout,
            Category = PrintErrorCategory.System,
            Message = $"Xử lý {fileName} quá lâu ({seconds}s) — đã hủy.",
            Hint = "Thử in lại hoặc chọn máy in khác.",
        };

    public static PrintError FileCorrupted(string file) =>
        new()
        {
            Code = ErrorCodes.FileCorrupted,
            Category = PrintErrorCategory.App,
            Message = $"File {file} bị hỏng hoặc không đọc được.",
            Hint = "Mở thử file trong app gốc để kiểm tra.",
        };

    public static PrintError JobNotFound(Guid jobId) =>
        new()
        {
            Code = ErrorCodes.JobNotFound,
            Category = PrintErrorCategory.Config,
            Message = $"Không tìm thấy job với ID: {jobId}",
            Hint = "Kiểm tra lại ID job hoặc tải lại danh sách.",
        };

    public static PrintError JobNotFound(string jobId) =>
        new()
        {
            Code = ErrorCodes.JobNotFound,
            Category = PrintErrorCategory.Config,
            Message = $"Không tìm thấy job với ID: {jobId}",
            Hint = "Kiểm tra lại ID job hoặc tải lại danh sách.",
        };

    public static PrintError PresetNotFound(string name) =>
        new()
        {
            Code = ErrorCodes.PresetNotFound,
            Category = PrintErrorCategory.Config,
            Message = $"Không tìm thấy preset \"{name}\".",
            Hint = "Xem danh sách preset bằng get_presets.",
        };

    public static PrintError InvalidPreset(string message) =>
        new()
        {
            Code = ErrorCodes.InvalidPreset,
            Category = PrintErrorCategory.Config,
            Message = message,
            Hint = "Kiểm tra lại tên preset.",
        };

    public static PrintError UnsupportedFormat(string format) =>
        new()
        {
            Code = ErrorCodes.UnsupportedFormat,
            Category = PrintErrorCategory.Config,
            Message = $"Định dạng \"{format}\" không được hỗ trợ.",
            Hint = "Kiểm tra lại định dạng file.",
        };
}