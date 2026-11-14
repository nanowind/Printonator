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
/// Màu / 2 mặt / khổ giấy thì ép được qua DEVMODE của máy in (CLI không có tham số cho chúng).
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
        // soffice CLI không có tham số trang → KHÔNG lọc được lẻ/chẵn. In cả file mà vẫn báo Done là bug
        // im lặng (user chọn "chỉ trang lẻ" vẫn ra đủ) → từ chối TRƯỚC khi kiểm máy in/file cho deterministic.
        if (job.Config.Parity != PageParityFilter.All)
            return Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.UnsupportedOption,
                Category = PrintErrorCategory.Config,
                Message = $"LibreOffice không in riêng trang lẻ/chẵn được (chỉ in cả file) — nên không in {job.FileName}.",
                Hint = "Cài MS Office để in file Office đúng cấu hình, bỏ bộ lọc lẻ/chẵn, hoặc nhập page range cụ thể (vd \"1-3,5\").",
            });

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

        // Màu / 2 mặt / khổ giấy: soffice CLI không có tham số cho mấy option này — phải ép qua
        // DEVMODE của máy in TRƯỚC khi khởi động (soffice đọc cấu hình driver lúc start).
        var devModePrinter = printer ?? DefaultPrinter.GetWindowsDefaultPrinterName();
        Result<PrinterDialogs.DevModeLease?>? leaseRes = null;   // không biết máy in → không đụng DEVMODE
        if (!string.IsNullOrWhiteSpace(devModePrinter))
            leaseRes = PrinterDialogs.ApplyDevMode(devModePrinter, job.Config);
        if (leaseRes is { IsSuccess: false } && job.Config.ColorMode is PrintColorMode.Grayscale or PrintColorMode.Color)
            return Result<bool>.Fail(leaseRes.Error!);   // không in sai màu trong im lặng
        if (leaseRes is { IsSuccess: false })
            Log($"ApplyDevMode fail printer='{devModePrinter}': {leaseRes.Error}");
        using var devModeLease = leaseRes?.Value;

        // -p            → in ra máy in MẶC ĐỊNH của Windows (tài liệu chính thức LibreOffice)
        // --pt "Tên máy" → in ra máy chỉ định, đóng file sau khi in
        var printArg = printer is null ? "-p" : $"--pt \"{printer}\"";
        // Chống argument injection: dùng ArgumentList thay vì ghép chuỗi → đường dẫn file có ký tự đặc biệt
        // (dấu nháy, gạch chéo ngược, null) không bị tách/sửa tham số của soffice.
        var psi = new ProcessStartInfo
        {
            FileName = soffice,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        psi.ArgumentList.Add("--headless");
        psi.ArgumentList.Add("--norestore");
        psi.ArgumentList.Add("--nologo");
        psi.ArgumentList.Add(printArg);
        psi.ArgumentList.Add(job.FilePath);

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
                    Hint = "Kiểm tra hàng đợi máy in trước — máy có thể đã in một phần (in lại sẽ trùng); rồi xem LibreOffice có đang hiện dialog ẩn hoặc profile bị khóa không, nếu có thì đóng tiến trình soffice kẹt rồi thử lại.",
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

    /// <summary>Ghi log debug vào %TEMP%\printonator-office.log (không bao giờ ném).</summary>
    private static void Log(string msg)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "printonator-office.log"),
                $"{DateTimeOffset.Now:O} {msg}\n");
        }
        catch { }
    }
}