using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Text.Json;
using Printonator.Core;
using Printonator.Core.Models;
using Printonator.Spool.Printing;

namespace Printonator.UI;

public partial class MainWindow : Window
{
    private readonly PrintQueue _queue = new();
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private string? _sortColumn;
    private bool _sortDescending;
    private int _printerScanGeneration;   // scan cũ về sau không được ghi đè kết quả scan mới

    /// <summary>Danh sách thông báo hiển thị trong bell. Badge = số thông báo chưa đọc.</summary>
    public ObservableCollection<AppNotification> Notifications { get; } = new();

    /// <summary>Cấu hình mặc định cho FILE MỚI (thay thế bảng Paper setup cũ) — áp khi thêm file.</summary>
    private readonly PrintConfig _defaultConfig = new();

    public ObservableCollection<PrintJob> Jobs => _queue.Jobs;

    /// <summary>Máy in đang chọn (PrinterInfo đầy đủ trạng thái/khả năng) — combo bind TwoWay.</summary>
    public PrinterInfo? SelectedPrinter { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        BellBadgeBorder.Visibility = Visibility.Collapsed;
        Notifications.CollectionChanged += (_, _) => UpdateNotificationBadge();
        JobList.SelectionChanged += OnSelectionChanged;
        _queue.JobStateChanged += OnJobStateChanged;
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            FadeToast(0);
        };
        PrinterCombo.SelectionChanged += (_, _) => UpdatePrinterDot();
        Closed += (_, _) =>
        {
            _queue.Dispose();
            CleanupOrphanPrintProcesses();   // đóng app là dọn triệt để — mọi engine để lại gì thì gom lại
        };
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste, PasteFromClipboard_Executed));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Delete, DeleteSelection_Executed));

        // Nhóm hiển thị theo thư mục chứa file: main row = folder cha, sub rows = file con
        var grouped = CollectionViewSource.GetDefaultView(Jobs);
        grouped.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PrintJob.FolderGroup)));

        Loaded += (_, _) =>
        {
            // Khớp cửa sổ vào vùng làm việc (trừ taskbar) — tránh mở lệch/che taskbar/no thể kéo-đóng
            // nhất là trên màn hình nhỏ (vd 1366x768) khi cửa sổ 1320x860 lớn hơn work-area.
            var work = System.Windows.SystemParameters.WorkArea;
            if (Height > work.Height) Height = work.Height;
            if (Width > work.Width) Width = work.Width;

            // Đảm bảo vị trí nằm TRONG vùng làm việc (phòng khi CenterScreen đặt ra ngoài do multi-monitor
            // / DPI). Nếu lệch, ép lại giữa vùng làm việc.
            if (Left < work.Left || Left + Width > work.Left + work.Width ||
                Top < work.Top || Top + Height > work.Top + work.Height)
            {
                Left = work.Left + (work.Width - Width) / 2;
                Top = work.Top + (work.Height - Height) / 2;
            }
        };
        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        // Engine ưu tiên (dynamic theo máy user — KHÔNG bundle lib, app nhẹ):
        // 1) MS Office COM → 2) LibreOffice (soffice nếu máy có) → 3) Browser render (Edge/Chrome headless — PDF/ảnh/TXT đúng page range/scale/khổ giấy) → 4) shell printto fallback
        _queue.RegisterEngine(new OfficeComPrintEngine());
        _queue.RegisterEngine(new LibreOfficePrintEngine());
        _queue.RegisterEngine(new BrowserPrintEngine());
        _queue.RegisterEngine(new SpoolPrintEngine());
        LoadPrinters();
        _queue.MaxRetries = 2;
        _ = CheckForUpdatesSilentAsync();   // kiểm tra bản mới nền khi app mở — thông báo vào bell nếu có
        await Task.CompletedTask;
    }

    /// <summary>Chạy update check nền ngay khi mở app (không làm phiền nếu không có bản mới).</summary>
    private async Task CheckForUpdatesSilentAsync()
    {
        await Task.Run(async () =>
        {
            try
            {
                var info = await new UpdateChecker(CurrentVersion()).CheckAsync(CancellationToken.None);
                if (info is null) return; // không có bản mới — im lặng
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    AddNotification(NotificationKind.Update, $"Có bản mới {info.Version}",
                        info.Name, act: () => PromptAndInstallUpdate(info));
                }));
            }
            catch { /* lỗi mạng — im lặng, không làm hỏng app */ }
        });
    }

    /// <summary>Phiên bản app hiện tại (đọc assembly).</summary>
    private static Version CurrentVersion()
        => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);

    private void LoadPrinters()
    {
        // Chạy QUÉT MÁY IN NGOÀI UI thread — máy in MẠNG/LAN bị firewall (vd Avast) chặn có thể
        // làm GetPrintQueues/GetPrintCapabilities treo; chạy nền + timeout để UI không đứng hình.
        // MỌI kết cục (thành công / rỗng / timeout / lỗi) đều dispatch ApplyPrinterList — không bao
        // giờ để dropdown rỗng câm (lỗi cũ: catch rỗng → không banner, không retry, rỗng vĩnh viễn).
        var gen = ++_printerScanGeneration;
        _ = Task.Run(async () =>
        {
            try
            {
                var r = await Task.Run(() => new PrinterService().ListPrinters())
                                   .WaitAsync(TimeSpan.FromSeconds(20));   // backstop cho GetPrintQueues() tự treo
                DispatchApplyPrinterList(gen, r);
            }
            catch (TimeoutException)
            {
                DispatchApplyPrinterList(gen, null, MakePrinterScanError(null));
            }
            catch (Exception ex)
            {
                DispatchApplyPrinterList(gen, null, MakePrinterScanError(ex));
            }
        });
    }

    /// <summary>Dispatch kết quả scan lên UI thread (nếu scan còn mới — scan cũ không ghi đè).
    /// Rỗng = lỗi → banner + nút Retry, không để im.</summary>
    private void DispatchApplyPrinterList(int gen, Result<List<PrinterInfo>>? r, PrintError? err = null)
    {
        if (gen != _printerScanGeneration) return;                    // scan cũ bỏ qua — không ghi đè
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (r is not null && r.Value is { Count: > 0 })
                    ApplyPrinterList(r.Value, null);
                else
                    ApplyPrinterList(null, err ?? r?.Error ?? MakePrinterScanError(null));
            }));
        }
        catch { /* window đóng giữa chừng — bỏ qua */ }
    }

    private static PrintError MakePrinterScanError(Exception? ex) => new()
    {
        Code = ErrorCodes.SpoolerFailed,
        Category = PrintErrorCategory.Printer,
        Message = "Không quét được danh sách máy in (máy in mạng có thể bị firewall chặn).",
        Hint = "Bấm Retry connection để thử lại.",
        Detail = ex?.Message ?? "",
    };

    /// <summary>Áp danh sách máy in lên combo (trên UI thread).
    /// Quan trọng: nếu scan thất bại / trả RỖNG (vd máy in mạng bị firewall chặn) →
    /// GIỮ danh sách máy in đã có sẵn, KHÔNG làm trống dropdown (như trước).</summary>
    private void ApplyPrinterList(List<PrinterInfo>? printers, PrintError? err)
    {
        if (printers is null || printers.Count == 0)
        {
            // Không scan được → giữ nguyên danh sách combo hiện tại; nếu có lỗi cụ thể thì báo nhẹ.
            if (err is not null && PrinterCombo.Items.Count == 0)
                ShowBanner(err.Code, err.Message, err.Hint);
            return;
        }
        PrinterCombo.ItemsSource = printers;

        // Máy in bị treo / bị firewall chặn → vẫn hiện (mục "Không phản hồi…") + báo vàng để user biết
        // tại sao list thiếu máy, và có nút Retry (không để im như trước).
        var unresponsive = printers.Count(p =>
            !p.IsAvailable && p.StatusDetail?.StartsWith("Không phản hồi", StringComparison.Ordinal) == true);
        if (unresponsive > 0)
            ShowBanner(ErrorCodes.SpoolerFailed,
                $"{unresponsive} máy in không phản hồi (có thể bị firewall chặn).",
                "Bấm Retry connection để quét lại.");

        SelectedPrinter = printers.FirstOrDefault(p => p.IsDefault && p.IsAvailable)
                          ?? printers.FirstOrDefault(p => p.IsDefault)
                          ?? printers.FirstOrDefault(p => p.IsAvailable)
                          ?? printers.FirstOrDefault();
        if (SelectedPrinter is not null) PrinterCombo.SelectedItem = SelectedPrinter;
        UpdatePrinterDot();
        ShowPrinterReminder();
    }

    // Nhắc khi khởi động: hiện "Kiểm tra máy in ✓" cạnh hộp chọn, tự ẩn sau 6s.
    private void ShowPrinterReminder()
    {
        if (PrinterReminder is null) return;
        PrinterReminder.Visibility = System.Windows.Visibility.Visible;
        PrinterReminder.Opacity = 1;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            PrinterReminder.BeginAnimation(System.Windows.Controls.Control.OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(600)));
            PrinterReminder.Visibility = System.Windows.Visibility.Collapsed;
        };
        t.Start();
    }

    private void UpdatePrinterDot()
    {
        if (PrinterStatusDot is null) return; // XAML chưa dựng xong

        var p = SelectedPrinter;
        if (p is null)
        {
            PrinterStatusDot.Fill = Brushes.Red;
            PrinterStatusDot.ToolTip = "Chưa có máy in khả dụng";
            return;
        }
        PrinterStatusDot.Fill = p.IsAvailable ? Brushes.Green : Brushes.Red;
        PrinterStatusDot.ToolTip = p.StatusDetail is null
            ? $"{p.Name} — sẵn sàng"
            : $"{p.Name} — {p.StatusDetail}";
    }

    /// <summary>Phím Delete (phím tắt, không có nút UI) — xóa các file đang chọn khỏi hàng đợi.</summary>
    private void DeleteSelection_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var targets = JobList.SelectedItems.OfType<PrintJob>().ToList();
        if (targets.Count == 0) return;
        foreach (var job in targets)
            _queue.RemoveJob(job);
        UpdateFooter();
        ShowToast($"Đã xóa {targets.Count} file khỏi hàng đợi.");
    }

    // ===== Select all (header cột checkbox) + popup trang in trên cột Pages =====

    /// <summary>Click checkbox chọn-tất-cả trên header: chưa chọn hết → chọn hết; đã chọn hết → bỏ chọn hết.
    /// (Dùng Click thay vì Checked/Unchecked để click lúc "indeterminate" luôn = CHỌN HẾT, đúng trực giác.)</summary>
    private void SelectAllChk_Click(object sender, RoutedEventArgs e)
    {
        var total = JobList.Items.Count;
        if (total == 0) { SyncSelectAllState(); return; }
        if (JobList.SelectedItems.Count >= total) JobList.UnselectAll();
        else JobList.SelectAll();
        SyncSelectAllState();
    }

    /// <summary>Đồng bộ checkbox header: ✓ khi chọn hết, ■ (indeterminate) khi chọn một phần, trống khi không chọn.</summary>
    private void SyncSelectAllState()
    {
        if (SelectAllChk is null) return; // XAML chưa dựng xong
        var total = JobList.Items.Count;
        var sel = JobList.SelectedItems.Count;
        SelectAllChk.IsChecked = total > 0 && sel >= total ? true : sel > 0 ? null : (bool?)false;
    }

    /// <summary>Bấm vào cell Pages → mở popup dưới chính cell đó, nạp trạng thái hiện tại của file.</summary>
    private void PagesCell_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement cell || cell.DataContext is not PrintJob job) return;
        if (cell.FindName("PagesPopup") is not Popup pop) return;

        var isAll = string.IsNullOrWhiteSpace(job.Config.PageRange)
                 || job.Config.PageRange.Equals("All", StringComparison.OrdinalIgnoreCase);
        if (cell.FindName("PRangeAll") is RadioButton allChk) allChk.IsChecked = isAll;
        if (cell.FindName("PRangeCustom") is RadioButton customChk) customChk.IsChecked = !isAll;
        if (cell.FindName("PRangeBox") is TextBox box)
            box.Text = isAll ? "" : job.Config.PageRange.Trim();

        pop.IsOpen = true;
    }

    /// <summary>Radio All/Khoảng-trang đổi → bật/tắt ô nhập tương ứng.</summary>
    private void PRangeMode_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.FindName("PRangeBox") is not TextBox box) return;
        var customActive = rb.Name == "PRangeCustom" && rb.IsChecked == true;
        box.IsEnabled = customActive;
        if (customActive) box.Focus();
    }

    /// <summary>Áp dụng lựa chọn trong popup: row nằm trong selection → áp cho CẢ nhóm (chuẩn Windows), không → chỉ row đó.</summary>
    private void PagesApply_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.FindName("PagesPopup") is not Popup pop) return;
        if (pop.PlacementTarget is not FrameworkElement cell || cell.DataContext is not PrintJob job) return;

        var isAll = fe.FindName("PRangeAll") is RadioButton a && a.IsChecked == true;
        // Rỗng coi như All (khớp NormalizeRange trong PrintSettingsWindow)
        var range = isAll ? "All" : ((fe.FindName("PRangeBox") as TextBox)?.Text?.Trim() ?? "");
        if (range.Length == 0) range = "All";

        var targets = JobList.SelectedItems.Contains(job)
            ? JobList.SelectedItems.OfType<PrintJob>().ToList()
            : new List<PrintJob> { job };
        foreach (var j in targets)
            j.Config.PageRange = range;

        pop.IsOpen = false;
        JobList.Items.Refresh();
        ShowToast($"Áp trang \"{(range == "All" ? "tất cả" : range)}\" cho {targets.Count} file.");
    }

    // ===== Add files (button + drag-drop) =====
    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        // Filter dựng TỪ SupportedExtensions (nguồn whitelist duy nhất) — không hardcode để tránh
        // lệch danh sách đuôi (trước đây thiếu .rtf/.xlsm/.csv/.ppt/.ppsx/.jpeg/.bmp/.gif/.webp)
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = "Print files|" + string.Join(";", SupportedExtensions.Select(ext => "*" + ext)) + "|All files|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        // Lọc lại sau dialog (đề phòng path nhập tay / chọn "All files"): file không hỗ trợ → đếm skipped + toast
        var supported = dlg.FileNames.Where(IsSupported).ToList();
        var skipped = dlg.FileNames.Length - supported.Count;
        if (supported.Count > 0)
            AddFiles(supported, skipped > 0
                ? $"Đã thêm {supported.Count} file, bỏ qua {skipped} file không hỗ trợ định dạng."
                : null);
        else if (skipped > 0)
            ShowToast($"Đã bỏ qua {skipped} file không hỗ trợ định dạng (chỉ nhận PDF, Office, ảnh, TXT).");
    }

    // ===== Copy-paste từ Explorer (Ctrl+V) — file HOẶC folder (tự quét toàn bộ + thư mục con) =====
    private void PasteFromClipboard_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        try
        {
            var toAdd = new List<string>();
            if (Clipboard.ContainsFileDropList())
                toAdd.AddRange(Clipboard.GetFileDropList().Cast<string>());
            else if (Clipboard.ContainsText())
            {
                foreach (var line in Clipboard.GetText().Split('\n'))
                {
                    var p = line.Trim().Trim('\r', '"', ' ');
                    if (p.Length > 0) toAdd.Add(p);
                }
            }
            if (toAdd.Count == 0) return;
            AddPaths(toAdd);
        }
        catch (Exception ex)
        {
            ShowBanner(ErrorCodes.FileNotFound, "Không dán được file/folder từ clipboard.", ex.Message);
        }
    }

    /// <summary>
    /// Nguồn ingest DÙNG CHUNG (paste + drag&amp;drop): file → thêm thẳng nếu đuôi hỗ trợ; folder → quét đệ quy.
    /// Trả về (added, skippedUnsupported, skippedMissing, folderCount). File không hỗ trợ định dạng HOẶC đường
    /// dẫn không tồn tại → đếm vào skipped để toast "bỏ qua N file" — không bỏ qua im lặng (feedback trung thực).
    /// </summary>
    private (int Added, int SkippedUnsupported, int SkippedMissing, int FolderCount) AddPaths(IEnumerable<string> paths)
    {
        var toAdd = new List<string>();
        var unsupported = 0;
        var missing = 0;
        var folderCount = 0;
        foreach (var raw in paths)
        {
            var p = raw.Trim().Trim('\r', '"', ' ');
            if (p.Length == 0) continue;
            if (Directory.Exists(p))
            {
                var files = CollectFolderRecursive(p);
                if (files.Count > 0) { toAdd.AddRange(files); folderCount++; }
            }
            else if (File.Exists(p))
            {
                if (IsSupported(p)) toAdd.Add(p);
                else unsupported++;
            }
            else missing++; // path dán vào không tồn tại / đã hết hạn — đếm để báo trung thực, không im lặng
        }
        if (toAdd.Count > 0)
        {
            var baseText = folderCount > 0
                ? $"Đã thêm {toAdd.Count} file từ {folderCount} thư mục (gồm cả thư mục con)"
                : $"Đã thêm {toAdd.Count} file vào hàng đợi";
            AddFiles(toAdd, baseText + SkipSummary(unsupported, missing));
        }
        else if (unsupported > 0 || missing > 0)
        {
            ShowToast($"Đã bỏ qua {SkippedList(unsupported, missing)}.");
        }
        return (toAdd.Count, unsupported, missing, folderCount);
    }

    /// <summary>Hậu tố toast: ", bỏ qua X..." / "." khi không có gì bị bỏ qua.</summary>
    private static string SkipSummary(int unsupported, int missing)
    {
        if (unsupported <= 0 && missing <= 0) return ".";
        return $", bỏ qua {SkippedList(unsupported, missing)}.";
    }

    /// <summary>Ghép cụm "N file không hỗ trợ định dạng và M đường dẫn không tồn tại".</summary>
    private static string SkippedList(int unsupported, int missing)
    {
        var parts = new List<string>();
        if (unsupported > 0) parts.Add($"{unsupported} file không hỗ trợ định dạng");
        if (missing > 0) parts.Add($"{missing} đường dẫn không tồn tại");
        return string.Join(" và ", parts);
    }

    // ===== Drag & drop từ Explorer: kéo file/folder thả vào cửa sổ =====
    private void Root_DragEnter(object sender, DragEventArgs e)
    {
        if (!IsFileDrop(e)) return;
        DropHighlight.Visibility = Visibility.Visible;
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (IsFileDrop(e))
        {
            e.Effects = DragDropEffects.Copy;   // chỉ kéo-thả ngoài app; không có drag source trong app
            e.Handled = true;
        }
        else e.Effects = DragDropEffects.None;
    }

    private void Root_DragLeave(object sender, DragEventArgs e)
    {
        // Root là drop-target DUY NHẤT trong cửa sổ (SearchBox đã tắt AllowDrop) nên
        // DragLeave chỉ bắn khi chuột thực sự rời cửa sổ — không cần counter chống flicker.
        if (!IsFileDrop(e)) return;
        DropHighlight.Visibility = Visibility.Collapsed;
    }

    private void Root_Drop(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;
        if (!IsFileDrop(e)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            AddPaths(files);
        e.Handled = true;
    }

    private static bool IsFileDrop(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

    /// <summary>Đuôi file in được — nguồn DUY NHẤT cho whitelist (paste, drag&drop, quét thư mục).</summary>
    private static readonly HashSet<string> SupportedExtensions = new(
        [".pdf", ".docx", ".doc", ".rtf", ".xlsx", ".xls", ".xlsm", ".csv",
         ".pptx", ".ppt", ".ppsx", ".png", ".jpg", ".jpeg", ".tiff", ".bmp",
         ".gif", ".webp", ".txt"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>File có định dạng in được? (Theo ĐUÔI — không cần file tồn tại.)</summary>
    private static bool IsSupported(string path) => SupportedExtensions.Contains(Path.GetExtension(path));

    /// <summary>Quét thư mục ĐỆ QUY, lấy mọi file định dạng in được; bỏ file ẩn/hệ thống/tạm (~$Ôoffice).</summary>
    private static List<string> CollectFolderRecursive(string root)
    {
        var result = new List<string>();
        var opt = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.Temporary,
        };
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", opt))
            {
                try
                {
                    var name = Path.GetFileName(f);
                    if (name.StartsWith("~$", StringComparison.Ordinal)) continue; // file khóa tạm của Office
                    if (IsSupported(f)) result.Add(f);
                }
                catch { /* 1 file lỗi (đang khóa/không đọc được) — bỏ qua FILE đó, KHÔNG làm hỏng cả thư mục */ }
            }
        }
        catch { /* thư mục gốc không đọc được — bỏ qua cả folder (hiếm) */ }
        return result;
    }

    // ===== Print settings (bảng cấu hình đầy đủ — thay PaperSetupDialog cũ) =====
    /// <summary>Nút "Print settings" trên toolbar: có selection → áp cho selection; không → đặt mặc định file mới.</summary>
    private void PrintSettings_Click(object sender, RoutedEventArgs e)
        => OpenPrintSettings(JobList.SelectedItems.OfType<PrintJob>().ToList());

    /// <summary>Nút "Cấu hình in…" trên BulkBar: chỉ mở khi có selection.</summary>
    private void BulkSettings_Click(object sender, RoutedEventArgs e)
        => OpenPrintSettings(JobList.SelectedItems.OfType<PrintJob>().ToList());

    /// <summary>Context menu "Cấu hình in (Item settings)…".</summary>
    private void CtxItemSettings_Click(object sender, RoutedEventArgs e)
        => OpenPrintSettings(GetTargetJobs(sender));

    /// <summary>Bấm trực tiếp ô "Settings" của 1 dòng → mở cấu hình in của ĐÚNG file đó.</summary>
    private void SettingsCell_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PrintJob job)
            OpenPrintSettings(new List<PrintJob> { job });
    }

    /// <summary>
    /// Mở bảng cấu hình in đầy đủ (PrintSettingsWindow).
    /// Có target → áp cho các file đó; không → đặt config MẶC ĐỊNH cho file mới.
    /// </summary>
    private void OpenPrintSettings(IReadOnlyList<PrintJob> targets)
    {
        var dlg = new PrintSettingsWindow(targets, targets.Count > 0 ? targets[0].Config : _defaultConfig, SelectedPrinter)
        {
            Owner = this,
        };
        if (dlg.ShowDialog() != true) return;

        if (targets.Count > 0)
        {
            foreach (var job in targets)
                dlg.Result.CopyInto(job.Config);
            JobList.Items.Refresh();
            ShowToast($"Đã áp cấu hình cho {targets.Count} file.");
        }
        else
        {
            dlg.Result.CopyInto(_defaultConfig);
            ShowToast("Đã đặt cấu hình mặc định cho file mới. (Số bản/khổ giấy chỉ áp khi thêm file.)");
        }
    }

    /// <summary>Khổ giấy mặc định theo loại file (Penpot gap): bản vẽ A3, văn bản/hóa đơn A4/A5, ảnh A4.</summary>
    private static string DefaultPaperFor(string format) => format switch
    {
        "DWG" or "DXF" or "PLT" or "DWT" => "A3",
        "TXT" or "CSV" => "A5", // hóa đơn/biên nhận dạng ngắn
        _ => "A4",
    };

    /// <summary>Cấu hình mặc định cho file mới (base từ _defaultConfig, áp khổ giấy theo loại file).</summary>
    private PrintConfig DefaultConfigFor(string paper, string range = "All", int copies = 1)
    {
        var cfg = _defaultConfig.Clone();
        cfg.PaperSize = paper;
        cfg.PageRange = range;
        cfg.Copies = copies;
        // KHÔNG gán cfg.Duplex (=false): bool ghi đè làm mất DuplexMode=AsPrinter ("theo máy in")
        // mà user đã chọn trong _defaultConfig — CopyInto(_defaultConfig) đã giữ nguyên enum (Major #2 fix).
        cfg.PrinterName ??= SelectedPrinter?.Name;
        return cfg;
    }

    private void AddFiles(IEnumerable<string> paths, string? toast = null)
    {
        var added = 0;
        foreach (var p in paths)
        {
            try
            {
                if (!File.Exists(p)) { ShowBanner(ErrorCodes.FileNotFound, $"Không tìm thấy file: {p}", ""); continue; }
                var fmt = Path.GetExtension(p).TrimStart('.').ToUpperInvariant();
                _queue.AddOnly(new PrintJob
                {
                    FilePath = p,
                    FileName = Path.GetFileName(p),
                    Format = fmt,
                    Config = DefaultConfigFor(DefaultPaperFor(fmt)),
                });   // thêm nhưng không tự in — user bấm Print mới in
                added++;
            }
            catch (Exception ex)
            {
                ShowBanner(ErrorCodes.FileCorrupted, $"Không thêm được file: {Path.GetFileName(p)}", ex.Message);
            }
        }
        UpdateFooter();
        if (added > 0) ShowToast(toast ?? $"Đã thêm {added} file vào hàng đợi.");
    }

    // ===== Multi-select helpers =====
    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var count = JobList.SelectedItems.Count;
        BulkCountText.Text = $"{count} files selected";
        BulkBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SyncSelectAllState();
        UpdateFooter(); // in-ngữ-cảnh: nút Print (N) cập nhật theo selection
        if (count > 0)
        {
            FooterHint.Text = $"Gợi ý: Ctrl+Click chọn rời · Shift+Click chọn dải · Ctrl+A chọn hết · Delete = xóa · Chuột phải = menu thao tác";
            var first = JobList.SelectedItems.OfType<PrintJob>().FirstOrDefault();
            BulkSummaryText.Text = first is null
                ? ""
                : $"Cấu hình file đầu tiên: {first.Config.SummaryText}";
        }
    }

    // ===== Context menu =====
    private PrintJob? ContextJob(object sender)
    {
        if (sender is FrameworkElement { DataContext: PrintJob job }) return job;
        if (sender is MenuItem { DataContext: PrintJob j }) return j;
        return null;
    }

    /// <summary>
    /// Danh sách job mà menu chuột phải nên tác động.
    /// Chuẩn Windows: nếu file được click nằm trong vùng đã chọn → áp dụng cho TOÀN BỘ selection;
    /// nếu không (click phải vào file chưa chọn) → chỉ file đó.
    /// </summary>
    private List<PrintJob> GetTargetJobs(object sender)
    {
        var clicked = ContextJob(sender);
        if (clicked is null) return new List<PrintJob>();

        var selected = JobList.SelectedItems.OfType<PrintJob>().ToList();
        if (selected.Contains(clicked) && selected.Count > 0)
            return selected;   // click phải vào file trong nhóm → cả nhóm
        return new List<PrintJob> { clicked }; // click phải file lẻ → chỉ file đó
    }

    // Cập nhật tiêu đề menu theo số file đang chọn (ContextMenu.Opened)
    private void RowContextMenu_Opening(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.ContextMenu is not ContextMenu menu) return;
        var n = GetTargetJobs(fe).Count;

        if (menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "CtxOpen") is { } open)
            open.Header = n > 1 ? $"Mở {n} files" : "Mở file";
        if (menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "CtxEdit") is { } edit)
            edit.Header = n > 1 ? $"Mở & sửa {n} files (tự nạp bản mới)" : "Mở & sửa file";
        if (menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "CtxPrint") is { } print)
            print.Header = n > 1 ? $"In {n} files" : "In file này";
        if (menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "CtxItemSettings") is { } itemSettings)
            itemSettings.Header = n > 1 ? $"Cấu hình in cho {n} files…" : "Cấu hình in (Item settings)…";
        if (menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "CtxRemove") is { } remove)
            remove.Header = n > 1 ? $"Xóa {n} files khỏi hàng đợi" : "Xóa khỏi hàng đợi";
    }

    private void CtxOpen_Click(object sender, RoutedEventArgs e)
    {
        foreach (var job in GetTargetJobs(sender))
            OpenFileCommand.Execute(job);
    }

    private void CtxEdit_Click(object sender, RoutedEventArgs e)
    {
        foreach (var job in GetTargetJobs(sender))
            OpenFileCommand.Execute(job); // mở app gốc; watcher tự nạp bản mới
    }

    private void CtxPrint_Click(object sender, RoutedEventArgs e)
    {
        var targets = GetTargetJobs(sender);
        if (targets.Count == 0) return;
        // In nhanh qua context menu — KHÔNG xác nhận in-lại/pre-flight (thao tác trực tiếp),
        // nhưng dùng CHUNG đường thực thi với nút Print (tránh DRY: sửa chỗ nọ quên chỗ kia).
        ShowToast($"Đưa {targets.Count} file vào hàng đợi in.");
        ApplySelectedPrinter(targets);
        StartPrintBatch(targets.ToList(), $"in {targets.Count} file này");
    }

    private void CtxPageRange_Click(object sender, RoutedEventArgs e)
    {
        var targets = GetTargetJobs(sender);
        if (targets.Count == 0) return;
        // Nếu có DOCX section → dialog dùng file đầu tiên có section; cấu hình áp cả nhóm
        var first = targets.FirstOrDefault(j => j.Sections.Count > 0) ?? targets[0];
        var dlg = new PageRangeDialog(first) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            foreach (var job in targets)
                job.Config.PageRange = dlg.PageRange;
            JobList.Items.Refresh();
            ShowToast($"Áp page range \"{dlg.PageRange}\" cho {targets.Count} file.");
        }
    }

    private void CtxRemove_Click(object sender, RoutedEventArgs e)
    {
        var targets = GetTargetJobs(sender);
        foreach (var job in targets)
            _queue.RemoveJob(job);   // lock an toàn, tránh race
        UpdateFooter();
    }

    // Nút print duy nhất (ngữ cảch): có chọn → in các file Selected đang Queued; không chọn → in tất cả Queued.
    private void PrintMain_Click(object sender, RoutedEventArgs e)
    {
        var selected = JobList.SelectedItems.OfType<PrintJob>()
            .Where(j => j.State == JobState.Queued).ToList();
        if (selected.Count > 0)
        {
            PrintJobs(selected, $"in {selected.Count} file đã chọn");
            return;
        }

        // Không chọn → in tất cả job ĐÁNG IN (Queued + Done/Error/Cancelled — để print all có thể
        // in lại file đã in khi user đồng ý qua confirm). PrintJobs sẽ hỏi nếu có file Done.
        var ready = Jobs.Where(j => j.State is JobState.Queued or JobState.Done or JobState.Error or JobState.Cancelled).ToList();
        if (ready.Count == 0)
        {
            ShowBanner(ErrorCodes.NoFilesSelected, "Không có file nào ở trạng thái chờ in.", "Thêm file hoặc chọn file đã in để in lại.");
            return;
        }
        PrintJobs(ready, $"in tất cả ({ready.Count} file)");
    }

    private void PrintJobs(List<PrintJob> jobs, string action)
    {
        var ready = jobs.Where(j => j.State is JobState.Queued or JobState.Done or JobState.Error or JobState.Cancelled).ToList();
        if (ready.Count == 0) { ShowBanner(ErrorCodes.NoFilesSelected, "Không có file nào ở trạng thái chờ in.", ""); return; }

        // ===== Xác nhận IN LẠI file đã in trước đó =====
        // Nếu user KHÔNG xóa file đã in (Done) khỏi hàng đợi, bấm in tiếp sẽ gộp luôn các file Done
        // đó — nghĩa là in lại chúng (tốn giấy/mực). Phải cho user quyết: in lại hết / bỏ qua file đã in.
        var alreadyPrinted = ready.Where(j => j.State == JobState.Done).ToList();
        if (alreadyPrinted.Count > 0)
        {
            var ask = MessageBox.Show(
                $"Có {alreadyPrinted.Count} file đã in xong trước đó trong hàng đợi.\n\n" +
                "Chọn Có = in lại TẤT CẢ (kể cả file đã in).\n" +
                "Chọn Không = chỉ in các file chưa in (bỏ qua file đã in).\n" +
                "Chọn Hủy = không in gì cả.",
                "Printonator — In lại file đã in?", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (ask == MessageBoxResult.Cancel) return;
            if (ask == MessageBoxResult.No)
                ready = ready.Where(j => j.State != JobState.Done).ToList();
            // Yes → giữ nguyên ready (in lại tất cả kể cả Done)
            if (ready.Count == 0)
            {
                ShowBanner(ErrorCodes.NoFilesSelected, "Không còn file nào để in (đã bỏ qua file đã in).", "");
                return;
            }
        }

        // Gắn máy in ĐANG CHỌN trên thanh công cụ cho MỌI job — máy in là lựa chọn toàn cục của batch.
        // (Trước đây dùng ??= chỉ gắn cho job CHƯA có máy → job giữ máy cũ ghi lúc thêm file,
        //  nên đổi combo sang LBP vẫn in vào "Microsoft Print to PDF".)
        ApplySelectedPrinter(ready);

        // ===== Pre-flight gate (chỉ khi lô lớn): ước tính tờ → vượt ngưỡng thì xác nhận =====
        // Lô nhỏ (dưới ngưỡng) in thẳng 1 click — không làm chậm việc thường. Lô lớn: cho người
        // xem "bao nhiêu tờ + máy in nào (sẽ áp cho mọi file)" trước khi tốn giấy/mực thật.
        const int ConfirmSheetThreshold = 100;
        var sheets = PrintConfirmWindow.EstimateSheets(ready);
        if (sheets > ConfirmSheetThreshold
            && !PrintConfirmWindow.Show(this, SelectedPrinter?.Name ?? "mặc định", ready, sheets))
        {
            return; // người dùng hủy — KHÔNG in, không toast "bắt đầu"
        }

        StartPrintBatch(ready, action);
    }

    /// <summary>Đường thực thi CHUNG cho mọi lệnh in (nút Print, In file này…) — ĐÚNG 1 nơi việc
    /// đẩy job, refresh, và fire completion. Trước đây tách rời ở từng nút → sửa chỗ này quên chỗ
    /// kia (vd completion chỉ chạy cho nút Print, không cho context menu).</summary>
    private void StartPrintBatch(List<PrintJob> ready, string action)
    {
        if (ready.Count == 0) { ShowBanner(ErrorCodes.NoFilesSelected, "Không có file nào ở trạng thái chờ in.", ""); return; }

        // ProcessExisting: in job đã có, KHÔNG thêm dòng mới
        foreach (var j in ready)
            _queue.ProcessExisting(j);
        ShowToast($"Bắt đầu {action}...");
        JobList.Items.Refresh();
        UpdateFooter();

        // Batch-done: chờ MỌI job trong lô về trạng thái cuối (Done/Error/Cancelled) rồi fire
        // OnAllCompleted MỘT lần. Không dùng timeout — chờ tự nhiên tới khi job xong.
        _ = WaitBatchDoneAsync(ready);
    }

    /// <summary>Chờ toàn bộ job trong lô về trạng thái cuối, rồi fire completion 1 lần.
    /// Không dùng timeout — chờ tự nhiên đến khi mọi job trong lô về trạng thái cuối
    /// (Done/Error/Cancelled); in xong là khi nào job xong, không chặt 30s.</summary>
    private async Task WaitBatchDoneAsync(List<PrintJob> batch)
    {
        var terminal = new[] { JobState.Done, JobState.Error, JobState.Cancelled };
        var toWait = new HashSet<PrintJob>(batch);
        try
        {
            while (toWait.Count > 0)
            {
                var pending = toWait.Where(j => !terminal.Contains(j.State)).ToList();
                if (pending.Count == 0) break;

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
        catch (Exception) { /* dù lỗi vẫn cố báo completion */ }

        // Truyền số file THẬT ĐÃ in xong (đếm từ batch đã chờ), không đếm lại từ Jobs.
        var doneCount = batch.Count(j => j.State == JobState.Done);
        try { await Dispatcher.BeginInvoke(new Action(() => OnAllCompleted(doneCount))); }
        catch { }
    }

    /// <summary>Ép máy in đang chọn lên job — máy in thanh công cụ luôn thắng máy cũ đã ghi trong config.</summary>
    private void ApplySelectedPrinter(IEnumerable<PrintJob> jobs)
    {
        var printer = SelectedPrinter?.Name ?? "mặc định";
        foreach (var j in jobs)
            j.Config.PrinterName = printer;
    }

    private void RetryConnection_Click(object sender, RoutedEventArgs e)
    {
        LoadPrinters();
        HideBanner();
    }

    private void Printers_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new PrinterConfigWindow { Owner = this };
        dlg.ShowDialog();
        LoadPrinters(); // sau khi đóng — nạp lại trạng thái mới
    }

    /// <summary>Nút Info (footer góc phải) — mở cửa sổ về changelog, license, liên hệ.</summary>
    private void Info_Click(object sender, RoutedEventArgs e)
        => AboutWindow.Show(this);

    // Mở/đóng popover thông báo khi bấm bell; khi mở → đánh dấu tất cả ĐÃ ĐỌC (badge về 0).
    private void BellToggle_Changed(object sender, RoutedEventArgs e)
    {
        NotifPopup.IsOpen = BellToggle.IsChecked == true;
        if (BellToggle.IsChecked == true)
        {
            foreach (var n in Notifications) n.Read = true;
            UpdateNotificationBadge();   // badge = unread (0)
        }
    }

    private void OnJobStateChanged(PrintJob job)
    {
        Dispatcher.Invoke(() =>
        {
            if (job.State == JobState.Error && job.Error is not null)
                ShowBanner(job.Error.Code, job.Error.Message, job.Error.Hint);
            // Refresh dòng để binding trạng thái (✓ Done / màu lỗi) cập nhật — PrintJob không INPC
            JobList.Items.Refresh();
            UpdateFooter();
            // (Xóa file đã in khỏi hàng đợi do popup 'In xong' quyết — không tự xóa âm thầm nữa)
        });
    }

    private void OnAllCompleted(int done)
    {
        // Dùng BeginInvoke (bất đồng bộ, KHÔNG block) — completion fire từ threadpool/waiter.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // Thông báo vào danh sách bell (KHÔNG còn card đơn bị ghi đè — mỗi lô một item).
            AddNotification(NotificationKind.Done,
                $"Đã in xong {done} file",
                $"{DateTime.Now:HH:mm}");
            ShowToast($"Đã in xong tất cả ({done} file).");

            // Popup hoàn tất — user quyết có xóa file đã in không (không tự xóa âm thầm).
            var remove = PrintDoneWindow.Show(this, done, Jobs.ToList(), AppVersion);
            if (remove)
            {
                var removes = Jobs.Where(j => j.State == JobState.Done).ToList();
                foreach (var j in removes) _queue.RemoveJob(j);
                JobList.Items.Refresh();
            }

            UpdateFooter();
        }));
    }

    /// <summary>Thêm một thông báo vào danh sách bell; cập nhật badge = số chưa đọc.</summary>
    private void AddNotification(NotificationKind kind, string title, string detail, Action? act = null)
    {
        Notifications.Add(new AppNotification(kind, title, detail, act));
    }

    /// <summary>Badge bell = số thông báo chưa đọc; ẩn khi không có gì. Cũng sync trạng thái rỗng.</summary>
    private void UpdateNotificationBadge()
    {
        var unread = Notifications.Count(n => !n.Read);
        BellBadge.Text = unread.ToString();
        BellBadgeBorder.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotifEmptyText.Visibility = Notifications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Bấm 1 item thông báo: đánh dấu đọc + chạy hành động (vd download/install update).</summary>
    private void NotifItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is Guid id)
        {
            var n = Notifications.FirstOrDefault(x => x.Id == id);
            if (n is null) return;
            n.Read = true;
            UpdateNotificationBadge();
            n.Act?.Invoke();
        }
        e.Handled = true;
    }

    /// <summary>Nút "Kiểm tra bản mới" trong bell — chạy update check nền, thêm thông báo nếu có bản mới.</summary>
    private async void BellCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        BellBadgeBorder.Visibility = Visibility.Visible;
        BellBadge.Text = "…";
        var info = await new UpdateChecker(CurrentVersion()).CheckAsync(CancellationToken.None);
        if (info is null)
        {
            AddNotification(NotificationKind.Warning, "Bạn đang dùng bản mới nhất", AppVersion);
            return;
        }
        AddNotification(NotificationKind.Update, $"Có bản mới {info.Version}",
            info.Name, act: () => PromptAndInstallUpdate(info));
    }

    /// <summary>Xác nhận + tải + xác thực SHA-256 + chạy installer (GUI, không cài im lặng).</summary>
    private async void PromptAndInstallUpdate(UpdateInfo info)
    {
        var ask = MessageBox.Show(
            $"Có bản mới {info.Version}.\n\n{info.Notes}\n\nTải và cài?", "Printonator — Bản cập nhật",
            MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (ask != MessageBoxResult.Yes) return;

        var path = await info.DownloadAsync(CancellationToken.None);
        if (path is null) { MessageBox.Show("Không tải được bản cập nhật.", "Printonator", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (!await info.VerifySha256Async(path, info.InstallerSha256))
        {
            MessageBox.Show("Bản tải về không khớp checksum — đã hủy.", "Printonator", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!info.LaunchInstaller(path))
            MessageBox.Show("Không khởi động được trình cài đặt.", "Printonator", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string AppVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    // ===== Tick "Tự xóa file đã in" + lưu cài đặt UI =====
    /// <summary>
    /// Dọn process MỒ CÔI do các engine in để lại, khi đóng app (defensive — gom triệt để).
    /// Chỉ giết process KHÔNG có cửa sổ (windowless): Word/Excel/PowerPoint headless mồ côi mà COM
    /// hay printto để lại, và Chrome/Edge headless in (CDP) — KHÔNG bao giờ đụng trình duyệt/Office
    /// THẬT user đang mở (cái có cửa sổ, MainWindowTitle != ""). Đó là thứ làm máy nặng vài trăm
    /// process sau nhiều lô in.
    /// </summary>
    private static void CleanupOrphanPrintProcesses()
    {
        string[] officeFamilies = ["WINWORD", "EXCEL", "POWERPNT"];
        foreach (var name in officeFamilies)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                    if (string.IsNullOrEmpty(p.MainWindowTitle))   // headless mồ côi, không cửa sổ
                        KillProcess(p);
            }
            catch { }
        }

        // Dọn MỒ CÔI chỉ do app này tạo: headless Chrome/Edge/msedgewebview2 mà engine in spawn
        // (đăng ký PID vào BrowserPrintEngine.SpawnedBrowserPids). KHÔNG quét mù theo tên —
        // tránh giết tab/browser THẬT của user. Dọn đúng PID → máy user không bị nặng vì orphan.
        foreach (var pid in BrowserPrintEngine.SpawnedBrowserPids.ToList())
        {
            try { KillProcess(Process.GetProcessById(pid)); }
            catch { }
        }
        BrowserPrintEngine.SpawnedBrowserPids.Clear();

        static void KillProcess(Process p)
        {
            try { p.Kill(entireProcessTree: true); p.WaitForExit(3000); } catch { }
        }
    }

    private void UpdateFooter()
    {
        var total = Jobs.Count;
        var done = Jobs.Count(j => j.State == JobState.Done);
        var err = Jobs.Count(j => j.State == JobState.Error);
        FooterStats.Text = $"{total} files | {done} printed | {err} error";

        // Progress bar + taskbar progress (Penpot gap).
        // Đúng: chỉ đếm job đã THỰC SỰ nằm trong lô in này (bắt đầu rồi hoặc xong/ngừng), chứ
        // KHÔNG đếm toàn bộ Jobs (bao gồm cả job còn Queued chưa đem in lần này). Nếu đếm cả
        // job đang chờ, khi bấm in 1 file mà còn N file Queued khác → progress frozen sai (vd 50%).
        // Denom = số job đã vào lô: Converting/Spooling (đang chạy) + Done/Error/Cancelled (kết thúc).
        var inRun = Jobs.Count(j => j.State is not JobState.Queued and not JobState.AwaitingApproval);
        var percent = inRun > 0 ? (int)(done * 100.0 / inRun) : 0;
        FooterProgress.Value = Math.Clamp(percent, 0, 100);
        ProgressText.Text = $"{percent}%";
        TaskbarInfo.ProgressValue = percent / 100.0;
        TaskbarInfo.ProgressState =
            Jobs.Any(j => j.State is JobState.Converting or JobState.Spooling)
                ? System.Windows.Shell.TaskbarItemProgressState.Normal
                : (total > 0 ? System.Windows.Shell.TaskbarItemProgressState.None : System.Windows.Shell.TaskbarItemProgressState.None);

        // Nút print duy nhất — ngữ cảnh: có chọn → "Print (N)", không chọn → "Print all (N)" theo số Queued
        var queued = Jobs.Count(j => j.State == JobState.Queued);
        var selQueued = JobList.SelectedItems.OfType<PrintJob>().Count(j => j.State == JobState.Queued);
        PrintMainBtn.Content = selQueued > 0
            ? $"Print ({selQueued})"
            : (queued > 0 ? $"Print all ({queued})" : "Print all");

        // Checkbox chọn-tất-cả trên header sync theo selection hiện tại
        SyncSelectAllState();

        // Overlay hướng dẫn khi hàng đợi trống
        UpdateEmptyState();
    }

    /// <summary>Empty state (Kéo thả file...) hiện khi hàng đợi trống VÀ không đang tìm kiếm.</summary>
    private void UpdateEmptyState()
    {
        if (EmptyState is null) return; // XAML chưa dựng xong
        var empty = Jobs.Count == 0 && string.IsNullOrWhiteSpace(SearchBox?.Text);
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    // ===== Sort theo cột (user yêu cầu) =====
    private void Sort_Column(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is not TextBlock header || header.Tag is not string column) return;

            if (_sortColumn == column)
                _sortDescending = !_sortDescending;
            else
            {
                _sortColumn = column;
                _sortDescending = false;
            }

            var view = CollectionViewSource.GetDefaultView(Jobs);
            using (view.DeferRefresh())
            {
                view.SortDescriptions.Clear();
                foreach (var p in SortPaths(column))
                    view.SortDescriptions.Add(new SortDescription(p, _sortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending));
            }
            UpdateSortHeaderArrows();
        }
        catch (Exception ex)
        {
            // Lỗi sort không được làm crash app — fallback về thứ tự thêm vào
            ShowBanner(ErrorCodes.SpoolerFailed, "Lỗi khi sắp xếp danh sách.", ex.Message);
        }
    }

    private static IEnumerable<string> SortPaths(string column) => column switch
    {
        "Pages" => ["PageCount"],
        // Property PHẲNG trên PrintJob — ListCollectionView không resolve path lồng "Config.Copies"
        "Settings" => ["SortCopies", "SortPaper"],
        "Status" => ["State"],
        _ => ["FileName"],
    };

    private void UpdateSortHeaderArrows()
    {
        ResetHeader(SortName, "Name");
        ResetHeader(SortPages, "Pages to print");
        ResetHeader(SortSettings, "Settings");
        ResetHeader(SortStatus, "Status");

        var target = _sortColumn switch
        {
            "Pages" => SortPages,
            "Settings" => SortSettings,
            "Status" => SortStatus,
            _ => SortName,
        };
        if (target is not null)
            target.Text += _sortDescending ? " ▼" : " ▲";

        void ResetHeader(TextBlock? tb, string label)
        {
            if (tb is not null) tb.Text = label;
        }
    }

    // ===== Search header (Penpot gap: icon ⌕) =====
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text?.Trim() ?? "";
        var view = CollectionViewSource.GetDefaultView(Jobs);
        view.Filter = string.IsNullOrEmpty(q)
            ? null
            : o => o is PrintJob j && j.FileName.Contains(q, StringComparison.OrdinalIgnoreCase);
        UpdateEmptyState(); // tìm không ra → không hiện nhầm empty state "kéo thả"
    }

    // ===== Toast success (Penpot gap) =====
    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void FadeToast(double to)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(300));
        if (to <= 0)
            anim.Completed += (_, _) => Toast.Visibility = Visibility.Collapsed;
        Toast.BeginAnimation(OpacityProperty, anim);
    }

    /// <summary>Mã lỗi thuộc loại "warn/offline thật" → banner giữ nền vàng; các mã còn lại là lỗi thật → nền đỏ.</summary>
    private static readonly HashSet<string> WarningBannerCodes = new(
        [ErrorCodes.PrinterOffline, ErrorCodes.PrinterNotFound, ErrorCodes.PrinterNoPermission,
         ErrorCodes.SpoolerBusy, ErrorCodes.SpoolerFailed, ErrorCodes.EngineNotFound,
         ErrorCodes.EngineTimeout, ErrorCodes.OfficeAppBusy, ErrorCodes.NoFilesSelected],
        StringComparer.Ordinal);

    private void ShowBanner(string? code, string message, string detail)
    {
        ErrorBannerText.Text = detail.Length > 0
            ? $"{message}  ({detail})"
            : message;
        // Nút "Retry connection" CHỈ hiện với lỗi kết nối máy in/spooler — lỗi file/tham số không retry được
        RetryBtn.Visibility = IsRetryable(code) ? Visibility.Visible : Visibility.Collapsed;

        // "Nói thật" mức độ: cảnh báo máy in offline/bận + hướng dẫn → vàng (mặc định XAML);
        // lỗi thật (file/job/không mở được…) → đỏ, phân biệt ngay bằng màu không cần đọc chữ.
        if (code is null || WarningBannerCodes.Contains(code))
            ResetBannerToWarn();
        else
            SetBannerToError();

        ErrorBanner.Visibility = Visibility.Visible;
    }

    private void ResetBannerToWarn()
    {
        // Trả về đúng màu mặc định khai báo trong XAML (vàng) — quan trọng sau khi banner đã hiện đỏ
        if (TryFindResource("WarnBgBrush") is Brush bg) ErrorBanner.Background = bg;
        ErrorBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(0xEA, 0xB3, 0x08)); // #EAB308 (default XAML)
        ErrorBannerText.Foreground = new SolidColorBrush(Color.FromRgb(0x92, 0x40, 0x0E)); // #92400E (default XAML)
        if (ErrorBannerIcon is not null) ErrorBannerIcon.Foreground = TryFindResource("WarnBrush") as Brush;
    }

    private void SetBannerToError()
    {
        if (TryFindResource("ErrorBgBrush") is Brush bg) ErrorBanner.Background = bg;
        if (TryFindResource("ErrorBrush") is Brush err)
        {
            ErrorBanner.BorderBrush = err;
            ErrorBannerText.Foreground = err;
            if (ErrorBannerIcon is not null) ErrorBannerIcon.Foreground = err;
        }
    }

    private static bool IsRetryable(string? code)
        => code is ErrorCodes.SpoolerFailed or ErrorCodes.PrinterNotFound;

    private void HideBanner() => ErrorBanner.Visibility = Visibility.Collapsed;

    /// <summary>✕ đóng banner — luôn cho phép đóng, không phụ thuộc Retry.</summary>
    private void BannerClose_Click(object sender, RoutedEventArgs e) => HideBanner();

    public ICommand OpenFileCommand => new RelayCommand<PrintJob>(job =>
    {
        if (job is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(job.FilePath) { UseShellExecute = true });
            // Theo dõi file đổi → reload bản mới (đúng yêu cầu: in luôn bản mới nhất)
            _ = Task.Run(async () =>
            {
                var last = File.GetLastWriteTimeUtc(job.FilePath);
                await Task.Delay(8000);
                if (File.Exists(job.FilePath))
                {
                    var now = File.GetLastWriteTimeUtc(job.FilePath);
                    if (now > last)
                    {
                        job.WasReloaded = true;
                        Dispatcher.Invoke(() => { JobList.Items.Refresh(); ShowToast($"Đã nạp lại bản mới: {job.FileName}"); });
                    }
                }
            });
        }
        catch (Exception ex)
        {
            ShowBanner(ErrorCodes.FileNotFound, $"Không mở được file: {job.FileName}", ex.Message);
        }
    });
}

/// <summary>Relay command đơn giản (không cần thư viện MVVM).</summary>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    public RelayCommand(Action<T?> execute) => _execute = execute;
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute((T?)parameter);
}

/// <summary>Group header: đường dẫn folder đầy đủ → tên leaf ("C:\a\b" → "b").</summary>
public sealed class FolderLeafConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrWhiteSpace(s))
        {
            var leaf = s.TrimEnd('\\', '/');
            var name = Path.GetFileName(leaf);
            if (!string.IsNullOrEmpty(name)) return name;
        }
        return "File rời";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}