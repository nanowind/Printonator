using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Printonator.Core;
using Printonator.Core.Models;
using Printonator.Spool.Printing;
using Printonator.UI.Localization;

namespace Printonator.UI;

/// <summary>
/// Orchestrator xử lý in lô — quản lý batch lifecycle, tuần tự in, completion events.
/// KHÔNG phụ thuộc trực tiếp control XAML (gọi callback qua event).
/// </summary>
public sealed class PrintBatchOrchestrator
{
    private readonly PrintQueue _queue;
    private readonly Dispatcher _dispatcher;
    private readonly Func<PrinterInfo?> _selectedPrinterGetter;
    private readonly Window _owner;

    /// <summary>Fire khi lô in hoàn tất — tham số = danh sách job đã về trạng thái cuối (Done/Error/Cancelled) trong lô.</summary>
    public event Action<IReadOnlyList<PrintJob>>? AllCompleted;

    /// <summary>Fire khi lô in bị dừng do lỗi — tham số = (số file đã in xong, job lỗi).</summary>
    public event Action<int, PrintJob?>? BatchStopped;

    /// <summary>Fire khi cần hiện toast.</summary>
    public event Action<string>? ToastRequested;

    /// <summary>Fire khi cần hiện banner lỗi.</summary>
    public event Action<string?, string, string>? BannerRequested;

    /// <summary>Fire khi cần cập nhật footer.</summary>
    public event Action? FooterUpdated;

    /// <summary>Fire khi cần refresh JobList (sau khi batch state thay đổi).</summary>
    public event Action? RefreshRequested;

    /// <summary>Tên lô in do user đặt trên dòng bìa (MainWindow set). Rỗng → engine tự fallback tên thư mục/ngày giờ.</summary>
    public string? BatchName { get; set; }

    public PrintBatchOrchestrator(PrintQueue queue, Dispatcher dispatcher,
        Func<PrinterInfo?> selectedPrinterGetter, Window owner)
    {
        _queue = queue;
        _dispatcher = dispatcher;
        _selectedPrinterGetter = selectedPrinterGetter;
        _owner = owner;
    }

    public IEnumerable<PrintJob> OrderByDisplay(IEnumerable<PrintJob> jobs)
    {
        var view = CollectionViewSource.GetDefaultView(_queue.Jobs);
        var order = new Dictionary<PrintJob, int>();
        var idx = 0;
        foreach (var j in view.Groups.Count > 0 ? FlattenDisplayGroups(view.Groups) : view.Cast<PrintJob>())
            order.TryAdd(j, idx++);
        return jobs.OrderBy(j => order.TryGetValue(j, out var i) ? i : int.MaxValue);
    }

    private static IEnumerable<PrintJob> FlattenDisplayGroups(System.Collections.IEnumerable groups)
    {
        foreach (var o in groups)
        {
            if (o is System.Windows.Data.CollectionViewGroup g)
            {
                foreach (var sub in FlattenDisplayGroups(g.Items)) yield return sub;
            }
            else if (o is PrintJob j)
                yield return j;
        }
    }

    public void ApplySelectedPrinter(IEnumerable<PrintJob> jobs)
    {
        var printer = _selectedPrinterGetter()?.Name ?? "mặc định";
        foreach (var j in jobs)
            if (!j.HasPerFilePrinter)
                j.Config.PrinterName = printer;
    }

