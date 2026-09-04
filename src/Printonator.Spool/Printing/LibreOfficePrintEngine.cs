using System.Diagnostics;
using System.IO;
using Printonator.Core;
using Printonator.Core.Models;
using Printonator.Core.Printing;

namespace Printonator.Spool.Printing;

/// <summary>
/// Engine in file Office bằng LibreOffice ĐÃ CÀI trên máy user (không bundle — `soffice --headless --pt`).
/// Đăng ký SAU OfficeComPrintEngine: chỉ chạy khi format office MÀ máy KHÔNG có MS Office cho format đó
/// (registry PickEngine = engine đầu tiên CanHandle=true). Không có LibreOffice → fallback SpoolPrintEngine.
/// Giới hạn CLI: không ép page range/copies/N-up — in theo driver + tài liệu (giống "print" của LO).
/// Không nuốt lỗi: hết timeout / lệnh fail → PrintError đầy đủ.
/// </summary>
public sealed class LibreOfficePrintEngine : IPrintEngine
{
    private const int TimeoutSeconds = 120;
    private static readonly string[] OfficeFormats = FileFormatRegistry.OfficeFormats;

    private readonly Func<string?> _sofficeResolver;

    public LibreOfficePrintEngine(Func<string?>? sofficeResolver = null)
        => _sofficeResolver = sofficeResolver ?? new LibreOfficeLocator().ResolveSofficePath;

    /// <summary>Chỉ nhận format office khi MÁY USER có LibreOffice (thứ tự registry đảm bảo MS Office thắng).</summary>
    public bool CanHandle(string format)
    {
        var f = format.ToUpperInvariant();
        if (!OfficeFormats.Contains(f)) return false;
        try { return !string.IsNullOrEmpty(_sofficeResolver()); } catch { return false; }
    }

    public async Task<Result<bool>> PrintAsync(PrintJob job, CancellationToken ct)
    {
        var soffice = GetSoffice();
        if (soffice is null)
            return Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.EngineNotFound,
                Category = PrintErrorCategory.App,
                Message = $"Máy này chưa cài LibreOffice nên không in được {job.FileName}.",
                Hint = "Cài LibreOffice (miễn phí) hoặc cài MS Office để in file Office từ app gốc.",
            });

        if (!File.Exists(job.FilePath))
            return Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.FileNotFound,
                Category = PrintErrorCategory.System,
                Message = $"File không tồn tại: {job.FilePath}",
                Hint = "File bị xóa hoặc di chuyển — kiểm tra lại đường dẫn.",
            });

        var printer = DefaultPrinter.Resolve(job.Config.PrinterName);

        // -p            → in ra máy in MẶC ĐỊNH của Windows (tài liệu chính thức LibreOffice)
        // --pt "Tên máy" → in ra máy chỉ định, đóng file sau khi in
        var printArg = printer is null ? "-p" : $"--pt \"{printer}\"";
        var psi = new ProcessStartInfo
        {
            FileName = soffice,
            Arguments = $"--headless --norestore --nologo {printArg} \"{job.FilePath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
                return Result<bool>.Fail(new PrintError
                {
                    Code = ErrorCodes.SpoolerFailed,
                    Category = PrintErrorCategory.Printer,
                    Message = "LibreOffice không khởi động được.",
                    Hint = "Kiểm tra LibreOffice còn hoạt động (có thể có tiến trình soffice kẹt — tắt rồi thử lại).",
                });

            try
            {
                await proc.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(TimeoutSeconds));
            }
            catch (TimeoutException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return Result<bool>.Fail(new PrintError
                {
                    Code = ErrorCodes.EngineTimeout,
                    Category = PrintErrorCategory.System,
                    Message = $"LibreOffice xử lý {job.FileName} quá lâu ({TimeoutSeconds}s) — đã hủy.",
                    Hint = "LibreOffice có thể đang hiện dialog ẩn hoặc profile bị khóa. Thử khởi động lại máy in/LibreOffice.",
                });
            }
            catch (OperationCanceledException)
            {
                // OCE do cancel — LAN RA (rethrow) để DrainLoopAsync catch (OperationCanceledException) → Cancelled.
                // KHÔNG biến cancel thành EngineTimeout: timeout THẬT (WaitAsync TimeoutException) vẫn Fail(EngineTimeout) ở catch trên.
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            if (proc.ExitCode != 0)
                return Result<bool>.Fail(new PrintError
                {
                    Code = ErrorCodes.SpoolerFailed,
                    Category = PrintErrorCategory.Printer,
                    Message = $"LibreOffice trả lỗi (exit {proc.ExitCode}) khi in {job.FileName}.",
                    Hint = "Mở thử file trong LibreOffice xem file có hỏng không; hoặc máy in không khả dụng.",
                });

            if (job.PageCount <= 0) job.PageCount = 1;
            return Result<bool>.Ok(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.SpoolerFailed,
                Category = PrintErrorCategory.App,
                Message = $"Lỗi khi gọi LibreOffice in {job.FileName}.",
                Hint = "Kiểm tra đường dẫn soffice (PRINTONATOR_LIBREOFFICE nếu cài lẻ).",
                Detail = ex.Message,
            });
        }
    }

    private string? GetSoffice()
    {
        try { return _sofficeResolver(); }
        catch { return null; }
    }
}