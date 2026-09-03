using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Printonator.Core;
using Printonator.Core.Models;
using Printonator.Spool.Printing;
using Printonator.UI.Localization;

namespace Printonator.UI;

/// <summary>
/// Chế độ printing server: 1 app → 1 thư mục DUY NHẤT. File mới thả vào folder → TỰ IN NGAY
/// vào máy in mặc định Windows (không cần mở UI, không cần bấm nút). Dùng 1 FileSystemWatcher duy nhất
/// (Created + Changed, debounce 2s — gom các file cùng lúc do copy nhiều file/Office tạo file khóa tạm).
/// File hợp lệ (đuôi in được + không trùng path trong queue) → tạo PrintJob cấu hình as-document
/// với PrinterName = "mặc định" (sentinel → engine dùng máy in mặc định Windows) rồi Enqueue LUÔN (tự in).
/// Mọi thao tác sửa queue và UI đều dispatch lên Dispatcher (FileSystemWatcher chạy thread riêng).
/// Config 1 folder duy nhất lưu qua WatchConfig dạng JSON %APPDATA%\Printonator\watch.json.
/// </summary>
public sealed class WatchFolderService : IDisposable
{
    /// <summary>Đuôi file in được — nguồn whitelist DUY NHẤT cho watcher (khớp MainWindow.SupportedExtensions).</summary>
    public static readonly HashSet<string> SupportedExtensions = new(
        [".pdf", ".docx", ".doc", ".rtf", ".xlsx", ".xls", ".xlsm", ".csv",
         ".pptx", ".ppt", ".ppsx", ".png", ".jpg", ".jpeg", ".tiff", ".bmp",
         ".gif", ".webp", ".txt"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan DebounceInterval = TimeSpan.FromSeconds(2);

    private readonly PrintQueue _queue;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationToken _lifecycleCt;

    /// <summary>Khóa đồng bộ cho toàn bộ trạng thái entry + retry (1 entry duy nhất → 1 lock là đủ).</summary>
    private readonly object _sync = new();

    /// <summary>Entry duy nhất đang theo dõi (null = chưa watch folder nào).</summary>
    private WatchEntry? _watchEntry;

    /// <summary>Folder đang watch (đường dẫn đầy đủ) — field riêng để lúc _watchEntry chưa tạo vẫn an toàn.</summary>
    private string? _currentFolder;

    /// <summary>Timer retry khi không mở watcher được ngay (folder chưa tồn tại/mất quyền) — AutoReset 10s, tối đa 3 lần.</summary>
    private System.Timers.Timer? _retryTimer;
    private int _retryCount;
    private string? _retryFolder;

    /// <summary>Đường dẫn cấu hình watch: %APPDATA%\Printonator\watch.json</summary>
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Printonator", "watch.json");

    /// <summary>Đang theo dõi 1 folder duy nhất có watcher THẬT ĐANG SỐNG hay không (khác IsConfigured).</summary>
    public bool IsWatching => _watchEntry is not null && _watchEntry.Watcher is not null;

    /// <summary>Folder đã được CHỌN hay chưa (kể cả khi watcher đang lỗi / chưa mở được) — dùng lưu config Enabled.</summary>
    public bool IsConfigured => _watchEntry is not null;

    /// <summary>Folder đã chọn nhưng watcher KHÔNG mở được (mất quyền/chưa tồn tại, retry cạn) — UI báo vàng.</summary>
    public bool WatcherFailed => _watchEntry is not null && _watchEntry.Watcher is null;

    /// <summary>Folder đang theo dõi (đường dẫn đầy đủ), null khi chưa watch.</summary>
    public string? Folder => _watchEntry is not null ? _currentFolder : null;

    /// <summary>Thông báo khi có file mới được thêm từ thư mục theo dõi (toast-log) — MainWindow gọi ShowToast.</summary>
    public Action<string>? Toast { get; set; }

    /// <summary>Entry đang theo dõi: watcher + timer debounce (gom file rồi xử lý 1 lần). KHÔNG còn AutoPrint — luôn tự in.</summary>
    private sealed class WatchEntry
    {
        public FileSystemWatcher? Watcher;
        public System.Timers.Timer? Debounce;
        public bool Disposed;                  // chặn event/timer cũ xử lý tiếp sau khi StopWatch
        public readonly List<string> Pending = new();
    }

    public WatchFolderService(PrintQueue queue, Dispatcher dispatcher, CancellationToken lifecycleCt)
    {
        _queue = queue;
        _dispatcher = dispatcher;
        _lifecycleCt = lifecycleCt;
    }

    /// <summary>Bắt đầu theo dõi 1 folder duy nhất (printing server). Đang watch folder KHÁC → dừng cái cũ trước.</summary>
    public void StartWatch(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || _lifecycleCt.IsCancellationRequested) return;
        string full;
        try { full = Path.GetFullPath(folderPath); }
        catch { return; }   // path không hợp lệ (ký tự đặc biệt/thiếu định dạng) — bỏ qua

        lock (_sync)
        {
            // Đang watch folder KHÁC → dừng cái cũ trước (1 app 1 folder).
            if (_watchEntry is not null && _currentFolder is not null &&
                !full.Equals(_currentFolder, StringComparison.OrdinalIgnoreCase))
            {
                StopWatch();
            }

            // Đang watch CÙNG folder → giữ nguyên (idempotent). Riêng trường hợp watcher đang lỗi
            // (retry đã cạn — Watcher null) → reset retry và thử mở lại ngay lần này; mở được thì dừng retry.
            if (_watchEntry is not null)
            {
                if (_watchEntry.Watcher is null)
                {
                    _retryCount = 0;
                    if (TryStartWatcher(full)) StopRetryTimer();
                    else ScheduleRetry(full);
                }
                return;
            }

            _retryCount = 0;
            _currentFolder = full;
            _watchEntry = new WatchEntry();
            if (!TryStartWatcher(full)) ScheduleRetry(full);
        }
    }

    /// <summary>Dừng theo dõi: đóng entry, xóa _watchEntry, hủy retry timer.</summary>
    public void StopWatch()
    {
        StopRetryTimer();

        WatchEntry? entry;
        lock (_sync)
        {
            entry = _watchEntry;
            _watchEntry = null;
            _currentFolder = null;
        }
        if (entry is not null) CloseEntry(entry);
    }

    /// <summary>
    /// Thử mở FileSystemWatcher cho folder. Thành công → true. Lỗi (folder chưa tồn tại/mất quyền đọc) →
    /// false — folder có thể mới được cắm ổ cứng/tạo sau khi app chạy, nên người gọi sẽ retry.
    /// </summary>
    private bool TryStartWatcher(string full)
    {
        try
        {
            var entry = _watchEntry;
            if (entry is null || entry.Disposed) return false;

            var watcher = new FileSystemWatcher(full)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            watcher.Created += (_, e) => OnFileEvent(entry, e.FullPath);
            watcher.Changed += (_, e) => OnFileEvent(entry, e.FullPath);
            watcher.Error += (_, e) => { };   // buffer tràn / mất quyền — giữ watcher sống, không crash app
            entry.Watcher = watcher;
            return true;
        }
        catch
        {
            if (_watchEntry is not null)
            {
                try { _watchEntry.Watcher?.Dispose(); } catch { }
                _watchEntry.Watcher = null;
            }
            return false;
        }
    }

    /// <summary>Lên lịch retry mở watcher: System.Timers.Timer 10s AutoReset, tối đa 3 lần.</summary>
    private void ScheduleRetry(string full)
    {
        if (_lifecycleCt.IsCancellationRequested) { StopRetryTimer(); return; }
        if (_retryCount >= 3) { StopRetryTimer(); return; }   // hết số lần thử — bỏ hẳn, không spam
        _retryCount++;
        _retryFolder = full;

        StopRetryTimer();
        _retryTimer = new System.Timers.Timer(10_000) { AutoReset = true };
        _retryTimer.Elapsed += (_, _) =>
        {
            if (_lifecycleCt.IsCancellationRequested) { StopRetryTimer(); return; }
            lock (_sync)
            {
                if (_watchEntry is null || _retryFolder is null) { StopRetryTimer(); return; }
                if (!TryStartWatcher(_retryFolder)) ScheduleRetry(_retryFolder);
                else StopRetryTimer();
            }
        };
        _retryTimer.Start();
    }

    /// <summary>Cấu hình watch (1 folder + cờ bật/tắt) — format JSON mới cho chế độ printing server.</summary>
    public sealed record WatchConfig
    {
        public string? Folder { get; init; }
        public bool Enabled { get; init; }
    }

    /// <summary>Lưu cấu hình watch ra JSON WriteIndented {Folder, Enabled}. config null → ghi Enabled:false.
    /// File hỏng/mất quyền → im lặng (không đáng làm hỏng shutdown).</summary>
    public static void SaveConfig(string filePath, WatchConfig? config)
    {
        try
        {
            config ??= new WatchConfig { Folder = null, Enabled = false };
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* disk full / file bị khóa — watch config không quan trọng bằng job queue, bỏ qua im lặng */ }
    }

    /// <summary>Đọc cấu hình watch từ path cụ thể (test dùng). File mất/hỏng → null.</summary>
    public static WatchConfig? LoadConfig(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return null;
            return JsonSerializer.Deserialize<WatchConfig>(File.ReadAllText(filePath));
        }
        catch { return null; }
    }

