using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Printonator.Core;
using Printonator.Core.Models;
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

    /// <summary>Fire khi lô in hoàn tất — tham số = số file đã in xong.</summary>
    public event Action<int>? AllCompleted;

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
            j.Config.PrinterName = printer;
    }

    /// <summary>
    /// In batch — xác nhận in lại, pre-flight, rồi chạy StartPrintBatch.
    /// </summary>
    public void PrintJobs(List<PrintJob> jobs, string action)
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

        // ===== Pre-flight gate (chỉ khi lô lớn) =====
        const int ConfirmSheetThreshold = 100;
        var sheets = PrintConfirmWindow.EstimateSheets(ready);
        if (sheets > ConfirmSheetThreshold
            && !PrintConfirmWindow.Show(_owner, _selectedPrinterGetter()?.Name ?? L10n.S(Keys.Main.PrinterDefaultName), ready, sheets))
        {
            return;
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

        var doneCount = batch.Count(j => j.State == JobState.Done);
        try { await _dispatcher.BeginInvoke(new Action(() => AllCompleted?.Invoke(doneCount))); }
        catch { }
    }
}
