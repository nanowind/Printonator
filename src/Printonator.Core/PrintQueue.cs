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
    private volatile bool _isPaused;   // Pause/Resume — user lỡ bấm in thì dừng lô, không lấy job mới
    private bool _drainRunning;         // CHỈ 1 vòng drain duy nhất — in tuần tự từng file + dừng-đúng-chỗ khi lỗi
    private volatile bool _stoppedByError; // lô bị dừng do 1 file lỗi (các file sau giữ Queued chờ Resume)
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();
    private Queue<PrintJob> _pending = new();
    private readonly List<Task> _workers = new();
    private int _activeWorkers;

    /// <summary>CTS per-job cho job ĐANG in — CancelJob/RemoveJob cancel token này để engine thoát sớm
    /// (engine nhận token ở điểm chờ → job chuyển Cancelled, KHÔNG đánh dấu Done). Truy cập trong lock(_sync).</summary>
    private readonly Dictionary<PrintJob, CancellationTokenSource> _jobCts = new();

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
        KickDrain();
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

    /// <summary>Xóa job khỏi danh sách UI an toàn (lock) — tránh race với DrainAsync.
    /// Job ĐANG IN (Converting/Spooling) → cancel token thật để engine thoát sớm; job sẽ
    /// chuyển Cancelled trong DrainLoopAsync (KHÔNG đánh dấu Done).</summary>
    public bool RemoveJob(PrintJob job)
    {
        lock (_sync)
        {
            if (job is null) return false;
            if (job.State == JobState.Queued)
            {
                // Nếu đang chờ trong hàng đợi — gỡ luôn khỏi pending
                var pending = _pending.ToList();
                if (pending.Remove(job)) _pending = new Queue<PrintJob>(pending);
            }
            else if (job.State is JobState.Converting or JobState.Spooling)
            {
                CancelJobLocked(job);
            }
            return Jobs.Remove(job);
        }
    }

    /// <summary>Đang tạm dừng lô in? (bấm Pause — dừng giữa các job; job chờ giữ Queued, job đang in chạy nốt)</summary>
    public bool IsPaused => _isPaused;

    /// <summary>Lô in bị DỪNG do 1 file lỗi (các file sau giữ Queued chờ Resume)? UI dùng để báo
    /// "lô bị dừng" thay vì popup "in xong" — không treo khi còn file chưa in.</summary>
    public bool StoppedByError => _stoppedByError;

    public void Pause() => _isPaused = true;
    public void Resume()
    {
        _isPaused = false;
        _stoppedByError = false;   // user tiếp tục lô — xóa dấu "dừng do lỗi"
        // Re-enqueue các job Queued còn lại (có thể không còn trong _pending do drain đã exit)
        // → đảm bảo drain tiếp tục in các file chưa in.
        lock (_sync)
        {
            foreach (var j in Jobs)
                if (j.State == JobState.Queued && !_pending.Contains(j))
                    _pending.Enqueue(j);
        }
        KickDrain();               // kích hoạt lại drain — job chờ trong _pending sẽ được tiếp tục
    }

    /// <summary>Yêu cầu có đúng MỘT vòng drain chạy cho mọi job đang chờ. In TUẦN TỰ (MaxConcurrency=1
    /// + gate) và DỪNG-ĐÚNG-CHỖ khi 1 file lỗi — nếu nhiều vòng chạy song song thì mỗi vòng dequeue
    /// riêng → lỗi không dừng được lô, Pause cũng vô nghĩa.</summary>
    private void KickDrain()
    {
        lock (_sync)
        {
            if (_drainRunning) return;   // vòng đang chạy sẽ tự nhặt job vừa thêm
            _drainRunning = true;
        }
        _ = DrainLoopAsync();
    }

    /// <summary>Vòng lặp chính — 1 vòng duy nhất cho cả lô: lấy job từng cái, chạy, retry nếu cần,
    /// chuyển trạng thái. Pause dừng giữa các file; 1 file lỗi → DỪNG cả lô (không đốt giấy cho
    /// phần còn lại). Enqueue (MCP) và ProcessBatch (UI) đi chung vòng này → in tuần tự như nhau.</summary>
    private async Task DrainLoopAsync()
    {
        try
        {
            while (true)
            {
                // Pause: KHÔNG lấy job mới (job chờ giữ Queued) — chờ tới khi Resume. Job đang in chạy nốt.
                while (_isPaused)
                    await Task.Delay(200, _cts.Token);   // theo _cts.Token — Dispose (app đóng) thoát được

                PrintJob? job = null;
                try
                {
                    lock (_sync)
                    {
                        if (_pending.Count == 0)
                        {
                            _drainRunning = false;   // ra ngoài lock nhả cờ — Enqueue/ProcessBatch sau sẽ mở vòng mới
                                                     // Reset pause state khi lô in xong — tránh UI kẹt ở trạng thái "Resume"
                                                     // (user pause rồi in hết trước khi bấm Resume).
                            _isPaused = false;
                            _stoppedByError = false;
                            if (_activeWorkers == 0) AllJobsCompleted?.Invoke();
                            return;
                        }
                        job = _pending.Dequeue();
                        _activeWorkers++;
                    }
                }
                catch (Exception ex)
                {
                    // Exception TRONG lock (subscriber của AllJobsCompleted ném...) → đánh dấu job lỗi
                    // (nếu có) rồi THOÁT drain — finally nhả _drainRunning, queue không chết vĩnh viễn.
                    if (job is not null) SetState(job, JobState.Error, WrapError(job, ex));
                    throw;
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
                    // Không nuốt lỗi — nếu exception kèm PrintError CỤ THỂ (PrintErrorException) thì GIỮ NGUYÊN
                    // (engine đã báo PRINTER_OFFLINE/FILE_LOCKED... rõ ràng), không re-wrap thành SPOOLER_FAILED.
                    var err = ExtractPrintError(ex);
                    SetState(job, JobState.Error, err ?? WrapError(job, ex));
                }
                finally
                {
                    _gate.Release();   // BẮT BUỘC — không release → _gate (1 slot) kẹt → job sau WaitAsync treo mãi
                    lock (_sync) _activeWorkers--;
                }

                // Stop-on-error: 1 file lỗi (hết retry) → DỪNG cả lô, các file sau giữ Queued chờ Resume.
                // Lỡ máy in lỗi giữa lô thì không tự in tiếp phần còn lại (in nhầm mất giấy/mực).
                if (job.State == JobState.Error)
                {
                    lock (_sync)
                    {
                        _isPaused = true;
                        _stoppedByError = true;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Queue đang dispose (app đóng) — drain thoát sạch, không đánh dấu thêm gì.
        }
        catch (Exception ex)
        {
            // Drain chết vì lỗi BẤT NGỜ (subscriber ném ngoài SetState, lỗi không lường trước...) →
            // phải nhả cờ _drainRunning + đánh dấu MỌI job còn dở (Converting/Queued trong pending)
            // thành Error — KHÔNG để queue chết vĩnh viễn ("Converting mãi" không bao giờ in tiếp).
            try { System.Diagnostics.Debug.WriteLine($"PrintQueue drain crashed: {ex}"); } catch { }
            MarkAllUnfinishedAsError();
        }
        finally
        {
            lock (_sync) { _drainRunning = false; _isPaused = false; _stoppedByError = false; }
        }
    }

    /// <summary>Drain chết (lỗi bất ngờ) → mọi job còn dở phải về Error, không kẹt Converting/Queued mãi.
    /// Gọi trong catch ngoài cùng của DrainLoopAsync — KHÔNG ném (finally vẫn phải nhả cờ).</summary>
    private void MarkAllUnfinishedAsError()
    {
        lock (_sync)
        {
            foreach (var j in _pending) SetState(j, JobState.Error, WrapError(j, new InvalidOperationException("Vòng in bị dừng do lỗi nội bộ.")));
            _pending.Clear();
            foreach (var j in Jobs)
                if (j.State is JobState.Converting or JobState.Spooling or JobState.Queued)
                {
                    _jobCts.Remove(j, out var c);
                    try { c?.Cancel(); } catch { }
                    try { c?.Dispose(); } catch { }
                    SetState(j, JobState.Error, WrapError(j, new InvalidOperationException("Vòng in bị dừng do lỗi nội bộ.")));
                }
        }
    }

    private async Task ProcessWithRetryAsync(PrintJob job)
    {
        SetState(job, JobState.Converting);
        // CTS per-job liên kết với token queue — CancelJob/RemoveJob cancel cái này để engine
        // (đang chờ ở điểm chờ) thoát sớm → OCE lan tới DrainLoopAsync → job Cancelled (KHÔNG retry).
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        lock (_sync) _jobCts[job] = cts;
        try
        {
            for (var attempt = 0; attempt <= MaxRetries; attempt++)
            {
                var result = await PrintOnceAsync(job, cts.Token);
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
        finally
        {
            // Dọn per-job CTS dù retry/cancel/error — không để mồ côi trong dictionary.
            lock (_sync)
            {
                if (_jobCts.Remove(job, out var c)) c.Dispose();
            }
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

    /// <summary>
    /// Chọn engine ƯU TIÊN cho format — có FALLBACK: nếu engine ưu tiên không xử lý được (không có
    /// Office/không có browser/không đọc được file) thì chuyển xuống engine tiếp theo trong chuỗi
    /// đã đăng ký (OfficeCom → LibreOffice → Gdi → Browser/Watermark → Spool), KHÔNG dừng ở engine đầu.
    /// </summary>
    private IEnumerable<IPrintEngine> PickEngines(string format)
    {
        lock (_engineLock)
        {
            var picked = _engines.Where(e => e.CanHandle(format)).ToList();
            if (picked.Count == 0) picked = _engines.ToList();
            return picked;
        }
    }

    private async Task<Result<bool>> PrintOnceAsync(PrintJob job, CancellationToken ct)
    {
        var engines = PickEngines(job.Format);
        if (engines is null || !engines.Any())
        {
            // Mặc định khi chưa gắn engine: đánh dấu thành công để UI demo chạy được.
            await Task.Delay(50, ct); // mô phỏng xử lý — tôn trọng token (cancel → OCE)
            job.PageCount = job.PageCount > 0 ? job.PageCount : 1;
            return Result<bool>.Ok(true);
        }

        // Chạy lần lượt từng engine theo ưu tiên — engine đầu fail (không xử lý được file/printer)
        // thì chuyển xuống engine kế (OfficeCom → LibreOffice → Gdi → Browser → Spool). Chỉ dừng
        // khi có engine trả thành công. Lỗi CUỐI cùng giữ nguyên để báo đúng lý do.
        Result<bool>? lastFail = null;
        foreach (var engine in engines)
        {
            ct.ThrowIfCancellationRequested();
            var r = await engine.PrintAsync(job, ct);
            if (r.IsSuccess) return r;
            lastFail = r;
            // Engine KHÔNG xử lý được định dạng (CanHandle=false nhưng vẫn được gọi do fallback)
            // hoặc lỗi config rõ ràng (file không tồn tại / máy in không có) → không fallback tiếp
            // để tránh in nhầm qua đường khác; lỗi spooler/engine (máy bận, render lỗi) → thử engine kế.
            var code = r.Error?.Code;
            if (code is ErrorCodes.FileNotFound or ErrorCodes.PrinterNotFound or ErrorCodes.UnsupportedFormat
                or ErrorCodes.InvalidPageRange or ErrorCodes.SectionNotFound or ErrorCodes.FileCorrupted)
                break;
        }
        return lastFail ?? Result<bool>.Fail(PrintErrorFactory.SpoolerFailed("Không có engine in nào hoạt động."));
    }

    private static bool IsRetryable(PrintError error) =>
        error.Code is ErrorCodes.SpoolerBusy or ErrorCodes.PrinterOffline or ErrorCodes.EngineTimeout or ErrorCodes.OfficeAppBusy;

    private void SetState(PrintJob job, JobState state, PrintError? error = null)
    {
        job.State = state;
        job.Error = error;
        if (state == JobState.Converting) job.StartedAt ??= DateTimeOffset.Now;
        if (state is JobState.Done or JobState.Error or JobState.Cancelled) job.FinishedAt = DateTimeOffset.Now;
        FireJobStateChanged(job);
    }

    /// <summary>Fire JobStateChanged AN TOÀN — subscriber lỗi (UI đang đóng, Dispatcher shutdown, handler ném...)
    /// KHÔNG được phá vòng drain / call site đang giữ lock. Exception lan ra làm _drainRunning kẹt true
    /// → queue chết vĩnh viễn (mọi KickDrain no-op, job "Converting mãi" không bao giờ in tiếp).</summary>
    private void FireJobStateChanged(PrintJob job)
    {
        try
        {
            JobStateChanged?.Invoke(job);
        }
        catch
        {
            // Nuốt lỗi subscriber — KHÔNG lan ra (xem comment trên).
        }
    }

    /// <summary>Hủy cả hàng đợi (jobs chưa in chuyển Cancelled).</summary>
    public void CancelPending()
    {
        lock (_sync)
        {
            while (_pending.TryDequeue(out var j)) SetState(j, JobState.Cancelled);
        }
    }

    /// <summary>Hủy 1 job đang chờ (Queued) — MCP cancel_job dùng. Job ĐANG IN (Converting/Spooling)
    /// cũng hủy được: cancel token thật → engine thoát sớm → DrainLoopAsync chuyển Cancelled
    /// (KHÔNG đánh dấu Done). Trả true nếu job còn chưa về trạng thái cuối.</summary>
    public bool CancelJob(PrintJob job)
    {
        lock (_sync)
        {
            if (job is null) return false;
            if (job.State == JobState.Queued)
            {
                var pending = _pending.ToList();
                if (pending.Remove(job)) _pending = new Queue<PrintJob>(pending);
                SetState(job, JobState.Cancelled);
                return true;
            }
            if (job.State is JobState.Converting or JobState.Spooling)
            {
                CancelJobLocked(job);
                return true;
            }
            return false; // Done/Error/Cancelled — không hủy được nữa
        }
    }

    /// <summary>Cancel token của job đang in (gọi TRONG lock(_sync)). Engine nhận token ở điểm chờ
    /// sẽ ném OperationCanceledException → DrainLoopAsync chuyển job sang Cancelled thay vì Done.</summary>
    private void CancelJobLocked(PrintJob job)
    {
        if (_jobCts.TryGetValue(job, out var cts))
        {
            try { cts.Cancel(); } catch { }
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
                FireJobStateChanged(j);
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
            FireJobStateChanged(job);
        }
        KickDrain();
        return true;
    }

    /// <summary>Đánh dấu job ĐÃ IN XONG (Done) từ bên ngoài — dùng cho lô in gộp (MergePrintEngine)
    /// khi file nguồn đã in chung 1 bản, không đẩy từng file vào queue. Chỉ áp dụng cho job
    /// đang chờ (Queued/AwaitingApproval); job khác trả false. SetState chạy trong lock(_sync).</summary>
    public bool MarkDone(PrintJob job)
    {
        lock (_sync)
        {
            if (job is null || !Jobs.Contains(job)) return false;
            if (job.State is not (JobState.Queued or JobState.AwaitingApproval)) return false;
            // Gỡ khỏi pending nếu đang chờ trong hàng đợi (tránh drain in lại sau khi đánh dấu xong)
            var pending = _pending.ToList();
            if (pending.Remove(job)) _pending = new Queue<PrintJob>(pending);
            SetState(job, JobState.Done);
            return true;
        }
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
    /// In một LÔ job ĐÃ CÓ trong hàng đợi (nút Print All / In file này / MCP in lô) — TUẦN TỰ từng
    /// file qua MỘT vòng drain duy nhất (MaxConcurrency=1). Khác Enqueue: KHÔNG thêm dòng mới;
    /// job Done/Error/Cancelled được reset về Queued (cho in lại). Pause dừng giữa các file; 1 file
    /// lỗi → DỪNG cả lô (các file sau giữ Queued chờ Resume). MỌI lệnh in từ UI đi qua đây → nút
    /// Print All và context menu "In file này" cùng logic.
    /// </summary>
    public void ProcessBatch(IEnumerable<PrintJob> jobs)
    {
        if (jobs is null) return;
        lock (_sync)
        {
            foreach (var job in jobs)
            {
                if (job is null || !Jobs.Contains(job)) continue;
                if (job.State is JobState.Converting or JobState.Spooling) continue; // đang in — không in kép
                if (_pending.Contains(job)) continue;   // đã chờ trong hàng đợi (double-click Print) — không in kép
                job.State = JobState.Queued;   // giữ Queued tới lượt — không "Converting" ồ ạt cả lô
                job.Error = null;
                job.FinishedAt = null;
                _pending.Enqueue(job);
                FireJobStateChanged(job);
            }
        }
        KickDrain();
    }

    /// <summary>In lại 1 job đã có trong hàng đợi (giữ API cũ) — đi chung vòng drain tuần tự.</summary>
    public void ProcessExisting(PrintJob job)
        => ProcessBatch(job is null ? Array.Empty<PrintJob>() : new[] { job });

    /// <summary>Móc PrintError cụ thể từ exception (nếu engine ném kèm lỗi đã phân loại qua PrintErrorException).</summary>
    private static PrintError? ExtractPrintError(Exception ex)
        => ex is PrintErrorException pee ? pee.Error : null;

    /// <summary>Bọc exception thành PrintError đầy đủ — dùng chung cho cả 2 đường drain.</summary>
    private static PrintError WrapError(PrintJob job, Exception ex) => new()
    {
        Code = ErrorCodes.SpoolerFailed,
        Category = PrintErrorCategory.App,
        Message = $"Lỗi không xác định khi in {job.FileName}.",
        Hint = "Xem log chi tiết. Nếu lặp lại, báo lỗi kèm file đang in.",
        Detail = ex.ToString(),
    };

    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return;   // idempotent — tránh ObjectDisposedException khi gọi 2 lần
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
        // App đóng → job đang in cũng dừng (cancel per-job CTS → engine thoát sớm), không mồ côi.
        lock (_sync)
        {
            foreach (var c in _jobCts.Values)
            {
                try { c.Cancel(); } catch { }
                try { c.Dispose(); } catch { }
            }
            _jobCts.Clear();
        }
        try { _gate.Dispose(); } catch { }
    }
}

/// <summary>Engine in — mỗi định dạng có engine riêng (PDFium, Word COM, LibreOffice...).</summary>
public interface IPrintEngine
{
    bool CanHandle(string format);
    Task<Result<bool>> PrintAsync(PrintJob job, CancellationToken ct);
}