    /// <summary>Đọc cấu hình watch từ %APPDATA%\Printonator\watch.json. File mất/hỏng → null.</summary>
    public static WatchConfig? LoadConfig() => LoadConfig(FilePath);

    /// <summary>
    /// Migrate file watch.json CŨ (Dictionary "folder" → bool autoPrint, format v0.1.x) sang WatchConfig mới.
    /// File chưa tồn tại → không làm gì. Đã format mới (Folder không rỗng) → giữ nguyên.
    /// Còn format cũ → lấy folder ĐẦU TIÊN đang bật → SaveConfig Enabled:true. Không đọc được/rỗng → đổi tên .old.
    /// </summary>
    public static void MigrateIfNeeded()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return;

            var raw = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(raw)) { RenameToOld(path); return; }

            // Phân biệt format mới theo SỰ HIỆN DIỆN của key "Folder" (kể cả giá trị null/empty = trạng thái
            // "dừng theo dõi" hợp lệ — không tạo .old thừa). Format CŨ (Dictionary folder→autoPrint) parse
            // thành WatchConfig thành công nhưng Folder null và KHÔNG có key "Folder" → không nhầm, rớt xuống dưới.
            bool hasFolderKey;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                hasFolderKey = doc.RootElement.TryGetProperty("Folder", out _);
            }
            catch { hasFolderKey = false; }
            if (hasFolderKey) return;   // đã format mới — giữ nguyên file

            // Đọc theo format cũ Dictionary<string,bool> ("folder" → autoPrint).
            Dictionary<string, bool>? old;
            try { old = JsonSerializer.Deserialize<Dictionary<string, bool>>(raw); }
            catch { old = null; }

            if (old is null || old.Count == 0) { RenameToOld(path); return; }
            // Ưu tiên folder có autoPrint==true (đang bật); nếu không có → lấy folder đầu tiên.
            var enabled = old.FirstOrDefault(kv => kv.Value);
            var first = string.IsNullOrEmpty(enabled.Key) ? old.First().Key : enabled.Key;
            SaveConfig(path, new WatchConfig { Folder = first, Enabled = true });
        }
        catch { /* file khóa/hỏng nặng — không migration, app vẫn chạy bình thường */ }
    }

    private static void RenameToOld(string path)
    {
        try
        {
            if (File.Exists(path)) File.Move(path, path + ".old", overwrite: true);
        }
        catch { }
    }

    /// <summary>File mới → hẹn debounce 2s (gom nhiều file gieo cùng lúc thành 1 lần xử lý).</summary>
    private void OnFileEvent(WatchEntry entry, string path)
    {
        lock (entry.Pending)
        {
            if (entry.Disposed) return;
            var name = Path.GetFileName(path);
            // Bỏ file khóa tạm của Office (~$file.docx) — đuôi "~$..." không nằm whitelist nên thêm dự phòng
            if (name.StartsWith("~$", StringComparison.Ordinal)) return;
            if (!SupportedExtensions.Contains(Path.GetExtension(path))) return;
            entry.Pending.Add(path);

            if (entry.Debounce is not null)
            {
                entry.Debounce.Stop();
                entry.Debounce.Start();
                return;
            }

            entry.Debounce = new System.Timers.Timer(DebounceInterval.TotalMilliseconds) { AutoReset = false };
            entry.Debounce.Elapsed += (_, _) =>
            {
                List<string> batch;
                lock (entry.Pending)
                {
                    if (entry.Disposed) return;
                    batch = entry.Pending.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    entry.Pending.Clear();
                }
                if (batch.Count == 0) return;
                ProcessBatch(entry, batch);
            };
            entry.Debounce.Start();
        }
    }

    /// <summary>Các file mới đã gom xong: lọc trùng queue, tạo job (as-document, máy in mặc định), LUÔN Enqueue.</summary>
    private void ProcessBatch(WatchEntry entry, List<string> paths)
    {
        if (_lifecycleCt.IsCancellationRequested) return;

        // Timer debounce chạy THREAD RIÊNG — mọi thao tác queue.Jobs (ObservableCollection) và
        // toast phải nằm trên UI thread → dispatch. Nếu window/animation đang shutdown → bỏ cả lô.
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
            if (_lifecycleCt.IsCancellationRequested) return;

            var added = 0;
            foreach (var p in paths)
            {
                try
                {
                    // File bị xóa ngay sau khi gieo (copy 2 bước) hoặc đường dẫn không đọc được → bỏ qua file đó.
                    if (!File.Exists(p)) continue;

                    // Edge-case file đang copy: sau debounce 2s mà mtime còn TRONG vòng 1s gần đây → file
                    // có thể vẫn đang bị ghi dở, coi là CHƯA ỔN ĐỊNH → bỏ qua file này lần này. User copy
                    // tiếp sẽ sinh Changed event mới → vào lô sau (đơn giản + an toàn, không chặn UI).
                    if (File.GetLastWriteTimeUtc(p) >= DateTime.UtcNow - TimeSpan.FromSeconds(1))
                        continue;

                    // Không trùng đường dẫn (không phân biệt hoa thường) với job đang có — tránh row kép.
                    if (_queue.Jobs.Any(j => j.FilePath.Equals(p, StringComparison.OrdinalIgnoreCase))) continue;

                    var fmt = Path.GetExtension(p).TrimStart('.').ToUpperInvariant();
                    if (fmt.Length == 0) continue;
                    var job = new PrintJob
                    {
                        FilePath = p,
                        FileName = Path.GetFileName(p),
                        Format = fmt,
                        Config = new PrintConfig
                        {
                            // "mặc định" = sentinel → engine dùng máy in mặc định Windows
                            // (OfficeCom: null → ActivePrinter; Spool: GetDefaultPrinterName).
                            PrinterName = "mặc định",
                        },
                        Source = JobSource.WatchFolder,
                        HasPerFilePrinter = true,   // không bị ApplySelectedPrinter ép máy toolbar
                    };
                    // Nếu máy in mặc định Windows là máy ảo (PDF/XPS/OneNote...), in ra nó sẽ xuất
                    // file PDF ngay trong folder watch → watcher kích hoạt → tự in → LOOP VÔ HẠN.
                    // Chặn triệt để: KHÔNG auto-in file nào (kể cả docx) khi default printer là ảo.
                    // User phải chọn máy in giấy thủ công rồi bấm in.
                    if (IsDefaultPrinterVirtual())
                    {
                        _queue.AddOnly(job);
                    }
                    else if (fmt == "PDF")
                        _queue.AddOnly(job);   // PDF: giữ lại chờ user chọn máy in
                    else
                        _queue.Enqueue(job);   // auto-in bình thường
                    added++;
                }
                catch { /* 1 file lỗi cá biệt (đang khóa/không đọc được) — không làm hỏng cả lô */ }
            }

            if (added > 0)
                Toast?.Invoke(L10n.F(Keys.Watch.FileAdded, added));
        }));
    }

    private void StopRetryTimer()
    {
        var t = _retryTimer;
        _retryTimer = null;
        _retryFolder = null;
        if (t is not null)
        {
            try { t.Stop(); t.Dispose(); } catch { }
        }
    }

    /// <summary>Máy in mặc định Windows có phải máy ảo (PDF/XPS/OneNote...) không?
    /// Nếu phải → auto-in qua watch folder sẽ xuất PDF ngay trong folder watch → LOOP. Watch phải dừng auto-in.</summary>
    private static bool IsDefaultPrinterVirtual()
        => PrinterService.IsDefaultPrinterVirtual();

    private static void CloseEntry(WatchEntry entry)
    {
        lock (entry.Pending)
        {
            if (entry.Disposed) return;
            entry.Disposed = true;
            if (entry.Debounce is not null)
            {
                try { entry.Debounce.Stop(); entry.Debounce.Dispose(); } catch { }
                entry.Debounce = null;
            }
            if (entry.Watcher is not null)
            {
                try { entry.Watcher.EnableRaisingEvents = false; entry.Watcher.Dispose(); } catch { }
                entry.Watcher = null;
            }
            entry.Pending.Clear();
        }
    }

    public void Dispose()
    {
        StopRetryTimer();
        WatchEntry? entry;
        lock (_sync)
        {
            entry = _watchEntry;
            _watchEntry = null;
            _currentFolder = null;
        }
        if (entry is not null) CloseEntry(entry);
    }
}