    /// <summary>
    /// In batch — xác nhận in lại, pre-flight, rồi chạy StartPrintBatch.
    /// </summary>
    public async Task PrintJobsAsync(List<PrintJob> jobs, string action)
    {
        var ready = jobs.Where(j => j.State is JobState.Queued or JobState.Done or JobState.Error or JobState.Cancelled).ToList();
        if (ready.Count == 0) { BannerRequested?.Invoke(ErrorCodes.NoFilesSelected, L10n.S(Keys.Banner.NoFilesSelected), ""); return; }

        // ===== Xác nhận IN LẠI file đã in trước đó =====
        var alreadyPrinted = ready.Where(j => j.State == JobState.Done).ToList();
        if (alreadyPrinted.Count > 0)
        {
            var ask = MessageBox.Show(
                L10n.F(Keys.Banner.ConfirmRePrint, alreadyPrinted.Count),
                L10n.S(Keys.Banner.ConfirmRePrintTitle), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (ask == MessageBoxResult.Cancel) return;
            if (ask == MessageBoxResult.No)
                ready = ready.Where(j => j.State != JobState.Done).ToList();
            if (ready.Count == 0)
            {
                BannerRequested?.Invoke(ErrorCodes.NoFilesSelected, L10n.S(Keys.Banner.NoFilesAfterSkip), "");
                return;
            }
        }

        ApplySelectedPrinter(ready);

        try
        {
            // ===== Gộp file (T2.4): job bật MergeIntoOneFile → in chung 1 bản qua MergePrintEngine =====
            var mergeJobs = ready.Where(j => j.Config.MergeIntoOneFile).ToList();
            var normalJobs = ready.Where(j => !j.Config.MergeIntoOneFile).ToList();
            if (mergeJobs.Count > 1)
            {
                var merged = await new MergePrintEngine().MergeAndPrintAsync(mergeJobs, CancellationToken.None);
                if (merged.IsSuccess)
                {
                    // Merge in xong ra spooler — đánh dấu file nguồn DONE để không bị "in lại" khi bấm
                    // In tất cả lần sau (chúng đã in chung 1 bản qua MergePrintEngine).
                    foreach (var j in mergeJobs) _queue.MarkDone(j);
                    ready = normalJobs;
                    if (ready.Count == 0)
                    {
                        ToastRequested?.Invoke($"Đã in gộp {mergeJobs.Count} file.");
                        return;
                    }
                }
                else
                {
                    // Merge thất bại → báo + giữ nguyên để in TỪNG FILE như bình thường (không mất lô)
                    BannerRequested?.Invoke(merged.Error?.Code ?? ErrorCodes.EngineFailed,
                        merged.Error?.Message ?? "Không gộp được file — in từng file riêng.",
                        merged.Error?.Hint ?? "");
                }
            }
        }
        catch (Exception ex)
        {
            // Fire-and-forget (PrintJobs wrapper) — exception phải biến thành banner, không được bay ra ngoài.
            BannerRequested?.Invoke(ErrorCodes.EngineFailed,
                "Không gộp được file — in từng file riêng.",
                ex.Message);
            return;
        }

        // ===== Pre-flight gate (chỉ khi lô lớn) =====
        const int ConfirmSheetThreshold = 100;
        var sheets = PrintConfirmWindow.EstimateSheets(ready);
        if (sheets > ConfirmSheetThreshold
            && !PrintConfirmWindow.Show(_owner, _selectedPrinterGetter()?.Name ?? L10n.S(Keys.Main.PrinterDefaultName), ready, sheets))
        {
            return;
        }

        // ===== Trang bìa (T2.1): in 1 trang bìa TRƯỚC lô — đặt SAU cổng xác nhận để user bấm Hủy
        // thì không tốn tờ bìa nào. Tính trên `ready` SAU merge (đúng số file/trang thực in).
        // Bọc try/catch RIÊNG: bìa lỗi → toast + VẪN in lô (không được nuốt cả lô).
        // Lô 1 file không in bìa (kể cả khi cờ CoverPage còn sót).
        if (ready.Count >= 2 && ready.Any(j => j.Config.CoverPage))
        {
            try
            {
                var cfg = ready.First().Config;
                // Bìa luôn khổ cố định A4 dọc 1 mặt 1 bản — không thừa hưởng AsDocument (Chromium rơi về Letter)
                // hay zoom/duplex của lô.
                var coverCfg = cfg.Clone();
                coverCfg.PaperSize = "A4";
                coverCfg.Orientation = PrintOrientation.Portrait;
                coverCfg.ScaleMode = PrintScaleMode.Original;
                coverCfg.ScalePercent = 100;
                coverCfg.DuplexMode = PrintDuplexMode.Simplex;
                coverCfg.Copies = 1;

                var batchTitle = BatchName?.Trim();
                if (batchTitle is { Length: > 60 }) batchTitle = batchTitle[..60];   // khớp MaxLength ô nhập
                if (string.IsNullOrWhiteSpace(batchTitle)) batchTitle = null;         // để engine tự fallback

                var appVersion = typeof(PrintBatchOrchestrator).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
                var printerName = cfg.PrinterName ?? "mặc định";
                var coverOutputDir = Path.GetDirectoryName(ready.First().FilePath);

                // Mọi format đều ? khi bìa dựng TRƯỚC khi in (PageCount điền trong lúc in) → probe hết ở đây.
                // PDF: nhanh (không mở app). Ảnh: 1. Office: 1 session/nhóm (~4s lần đầu Excel). TXT: giữ ? (không ước lượng bừa).
                // Không bao giờ ném — probe lỗi → bìa ghi ? cho file đó.
                try { await PageCountProber.ProbeAsync(ready, CancellationToken.None); }
                catch { /* PageCount giữ 0 → bìa ghi "?" */ }

                var html = CoverPageRenderer.BuildHtml(ready, batchTitle, DateTime.Now, appVersion, printerName);
                var (ok, b64) = await CoverPageRenderer.RenderCoverAsync(html, coverCfg, CancellationToken.None);
                if (ok && b64 is not null)
                {
                    var printed = await CoverPageRenderer.PrintCoverAsync(
                        b64, printerName, coverOutputDir, batchTitle ?? "Trang bia", CancellationToken.None);
                    // Lỗi in bìa = lỗi máy in → BANNER (giữ mã lỗi + gợi ý), KHÔNG toast
                    // (toast tự ẩn sau vài giây, không vào bell/history → user mất dấu vết lỗi).
                    if (!printed.IsSuccess)
                        BannerRequested?.Invoke(
                            printed.Error?.Code ?? ErrorCodes.SpoolerFailed,
                            L10n.F(Keys.Banner.CoverPrintFailed, printed.Error?.Message ?? ""),
                            printed.Error?.Hint ?? "");
                }
                else
                {
                    // Không dựng được bìa (thường do máy không có Chrome/Edge) — KHÔNG phải lỗi máy in,
                    // chỉ là bỏ qua bìa → toast là đủ, lô vẫn in bình thường.
                    ToastRequested?.Invoke(L10n.S(Keys.Toast.CoverSkipped));
                }
            }
            catch (Exception ex)
            {
                // Bìa hỏng (vd base64 sai → FormatException) KHÔNG được chặn lô → banner + VẪN in lô.
                BannerRequested?.Invoke(ErrorCodes.EngineFailed, L10n.S(Keys.Banner.CoverBuildFailed), ex.Message);
            }
        }

        StartPrintBatch(ready, action);
    }

    /// <summary>
    /// Đường thực thi CHUNG cho mọi lệnh in — đẩy job, refresh, fire completion.
    /// </summary>
    public void StartPrintBatch(List<PrintJob> ready, string action)
    {
        if (ready.Count == 0) { BannerRequested?.Invoke(ErrorCodes.NoFilesSelected, L10n.S(Keys.Banner.NoFilesSelected), ""); return; }

        _queue.ProcessBatch(ready);
        ToastRequested?.Invoke(L10n.F(Keys.Toast.BatchStart, action));
        RefreshRequested?.Invoke();
        FooterUpdated?.Invoke();

        _ = WaitBatchDoneAsync(ready);
    }

    /// <summary>
    /// Chờ toàn bộ job trong lô về trạng thái cuối, rồi fire completion 1 lần.
    /// </summary>
    private async Task WaitBatchDoneAsync(List<PrintJob> batch)
    {
        var terminal = new[] { JobState.Done, JobState.Error, JobState.Cancelled };
        var toWait = new HashSet<PrintJob>(batch);
        var interrupted = false;
        try
        {
            while (toWait.Count > 0)
            {
                var pending = toWait.Where(j => !terminal.Contains(j.State)).ToList();
                if (pending.Count == 0) break;

                // Stop-on-error: 1 file lỗi → queue pause, các file sau vẫn Queued → ngắt lô
                // (không chờ vô hạn, không báo "in xong").
                if (_queue.IsPaused && _queue.StoppedByError)
                {
                    interrupted = true;
                    break;
                }

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Action<PrintJob> handler = _ => { };
                handler = j =>
                {
                    if (pending.Contains(j))
                    {
                        _queue.JobStateChanged -= handler;
                        tcs.TrySetResult(true);
                    }
                };
                _queue.JobStateChanged += handler;
                if (pending.All(j => terminal.Contains(j.State)))
                {
                    _queue.JobStateChanged -= handler;
                    tcs.TrySetResult(true);
                }
                await tcs.Task;
                toWait.RemoveWhere(j => terminal.Contains(j.State));
            }
        }
        catch (Exception) { }

        if (interrupted)
        {
            var done = batch.Count(j => j.State == JobState.Done);
            var failed = batch.FirstOrDefault(j => j.State == JobState.Error);
            try { await _dispatcher.BeginInvoke(new Action(() => BatchStopped?.Invoke(done, failed))); }
            catch { }
            return;
        }

        var completed = batch.Where(j => terminal.Contains(j.State)).ToList();
        // Batch có job lỗi nhưng không kịp set interrupted (vd 1 file duy nhất lỗi ngay) →
        // không fire "In xong" mà fire "dừng do lỗi" để UI báo đúng.
        if (!interrupted && completed.Any(j => j.State == JobState.Error))
        {
            var done = completed.Count(j => j.State == JobState.Done);
            var failed = completed.FirstOrDefault(j => j.State == JobState.Error);
            try { await _dispatcher.BeginInvoke(new Action(() => BatchStopped?.Invoke(done, failed))); }
            catch { }
            return;
        }

        try { await _dispatcher.BeginInvoke(new Action(() => AllCompleted?.Invoke(completed))); }
        catch { }
    }
}
