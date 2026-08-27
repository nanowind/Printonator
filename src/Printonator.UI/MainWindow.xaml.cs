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
    private bool _autoRemovePrinted;   // tick "Tự xóa file đã in" (footer) — mặc định tắt
    private int _printDoneCount;       // số lô đã in xong → badge bell

    /// <summary>Cấu hình mặc định cho FILE MỚI (thay thế bảng Paper setup cũ) — áp khi thêm file.</summary>
    private readonly PrintConfig _defaultConfig = new();

    public ObservableCollection<PrintJob> Jobs => _queue.Jobs;

    /// <summary>Máy in đang chọn (PrinterInfo đầy đủ trạng thái/khả năng) — combo bind TwoWay.</summary>
    public PrinterInfo? SelectedPrinter { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        LoadUiSettings();
        AutoRemoveChk.IsChecked = _autoRemovePrinted;
        BellBadgeBorder.Visibility = Visibility.Collapsed;
        DoneNotif.Visibility = Visibility.Collapsed;
        NotifEmptyText.Visibility = Visibility.Visible; // chưa in xong lô nào → "Không có thông báo mới"
        JobList.SelectionChanged += OnSelectionChanged;
        _queue.JobStateChanged += OnJobStateChanged;
        _queue.AllJobsCompleted += OnAllCompleted;
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
        await Task.CompletedTask;
    }

    private void LoadPrinters()
    {
        var r = new PrinterService().ListPrinters();
        if (!r.IsSuccess)
        {
            ShowBanner(r.Error!.Code, r.Error.Message, r.Error.Hint);
            return;
        }
        var printers = r.Value!;
        PrinterCombo.ItemsSource = printers;
        SelectedPrinter = printers.FirstOrDefault(p => p.IsAvailable) ?? printers.FirstOrDefault();
        if (SelectedPrinter is not null) PrinterCombo.SelectedItem = SelectedPrinter;
        UpdatePrinterDot();
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
                var name = Path.GetFileName(f);
                if (name.StartsWith("~$", StringComparison.Ordinal)) continue; // file khóa tạm của Office
                if (IsSupported(f)) result.Add(f);
            }
        }
        catch { /* thư mục không đọc được — bỏ qua */ }
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
        ShowToast($"Đưa {targets.Count} file vào hàng đợi in.");   // tin tốt → toast xanh, không phải banner lỗi
        ApplySelectedPrinter(targets);
        foreach (var job in targets)
            _queue.ProcessExisting(job);   // KHÔNG enqueue lại — tránh trùng dòng
        JobList.Items.Refresh();
        UpdateFooter();
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

    private void PrintAll_Click(object sender, RoutedEventArgs e)
    {
        // In tất cả job đang CHỜ (Queued) — không tự in lại job đã xong/lỗi
        // (muốn in lại job cũ → chọn nó rồi bấm Print selected)
        var ready = Jobs.Where(j => j.State == JobState.Queued).ToList();
        if (ready.Count == 0)
        {
            ShowBanner(ErrorCodes.NoFilesSelected, "Không có file nào ở trạng thái chờ in.", "Thêm file hoặc chọn file đã in để in lại.");
            return;
        }
        PrintJobs(ready, $"in tất cả ({ready.Count} file)");
    }

    // In các file đang chọn — nút riêng, không phải Print all
    private void PrintSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = JobList.SelectedItems.OfType<PrintJob>()
            .Where(j => j.State == JobState.Queued).ToList();
        if (selected.Count == 0)
        {
            ShowBanner(ErrorCodes.NoFilesSelected, "Chưa chọn file nào ở trạng thái chờ in.", "Chọn file rồi bấm Print selected.");
            return;
        }
        PrintJobs(selected, $"in {selected.Count} file đã chọn");
    }

    private void PrintJobs(List<PrintJob> jobs, string action)
    {
        var ready = jobs.Where(j => j.State is JobState.Queued or JobState.Done or JobState.Error or JobState.Cancelled).ToList();
        if (ready.Count == 0) { ShowBanner(ErrorCodes.NoFilesSelected, "Không có file nào ở trạng thái chờ in.", ""); return; }
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

        // ProcessExisting: in job đã có, KHÔNG thêm dòng mới
        foreach (var j in ready)
            _queue.ProcessExisting(j);
        ShowToast($"Bắt đầu {action}...");
        JobList.Items.Refresh();
        UpdateFooter();
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

    // Mở/đóng popover thông báo khi bấm bell
    private void BellToggle_Changed(object sender, RoutedEventArgs e)
    {
        // Popup IsOpen đã bind theo IsChecked — chỉ cần sync trạng thái badge
        NotifPopup.IsOpen = BellToggle.IsChecked == true;
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
            if (job.State == JobState.Done && _autoRemovePrinted)
                ScheduleAutoRemove(job);
        });
    }

    private void OnAllCompleted()
    {
        Dispatcher.Invoke(() =>
        {
            _printDoneCount++;
            var done = Jobs.Count(j => j.State == JobState.Done);
            DoneNotifTitle.Text = $"Đã in xong {done} file";
            DoneNotifSub.Text = $"{DateTime.Now:HH:mm} · {(_autoRemovePrinted ? "sẽ tự rời khỏi hàng đợi" : "vẫn giữ file trong hàng đợi")}";
            DoneNotif.Visibility = Visibility.Visible;
            NotifEmptyText.Visibility = Visibility.Collapsed;
            BellBadge.Text = _printDoneCount.ToString();
            BellBadgeBorder.Visibility = Visibility.Visible;
            ShowToast($"Đã in xong tất cả ({done} file).");
            UpdateFooter();
        });
    }

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

        // Chrome/Edge: chỉ giết headless mồ côi (CDP/printto), GIỮ trình duyệt user đang dùng.
        foreach (var name in new[] { "chrome", "msedge" })
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                    if (string.IsNullOrEmpty(p.MainWindowTitle))
                        KillProcess(p);
            }
            catch { }
        }

        static void KillProcess(Process p)
        {
            try { p.Kill(entireProcessTree: true); p.WaitForExit(3000); } catch { }
        }
    }

    private static string UiSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Printonator", "ui.json");

    private void LoadUiSettings()
    {
        try
        {
            if (File.Exists(UiSettingsPath)
                && JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(UiSettingsPath)) is JsonElement j
                && j.TryGetProperty("autoRemovePrinted", out var v) && v.ValueKind == JsonValueKind.True)
                _autoRemovePrinted = true;
        }
        catch { }
    }

    private void SaveUiSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UiSettingsPath)!);
            File.WriteAllText(UiSettingsPath, JsonSerializer.Serialize(new { autoRemovePrinted = _autoRemovePrinted }));
        }
        catch { }
    }

    private void AutoRemoveChk_Toggled(object sender, RoutedEventArgs e)
    {
        _autoRemovePrinted = AutoRemoveChk.IsChecked == true;
        SaveUiSettings();
    }

    /// <summary>Chờ 1 nhịp rồi gỡ file đã in (Done) khỏi hàng đợi nếu vẫn thỏa điều kiện.</summary>
    private void ScheduleAutoRemove(PrintJob job)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(2500);
            Dispatcher.Invoke(() =>
            {
                if (!_autoRemovePrinted || job.State != JobState.Done || !Jobs.Contains(job)) return;
                _queue.RemoveJob(job);
                JobList.Items.Refresh();
                UpdateFooter();
            });
        });
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

        // "Print all (N)" — số job đang chờ (Penpot gap)
        var queued = Jobs.Count(j => j.State == JobState.Queued);
        PrintAllBtn.Content = queued > 0 ? $"Print all ({queued})" : "Print all";

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