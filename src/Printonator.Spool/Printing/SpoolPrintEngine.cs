using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Printonator.Core;
using Printonator.Core.Models;
using Printonator.Core.Printing;

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
        List<Process> spawned = new();   // scope ngoài try — catch cần biết đã gửi bao nhiêu bản
        var copies = Math.Max(job.Config.Copies, 1);   // cùng lý do: catch dùng để báo "k/N bản"
        try
        {
            // PrinterName rỗng/null = "máy in mặc định Windows" (combo toolbar ở item "Theo máy thanh công cụ"
            // → PrintJob.Config.PrinterName bị đặt null). Resolve TRƯỚC khi kiểm rỗng, giống GdiPrintEngine —
            // nếu báo lỗi ở đây thì file Office/Excel chết PRINTER_NOT_FOUND dù lô PDF vẫn in được.
            var printerName = job.Config.PrinterName;
            if (DefaultPrinter.IsDefault(printerName))
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

            if (!File.Exists(job.FilePath))
                return Task.FromResult(Result<bool>.Fail(new PrintError
                {
                    Code = ErrorCodes.FileNotFound,
                    Category = PrintErrorCategory.System,
                    Message = $"File không tồn tại: {job.FilePath}",
                    Hint = "File bị xóa hoặc di chuyển — kiểm tra lại đường dẫn."
                }));

            ct.ThrowIfCancellationRequested();

            // printto (verb của shell) KHÔNG có tham số số bản — handler luôn in 1 bản. Phải tự lặp
            // N lần, nếu không job "4 bản" rơi xuống đường shell sẽ chỉ ra 1 bản mà vẫn báo Done
            // (bug thật 2026-10-21: Word COM fail → rớt shell → in 1 bản).
            // `copies` khai báo ở đầu method (catch cũng cần).

            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "printonator-office.log"),
                    $"{DateTimeOffset.Now:O} SpoolPrintEngine printto printer='{printerName}' copies={copies} file={job.FileName}\n");
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
            // Đọc queue là BEST-EFFORT: không mở được thì vẫn phải in (trước đây ném ở đây là
            // không in gì cả mà vẫn trả Ok — nuốt mất job).
            // Server phải SỐNG suốt vòng lặp — PrintQueue lấy từ nó, dispose sớm thì
            // ActiveJobCount nuốt lỗi trả 0 → mỗi bản chờ mù 90s.
            System.Printing.LocalPrintServer? server = null;
            System.Printing.PrintQueue? q = null;
            try
            {
                server = new System.Printing.LocalPrintServer();
                q = server.GetPrintQueue(printerName);
            }
            catch (Exception) { /* không đọc được queue — vẫn in, chỉ không chờ */ }

            // Deadline cho CẢ job, co giãn theo số bản: mỗi bản thêm 60s (in 10 bản vẫn đủ thời gian),
            // nhưng chặn trần 15 phút để máy kẹt giấy không treo drain vô hạn.
            var deadline = DateTime.UtcNow.AddSeconds(Math.Min(90 + 60 * (copies - 1), 900));
            try
            {
                for (var copy = 0; copy < copies; copy++)
                {
                    ct.ThrowIfCancellationRequested();

                    // KHÔNG kiểm deadline ở đây: chỉ nhánh "máy kẹt thật" (TimedOut) mới được báo lỗi.
                    // "Không thấy job" (WSD/mạng) là chuyện bình thường — báo lỗi ở đây sẽ chặn oan
                    // job vốn đã in đúng (bug bị review bắt ở vòng 3).

                    int baseline = q is null ? 0 : ActiveJobCount(q);   // baseline TRƯỚC printto = biết job nào là của mình

                    var proc = Process.Start(psi);
                    if (proc is null)
                        return Task.FromResult(Result<bool>.Fail(spawned.Count > 0
                            // ĐÃ gửi ≥1 bản → không được để chuỗi engine chạy tiếp (in lại cả file).
                            ? new PrintError
                            {
                                Code = ErrorCodes.EngineTimeout,
                                Category = PrintErrorCategory.System,
                                Message = $"Đã gửi {spawned.Count}/{copies} bản của {job.FileName} rồi không khởi động được lệnh in tiếp.",
                                Hint = "Kiểm tra hàng đợi máy in rồi in lại các bản còn thiếu.",
                            }
                            : new PrintError
                            {
                                Code = ErrorCodes.SpoolerFailed,
                                Category = PrintErrorCategory.Printer,
                                Message = $"Không khởi động được lệnh in tới \"{printerName}\".",
                                Hint = "Kiểm tra máy in có tồn tại và app đọc file có hoạt động."
                            }));
                    spawned.Add(proc);

                    // Chờ CẢ bản cuối: DrainLoopAsync in tuần tự từng FILE, nên bản cuối của file này
                    // phải xong trước khi file kế được đẩy (copies=1 là đường phổ biến nhất — break ở đây
                    // làm lô in dồn cùng lúc, đúng lỗi file này sinh ra để tránh).
                    var wait = q is null
                        ? PrintWaitResult.NotSeen
                        : WaitForPrintCompletion(q, baseline, ct, deadline);

                    if (wait == PrintWaitResult.TimedOut)
                        return Task.FromResult(Result<bool>.Fail(new PrintError
                        {
                            Code = ErrorCodes.EngineTimeout,
                            Category = PrintErrorCategory.System,
                            Message = $"In {job.FileName} quá lâu — đã bắt đầu {spawned.Count}/{copies} bản rồi dừng.",
                            Hint = "Kiểm tra hàng đợi máy in (kẹt giấy/hết mực/offline) rồi in lại các bản còn thiếu.",
                        }));

                    if (wait == PrintWaitResult.NotSeen && copy < copies - 1)
                        // Không đọc được queue (máy mạng/WSD) hoặc handler không để lại job trong spooler
                        // → KHÔNG có gì bảo đảm bản trước đã nhả file. Nghỉ một nhịp để handler bàn giao
                        // xong, tránh N lệnh printto đè nhau mở cùng 1 file (Word/Excel single-instance
                        // → chỉ 1 bản thật + N-1 process mồ côi).
                        ct.WaitHandle.WaitOne(1500);   // cancel-aware (Sleep sẽ chặn nút Hủy tới 1.5s)
                }
            }
            catch (OperationCanceledException)
            {
                // PHẢI kill Ở ĐÂY: `finally` bên dưới Dispose các Process, sau đó object không còn
                // liên kết → HasExited/Kill ném InvalidOperationException và process in vẫn sống.
                foreach (var p in spawned) CleanupSpawnedProc(p);
                throw;
            }
            finally
            {
                foreach (var p in spawned)
                {
                    try { p.Dispose(); } catch { }
                }
                try { q?.Dispose(); } catch { }
                try { server?.Dispose(); } catch { }
            }

            if (job.PageCount <= 0) job.PageCount = 1; // shell không biết số trang — giữ giá trị đã probe nếu có
            return Task.FromResult(Result<bool>.Ok(true));
        }
        catch (OperationCanceledException)
        {
            // CANCEL (user bấm Cancel) → RETHROW để OCE lan tới PrintQueue.DrainLoopAsync
            // → job chuyển Cancelled (KHÔNG Error, KHÔNG retry). Process printto đã được dọn
            // ở catch lồng bên trong vòng lặp (trước khi finally Dispose chúng).
            throw;
        }
        catch (Exception ex)
        {
            // Lỗi giữa chừng (đã gửi ≥1 bản) → EngineTimeout: nằm trong break-list của PrintQueue nên
            // chuỗi engine DỪNG, không in lại cả file (tốn giấy thật).
            return Task.FromResult(Result<bool>.Fail(spawned.Count > 0
                ? new PrintError
                {
                    Code = ErrorCodes.EngineTimeout,
                    Category = PrintErrorCategory.System,
                    Message = $"Lỗi khi in {job.FileName} sau {spawned.Count}/{copies} bản.",
                    Hint = "Kiểm tra hàng đợi máy in rồi in lại các bản còn thiếu.",
                    Detail = ex.ToString(),
                }
                : new PrintError
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

    /// <summary>Kết quả chờ 1 bản in — phân biệt "xong thật" với "không thấy job" (cần nghỉ nhịp)
    /// và "hết giờ khi job còn kẹt" (phải dừng, không đẩy thêm bản).</summary>
    private enum PrintWaitResult { Completed, NotSeen, TimedOut }

    /// <summary>Chờ job in vừa đẩy (sau printto) HOÀN TẤT trên máy in — in LẦN LƯỢT từng file (như Print
    /// Conductor), không chồng đống job lên máy. Poll spooler queue; baseline = job đang chạy TRƯỚC printto.</summary>
    private static PrintWaitResult WaitForPrintCompletion(
        System.Printing.PrintQueue q, int baseline, CancellationToken ct, DateTime deadline)
    {
        try
        {
            var jobSeen = false;
            while (DateTime.UtcNow < deadline)
            {
                var active = ActiveJobCount(q);
                if (active > baseline) jobSeen = true;
                if (jobSeen && active <= baseline) return PrintWaitResult.Completed;   // job mới đã in xong
                ct.ThrowIfCancellationRequested();                 // cancel giữa mỗi poll → OCE rethrow
                System.Threading.Thread.Sleep(300);
            }
            // Hết giờ: job CÓ xuất hiện mà chưa rời queue = máy kẹt → dừng.
            // Không thấy job nào = máy không báo (WSD/mạng) → coi như đã đẩy, chỉ cần nghỉ nhịp.
            return jobSeen ? PrintWaitResult.TimedOut : PrintWaitResult.NotSeen;
        }
        catch (OperationCanceledException)
        {
            throw;   // CANCEL → rethrow để PrintAsync dọn process + DrainLoopAsync chuyển Cancelled
        }
        catch { return PrintWaitResult.Completed; /* máy in lỗi/lỗi đọc queue — không chờ (best-effort) */ }
    }

    /// <summary>Dọn process printto engine đã spawn khi job bị cancel — tránh app đọc file mồ côi.</summary>
    private static void CleanupSpawnedProc(Process? proc)
    {
        if (proc is null) return;
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
            }
        }
        catch { /* best-effort — process có thể đã tự thoát */ }
        finally
        {
            proc.Dispose();
        }
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