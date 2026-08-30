using System.IO;
using System.Windows.Threading;
using Printonator.Core;
using Printonator.Core.Models;
using Printonator.UI.Localization;

namespace Printonator.UI;

/// <summary>
/// Theo dõi các THƯ MỤC chờ file MỚI được thả vào rồi tự động thêm vào hàng đợi in (T2.6).
/// Mỗi thư mục một FileSystemWatcher (Created + Changed, debounce 2s — gom các file cùng lúc
/// do copy nhiều file/Office tạo file khóa tạm). File hợp lệ (đuôi in được + không trùng path
/// trong queue) → tạo PrintJob cấu hình mặc định; autoPrint=true → Enqueue (tự in), false → AddOnly.
/// Mọi thao tác sửa queue và UI đều dispatch lên Dispatcher (FileSystemWatcher chạy thread riêng).
/// Config (folder ↔ autoPrint) lưu qua SaveWatches/LoadWatches dạng JSON %APPDATA%\Printonator\watch.json.
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
    private readonly Dictionary<string, WatchEntry> _watches = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Đường dẫn cấu hình watch: %APPDATA%\Printonator\watch.json</summary>
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Printonator", "watch.json");

    /// <summary>Thông báo khi có file mới được thêm từ thư mục theo dõi (toast-log) — MainWindow gọi ShowToast.</summary>
    public Action<string>? Toast { get; set; }

    /// <summary>Một thư mục đang theo dõi: watcher + cờ auto-print + timer debounce (gom file rồi xử lý 1 lần).</summary>
    private sealed class WatchEntry
    {
        public FileSystemWatcher? Watcher;
        public bool AutoPrint;
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

    /// <summary>Bắt đầu theo dõi một thư mục. Đã theo dõi rồi → cập nhật lại cờ autoPrint.</summary>
    public void StartWatch(string folderPath, bool autoPrint)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || _lifecycleCt.IsCancellationRequested) return;
        string full;
        try { full = Path.GetFullPath(folderPath); }
        catch { return; }   // path không hợp lệ (ký tự đặc biệt/thiếu định dạng) — bỏ qua, không làm hỏng các thư mục khác

        lock (_watches)
        {
            if (_watches.TryGetValue(full, out var existing))
            {
                existing.AutoPrint = autoPrint;   // đổi cờ qua cửa sổ theo dõi — watcher giữ nguyên
                return;
            }

            var entry = new WatchEntry { AutoPrint = autoPrint };
            try
            {
                var watcher = new FileSystemWatcher(full)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true,
                };
                watcher.Created += (_, e) => OnFileEvent(entry, e.FullPath);
                watcher.Changed += (_, e) => OnFileEvent(entry, e.FullPath);
                watcher.Error += (_, e) => { };   // buffer tràn / mất quyền — giữ watcher sống, không crash app
                entry.Watcher = watcher;
            }
            catch
            {
                // Thư mục không tồn tại / mất quyền đọc → không watcher; folder vẫn xuất hiện trong
                // danh sách cửa sổ theo dõi (để user thấy trạng thái) nhưng không bắt được file.
                entry.Watcher = null;
            }
            _watches[full] = entry;
        }
    }

    /// <summary>Dừng theo dõi một thư mục.</summary>
    public void StopWatch(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        string full;
        try { full = Path.GetFullPath(folderPath); }
        catch { return; }

        lock (_watches)
        {
            if (!_watches.TryGetValue(full, out var entry)) return;
            CloseEntry(entry);
            _watches.Remove(full);
        }
    }

    /// <summary>Trạng thái hiện tại: folder (đường dẫn đầy đủ) → autoPrint. Cửa sổ theo dõi dùng để hiển thị + lưu.</summary>
    public Dictionary<string, bool> Snapshot()
    {
        lock (_watches)
            return _watches.ToDictionary(kv => kv.Key, kv => kv.Value.AutoPrint, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Lưu cấu hình watch (folder → autoPrint) ra JSON. File hỏng/mất quyền → im lặng (không đáng làm hỏng shutdown).</summary>
    public static void SaveWatches(string filePath, IEnumerable<KeyValuePair<string, bool>> watches)
    {
        try
        {
            var dict = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (watches is not null)
                foreach (var kv in watches) dict[kv.Key] = kv.Value;

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, System.Text.Json.JsonSerializer.Serialize(dict,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* disk full / file bị khóa — watch config không quan trọng bằng job queue, bỏ qua im lặng */ }
    }

    /// <summary>Đọc cấu hình watch từ %APPDATA%\Printonator\watch.json. File mất/hỏng → { }.</summary>
    public static Dictionary<string, bool> LoadWatches() => LoadWatches(FilePath);

    /// <summary>Đọc cấu hình watch từ path cụ thể (test dùng). File hỏng → { }.</summary>
    public static Dictionary<string, bool> LoadWatches(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var raw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(filePath));
            return raw is null
                ? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, bool>(raw, StringComparer.OrdinalIgnoreCase);
        }
        catch { return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase); }
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

    /// <summary>Các file mới đã gom xong: lọc trùng queue, tạo job, đẩy vào hàng đợi (dispatch UI thread).</summary>
    private void ProcessBatch(WatchEntry entry, List<string> paths)
    {
        if (_lifecycleCt.IsCancellationRequested) return;
        var autoPrint = entry.AutoPrint;   // đọc 1 lần — đổi cờ giữa chừng không làm lệch cả lô

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
                    // Không trùng đường dẫn (không phân biệt hoa thường) với job đang có — tránh row kép.
                    if (_queue.Jobs.Any(j => j.FilePath.Equals(p, StringComparison.OrdinalIgnoreCase))) continue;

                    var fmt = Path.GetExtension(p).TrimStart('.').ToUpperInvariant();
                    if (fmt.Length == 0) continue;
                    var job = new PrintJob
                    {
                        FilePath = p,
                        FileName = Path.GetFileName(p),
                        Format = fmt,
                        Config = new PrintConfig(),
                    };
                    if (autoPrint) _queue.Enqueue(job);
                    else _queue.AddOnly(job);
                    added++;
                }
                catch { /* 1 file lỗi cá biệt (đang khóa/không đọc được) — không làm hỏng cả lô */ }
            }

            if (added > 0)
                Toast?.Invoke(L10n.F(Keys.Watch.FileAdded, added));
        }));
    }

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
        lock (_watches)
        {
            foreach (var entry in _watches.Values) CloseEntry(entry);
            _watches.Clear();
        }
    }
}