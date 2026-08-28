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

            // Không có máy in mặc định → báo lỗi RÕ (tránh printto với tên rỗng → in nhầm máy/xuất PDF).
            if (string.IsNullOrWhiteSpace(printerName))
                return Task.FromResult(Result<bool>.Fail(new PrintError
                {
                    Code = ErrorCodes.PrinterNotFound,
                    Category = PrintErrorCategory.Config,
                    Message = "Không tìm thấy máy in mặc định.",
                    Hint = "Chọn máy in cụ thể ở thanh công cụ.",
                }));

            ct.ThrowIfCancellationRequested();

            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "printonator-office.log"),
                    $"{DateTimeOffset.Now:O} SpoolPrintEngine printto printer='{printerName}' file={job.FileName}\n");
            }
            catch { }

            var psi = new ProcessStartInfo
            {
                FileName = job.FilePath,
                Verb = "printto",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                Arguments = $"\"{printerName}\"",
            };

            // In LẦN LƯỢT từng file (như Print Conductor): printto trả về NGAY sau khi đẩy job — nếu không
            // chờ job thật in XONG thì queue ném liên tục các job lên máy → "đồng loạt chạy", Pause vô nghĩa.
            try
            {
                using var server = new System.Printing.LocalPrintServer();
                var q = server.GetPrintQueue(printerName);
                int baseline = ActiveJobCount(q);   // baseline TRƯỚC printto = biết job nào là của mình

                var proc = Process.Start(psi);
                if (proc is null)
                    return Task.FromResult(Result<bool>.Fail(new PrintError
                    {
                        Code = ErrorCodes.SpoolerFailed,
                        Category = PrintErrorCategory.Printer,
                        Message = $"Không khởi động được lệnh in tới \"{printerName}\".",
                        Hint = "Kiểm tra máy in có tồn tại và app đọc file có hoạt động."
                    }));

                WaitForPrintCompletion(q, baseline);   // chờ job mới in XONG mới trả về (lần lượt từng file)
            }
            catch (System.Printing.PrintSystemException) { /* không đọc được queue — không chờ (best-effort) */ }

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
                Hint = "Có thể máy in không tồn tại. Kiểm tra máy in đã chọn còn hoạt động.",
                Detail = ex.ToString(),
            }));
        }
    }

    /// <summary>Lấy tên máy in mặc định Windows (fallback khi chưa chọn). Trả null nếu không đọc được —
    /// caller phải báo lỗi rõ (KHÔNG hardcode "Microsoft Print to PDF" — sẽ in nhầm ra PDF).</summary>
    internal static string? GetDefaultPrinterName()
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
        return null;
    }

    /// <summary>Chờ job in vừa đẩy (sau printto) HOÀN TẤT trên máy in — in LẦN LƯỢT từng file (như Print
    /// Conductor), không chồng đống job lên máy. Poll spooler queue; baseline = job đang chạy TRƯỚC printto.</summary>
    private static void WaitForPrintCompletion(System.Printing.PrintQueue q, int baseline)
    {
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(90);
            var jobSeen = false;
            while (DateTime.UtcNow < deadline)
            {
                var active = ActiveJobCount(q);
                if (active > baseline) jobSeen = true;
                if (jobSeen && active <= baseline) return;   // job mới đã in xong
                System.Threading.Thread.Sleep(300);
            }
        }
        catch { /* máy in lỗi/lỗi đọc queue — không chờ (best-effort) */ }
    }

    private static int ActiveJobCount(System.Printing.PrintQueue q)
    {
        try
        {
            var jobs = q.GetPrintJobInfoCollection();
            var active = 0;
            foreach (var j in jobs)
                if (j.JobStatus is not (System.Printing.PrintJobStatus.Completed or System.Printing.PrintJobStatus.Deleted))
                    active++;
            return active;
        }
        catch { return 0; }
    }
}