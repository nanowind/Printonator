using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Printonator.Core;
using Printonator.Core.Models;

namespace Printonator.Spool.Printing;

/// <summary>
/// Engine in file qua Windows ShellExecute("printto") — gọi app mặc định (Edge/Word/Excel...)
/// với verb "printto" + máy in chỉ định. Dùng cho mọi định dạng (shell xử lý),
/// fallback khi chưa có engine chuyên biệt (PDFium, LibreOffice...).
/// UI và MCP dùng chung engine này.
/// </summary>
public sealed class SpoolPrintEngine : IPrintEngine
{
    public bool CanHandle(string format) => true; // mọi định dạng — shell xử lý

    public Task<Result<bool>> PrintAsync(PrintJob job, CancellationToken ct)
    {
        try
        {
            var printerName = job.Config.PrinterName;
            if (string.IsNullOrEmpty(printerName))
                return Task.FromResult(Result<bool>.Fail(new PrintError
                {
                    Code = ErrorCodes.PrinterNotFound,
                    Category = PrintErrorCategory.Config,
                    Message = "Chưa chọn máy in.",
                    Hint = "Chọn máy in ở thanh công cụ (ví dụ Microsoft Print to PDF)."
                }));

            if (!File.Exists(job.FilePath))
                return Task.FromResult(Result<bool>.Fail(new PrintError
                {
                    Code = ErrorCodes.FileNotFound,
                    Category = PrintErrorCategory.System,
                    Message = $"File không tồn tại: {job.FilePath}",
                    Hint = "File bị xóa hoặc di chuyển — kiểm tra lại đường dẫn."
                }));

            // "mặc định" = máy in mặc định của Windows
            if (printerName.Equals("mặc định", StringComparison.OrdinalIgnoreCase)
                || printerName.Equals("default", StringComparison.OrdinalIgnoreCase))
                printerName = GetDefaultPrinterName();

            ct.ThrowIfCancellationRequested();

            var psi = new ProcessStartInfo
            {
                FileName = job.FilePath,
                Verb = "printto",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                Arguments = $"\"{printerName}\"",
            };

            var proc = Process.Start(psi);
            if (proc is null)
                return Task.FromResult(Result<bool>.Fail(new PrintError
                {
                    Code = ErrorCodes.SpoolerFailed,
                    Category = PrintErrorCategory.Printer,
                    Message = $"Không khởi động được lệnh in tới \"{printerName}\".",
                    Hint = "Kiểm tra máy in có tồn tại và app đọc file có hoạt động."
                }));

            if (job.PageCount <= 0) job.PageCount = 1; // shell không biết số trang — giữ giá trị đã probe nếu có
            return Task.FromResult(Result<bool>.Ok(true));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.EngineTimeout,
                Category = PrintErrorCategory.System,
                Message = $"In {job.FileName} bị hủy.",
                Hint = "Bấm in lại nếu cần."
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.SpoolerFailed,
                Category = PrintErrorCategory.Printer,
                Message = $"Lỗi khi in {job.FileName}.",
                Hint = "Có thể máy in không tồn tại. Thử chọn 'Microsoft Print to PDF'.",
                Detail = ex.ToString(),
            }));
        }
    }

    /// <summary>Lấy tên máy in mặc định Windows (fallback khi chưa chọn).</summary>
    internal static string GetDefaultPrinterName()
    {
        try
        {
            var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\windows NT\CurrentVersion\Windows");
            var val = key?.GetValue("Device")?.ToString();
            if (!string.IsNullOrEmpty(val))
            {
                var comma = val.IndexOf(',');
                return comma > 0 ? val[..comma] : val;
            }
        }
        catch { }
        return "Microsoft Print to PDF";
    }
}