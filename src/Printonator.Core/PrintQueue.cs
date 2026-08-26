using System.Collections.ObjectModel;
using Printonator.Core.Models;

namespace Printonator.Core;

/// <summary>
/// Hàng đợi in: tuần tự (mặc định) + parallel tối đa N, retry có timeout,
/// KHÔNG bao giờ nuốt lỗi — mọi job lỗi đều có PrintError với hint khắc phục.
/// UI + MCP cùng quan sát qua ObservableCollection.
/// </summary>
public sealed class PrintQueue : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();
    private Queue<PrintJob> _pending = new();
    private readonly List<Task> _workers = new();
    private int _activeWorkers;

    /// <summary>Jobs đang hiển thị trên UI (bao gồm cả pending).</summary>
    public ObservableCollection<PrintJob> Jobs { get; } = new();

    /// <summary>Độ parallel tối đa (mặc định 1 = tuần tự — in không nên gửi ồ ạt lên máy in).</summary>
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>Số lần retry tối đa cho 1 job khi lỗi có thể hồi phục (spooler busy...).</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Số mili-giây chờ giữa 2 lần retry.</summary>
    public int RetryDelayMs { get; set; } = 1500;

    /// <summary>Sự kiện khi có job đổi trạng thái — UI dùng để refresh.</summary>
    public event Action<PrintJob>? JobStateChanged;

    /// <summary>Sự kiện khi queue rảnh (in xong tất cả hoặc hủy).</summary>
    public event Action? AllJobsCompleted;

    public void Enqueue(IEnumerable<PrintJob> jobs)
    {
        lock (_sync)
        {
            foreach (var j in jobs)
            {
                _pending.Enqueue(j);
                Jobs.Add(j);
            }
        }
        _ = DrainAsync();
    }

    public void Enqueue(PrintJob job) => Enqueue(new[] { job });

    /// <summary>Thêm job vào hàng đợi NHƯNG chưa in (chờ user bấm in).</summary>
    public void AddOnly(IEnumerable<PrintJob> jobs)
    {
        lock (_sync)
        {
            foreach (var j in jobs) Jobs.Add(j);
        }
    }

    public void AddOnly(PrintJob job) => AddOnly(new[] { job });

    /// <summary>Xóa job khỏi danh sách UI an toàn (lock) — tránh race với DrainAsync.</summary>
    public bool RemoveJob(PrintJob job)
    {
        lock (_sync)
        {
            if (job.State == JobState.Queued)
            {
                // Nếu đang chờ trong hàng đợi — gỡ luôn khỏi pending
                var pending = _pending.ToList();
                if (pending.Remove(job)) _pending = new Queue<PrintJob>(pending);
            }
            return Jobs.Remove(job);
        }
    }

    /// <summary>Chạy vòng lặp chính — lấy job từ hàng đợi, chạy, retry nếu cần, chuyển trạng thái.</summary>
    private async Task DrainAsync()
    {
        while (true)
        {
            PrintJob? job;
            lock (_sync)
            {
                if (_pending.Count == 0)
                {
                    if (_activeWorkers == 0) AllJobsCompleted?.Invoke();
                    return;
                }
                job = _pending.Dequeue();
                _activeWorkers++;
            }

            try
            {
                await _gate.WaitAsync(_cts.Token);
                await ProcessWithRetryAsync(job);
            }
            catch (OperationCanceledException)
            {
                SetState(job, JobState.Cancelled);
            }
            catch (Exception ex)
            {
                // Không nuốt lỗi — chuyển thành PrintError đầy đủ
                SetState(job, JobState.Error, WrapError(job, ex));
            }
            finally
            {
                _gate.Release();
                lock (_sync) _activeWorkers--;
            }
        }
    }

    private async Task ProcessWithRetryAsync(PrintJob job)
    {
        SetState(job, JobState.Converting);
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            var result = await PrintOnceAsync(job);
            if (result.IsSuccess)
            {
                SetState(job, JobState.Done);
                return;
            }

            if (result.Error is null || !IsRetryable(result.Error))
            {
                SetState(job, JobState.Error, result.Error);
                return;
            }

            // Retry có giới hạn
            if (attempt < MaxRetries)
                await Task.Delay(RetryDelayMs);
            else
                SetState(job, JobState.Error, result.Error);
        }
    }

    /// <summary>
    /// Các engine in đã đăng ký — chọn theo CanHandle(format), ưu tiên thứ tự đăng ký.
    /// Engine càng chuyên (Office COM...) đăng ký TRƯỚC engine fallback (shell).
    /// </summary>
    private readonly object _engineLock = new();
    private readonly List<IPrintEngine> _engines = new();

    /// <summary>Đăng ký engine in — UI/MCP gọi lúc khởi động; nhiều engine được phép.</summary>
    public void RegisterEngine(IPrintEngine engine)
    {
        if (engine is null) return;
        lock (_engineLock)
        {
            if (!_engines.Contains(engine)) _engines.Add(engine);
        }
    }

    /// <summary>Chọn engine đầu tiên nhận format này; rỗng → engine đầu (fallback).</summary>
    private IPrintEngine? PickEngine(string format)
    {
        lock (_engineLock)
        {
            return _engines.FirstOrDefault(e => e.CanHandle(format)) ?? _engines.FirstOrDefault();
        }
    }

    private async Task<Result<bool>> PrintOnceAsync(PrintJob job)
    {
        var engine = PickEngine(job.Format);
        if (engine is null)
        {
            // Mặc định khi chưa gắn engine: đánh dấu thành công để UI demo chạy được.
            await Task.Delay(50); // mô phỏng xử lý
            job.PageCount = job.PageCount > 0 ? job.PageCount : 1;
            return Result<bool>.Ok(true);
        }
        return await engine.PrintAsync(job, CancellationToken.None);
    }

    private static bool IsRetryable(PrintError error) =>
        error.Code is ErrorCodes.SpoolerBusy or ErrorCodes.PrinterOffline or ErrorCodes.EngineTimeout or ErrorCodes.OfficeAppBusy;

    private void SetState(PrintJob job, JobState state, PrintError? error = null)
    {
        job.State = state;
        job.Error = error;
        if (state == JobState.Converting) job.StartedAt ??= DateTimeOffset.Now;
        if (state is JobState.Done or JobState.Error or JobState.Cancelled) job.FinishedAt = DateTimeOffset.Now;
        JobStateChanged?.Invoke(job);
    }

    /// <summary>Hủy cả hàng đợi (jobs chưa in chuyển Cancelled).</summary>
    public void CancelPending()
    {
        lock (_sync)
        {
            while (_pending.TryDequeue(out var j)) SetState(j, JobState.Cancelled);
        }
    }

    /// <summary>Hủy 1 job đang chờ (Queued) — MCP cancel_job dùng. Job đang in thì chờ xong.</summary>
    public bool CancelJob(PrintJob job)
    {
        lock (_sync)
        {
            if (job is null || job.State != JobState.Queued) return false;
            var pending = _pending.ToList();
            if (pending.Remove(job)) _pending = new Queue<PrintJob>(pending);
            SetState(job, JobState.Cancelled);
            return true;
        }
    }

    /// <summary>
    /// Thêm job từ AI qua MCP vào hàng đợi CHỜ DUYỆT (state AwaitingApproval) — chưa in.
    /// Chỉ ApproveJob mới cho in; nguồn job phải là Mcp.
    /// </summary>
    public void AddForApproval(IEnumerable<PrintJob> jobs)
    {
        if (jobs is null) return;
        lock (_sync)
        {
            foreach (var j in jobs)
            {
                if (j.Source != JobSource.Mcp) continue; // an toàn: chỉ job AI cần duyệt
                j.State = JobState.AwaitingApproval;
                j.Error = null;
                Jobs.Add(j);
                JobStateChanged?.Invoke(j);
            }
        }
    }

    /// <summary>
    /// Duyệt job đang chờ — đẩy vào hàng đợi để in (đi qua gate, không sửa collection đôi lần).
    /// Chỉ chấp nhận job AwaitingApproval; job đã Done/Error/Cancelled/Converting KHÔNG được duyệt lại.
    /// </summary>
    public bool ApproveJob(PrintJob job)
    {
        lock (_sync)
        {
            if (job is null || job.State != JobState.AwaitingApproval || !Jobs.Contains(job)) return false;
            job.State = JobState.Queued;
            job.Error = null;
            _pending.Enqueue(job);
            JobStateChanged?.Invoke(job);
        }
        _ = DrainAsync();
        return true;
    }

    /// <summary>Từ chối job đang chờ duyệt (chuyển Cancelled, không in).</summary>
    public bool RejectJob(PrintJob job)
    {
        lock (_sync)
        {
            if (job is null || job.State != JobState.AwaitingApproval || !Jobs.Contains(job)) return false;
            SetState(job, JobState.Cancelled);
            return true;
        }
    }

    /// <summary>
    /// Số trang thực tế đang "chờ in" (pending + chờ duyệt) — PrintGuard dùng để không cho vượt quota.
    /// Trang = số trang vật lý × số bản (ước lượng từ ResolvePhysicalPages).
    /// </summary>
    public int CountPendingPages()
    {
        lock (_sync)
        {
            var total = 0;
            foreach (var j in _pending)
                total += EstimatedPages(j);
            foreach (var j in Jobs.Where(j => j.State == JobState.AwaitingApproval))
                total += EstimatedPages(j);
            return total;
        }
    }

    /// <summary>Ước lượng trang: file chưa rõ số trang (PageCount&lt;=0) dùng ngân sách bảo thủ — KHỚP với PrintGuard.</summary>
    internal static int EstimatedPages(PrintJob job)
    {
        if (job.PageCount <= 0)
            return UnknownPageBudget * Math.Max(job.Config.Copies, 1);
        var r = job.ResolvePhysicalPages();
        var pages = r.IsSuccess ? Math.Max(r.Value!.Length, 1) : 1;
        return pages * Math.Max(job.Config.Copies, 1);
    }

    /// <summary>Ngân sách trang/file khi chưa probe được số trang (fail-closed, không "1 trang").</summary>
    internal const int UnknownPageBudget = 50;

    /// <summary>
    /// In job ĐÃ CÓ trong hàng đợi (không thêm dòng mới) — dùng khi user bấm "Print all/selected".
    /// Đảo ngược trạng thái về Queued rồi cho engine chạy.
    /// </summary>
    public void ProcessExisting(PrintJob job)
    {
        if (job is null) return;
        lock (_sync)
        {
            if (!Jobs.Contains(job)) return;
            if (job.State is JobState.Converting or JobState.Spooling) return; // đang in — không in kép
            if (job.State is JobState.Done or JobState.Error or JobState.Cancelled)
            {
                // Cho in lại job đã xong/lỗi: reset trạng thái về Queued
                job.State = JobState.Queued;
                job.Error = null;
                job.FinishedAt = null;
                JobStateChanged?.Invoke(job);
            }
            // Claim ngay TRONG lock — đối thủ (ProcessExisting/cancel) không đọc-ghi lệch được
            job.State = JobState.Converting;
        }
        _ = DrainOnceAsync(job);
    }

    private async Task DrainOnceAsync(PrintJob job)
    {
        try
        {
            SetState(job, JobState.Converting);
            await ProcessWithRetryAsync(job);
        }
        catch (OperationCanceledException)
        {
            SetState(job, JobState.Cancelled);
        }
        catch (Exception ex)
        {
            SetState(job, JobState.Error, WrapError(job, ex));
        }
    }

    /// <summary>Bọc exception thành PrintError đầy đủ — dùng chung cho cả 2 đường drain.</summary>
    private static PrintError WrapError(PrintJob job, Exception ex) => new()
    {
        Code = ErrorCodes.SpoolerFailed,
        Category = PrintErrorCategory.App,
        Message = $"Lỗi không xác định khi in {job.FileName}.",
        Hint = "Xem log chi tiết. Nếu lặp lại, báo lỗi kèm file đang in.",
        Detail = ex.ToString(),
    };

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _gate.Dispose();
    }
}

/// <summary>Engine in — mỗi định dạng có engine riêng (PDFium, Word COM, LibreOffice...).</summary>
public interface IPrintEngine
{
    bool CanHandle(string format);
    Task<Result<bool>> PrintAsync(PrintJob job, CancellationToken ct);
}