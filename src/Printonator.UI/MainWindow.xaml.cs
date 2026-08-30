﻿using System.Collections.ObjectModel;
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
using Printonator.Core.Persistence;
using Printonator.Spool.Printing;
using Printonator.UI.Localization;

namespace Printonator.UI;

public partial class MainWindow : Window
{
    private readonly PrintQueue _queue = new();

    private string? _sortColumn;
    private bool _sortDescending;
    private int _printerScanGeneration;   // scan cũ về sau không được ghi đè kết quả scan mới
    private Popup? _openPagePopup;        // popup Pages đang mở — đóng bằng field (không duyệt container grouped)

    // ===== Refactor T0.1: batch orchestration + footer/banner/toast tách sang class riêng =====
    private readonly PrintBatchOrchestrator _orchestrator;
    private readonly FooterController _footer;

    private readonly CancellationTokenSource _lifeCts = new();   // vòng đời MainWindow — huỷ task nền khi đóng

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

        // ===== Refactor T0.1: batch orchestration + footer/banner/toast tách sang class riêng =====
        _footer = new FooterController(
            FooterStats, FooterProgress, ProgressText, TaskbarInfo, PrintMainBtn,
            EmptyState, SearchBox,
            Toast, ToastText,
            ErrorBanner, ErrorBannerText, RetryBtn, ErrorBannerIcon,
            BellBadge, BellBadgeBorder, NotifEmptyText,
            Notifications, _queue,
            () => JobList.SelectedItems,
            () => JobList.SelectedItems.Count)
        {
            SyncSelectAllStateCallback = SyncSelectAllState,
        };
        _footer.UpdateNotificationBadge();

        _orchestrator = new PrintBatchOrchestrator(_queue, Dispatcher, () => SelectedPrinter, this);
        _orchestrator.AllCompleted += OnAllCompleted;
        _orchestrator.BatchStopped += OnBatchStopped;
        _orchestrator.ToastRequested += ShowToast;
        _orchestrator.BannerRequested += ShowBanner;
        _orchestrator.FooterUpdated += UpdateFooter;
        _orchestrator.RefreshRequested += () => JobList.Items.Refresh();

        BellBadgeBorder.Visibility = Visibility.Collapsed;
        Notifications.CollectionChanged += (_, _) => UpdateNotificationBadge();
        JobList.SelectionChanged += OnSelectionChanged;
        _queue.JobStateChanged += OnJobStateChanged;

        PrinterCombo.SelectionChanged += (_, _) => UpdatePrinterDot();
        PreviewMouseLeftButtonDown += Window_PreviewMouseLeftButtonDown;   // bấm ngoài → đóng popup Pages
        // Chuyển sang app KHÁC (main window mất focus) → đóng popup (Popup là cửa sổ riêng, LUÔN nổi trên
        // mọi app nếu không đóng — user chuyển app vẫn thấy panel, khó chịu).
        Deactivated += (_, _) => { if (_openPagePopup is { IsOpen: true }) _openPagePopup.IsOpen = false; };
        Closed += (_, _) =>
        {
            try { QueueStore.Save(_queue.Jobs); }
            catch { /* Đóng app: lưu lỗi (disk full, file bị khóa...) không đáng để phá shutdown — cleanup dưới vẫn phải chạy */ }
            _lifeCts.Cancel();
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
        RestorePendingJobs();
        LoadPrinters();
        _queue.MaxRetries = 2;
        _ = CheckForUpdatesSilentAsync();   // kiểm tra bản mới nền khi app mở — thông báo vào bell nếu có
        SeedTestApprovalIfRequested();      // test hook PRINTONATOR_TEST_APPROVAL=1 — chỉ khi env set, không ảnh hưởng production
        UpdateApprovalBar();                // trạng thái duyệt ban đầu (job Mcp khôi phục / test seed)
        await Task.CompletedTask;
    }

    /// <summary>
    /// Test hook: PRINTONATOR_TEST_APPROVAL=1 → tạo 1 job Mcp giả (temp .txt) chờ duyệt để kiểm
    /// tra màn duyệt job AwaitingApproval. CHỈ chạy khi env set — production không bao giờ gọi.
    /// </summary>
    private void SeedTestApprovalIfRequested()
    {
        if (Environment.GetEnvironmentVariable("PRINTONATOR_TEST_APPROVAL") != "1") return;
        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"printonator_test_approval_{Guid.NewGuid():N}.txt");
            File.WriteAllText(path, "Printonator test approval job — delete me.");
            var job = new PrintJob
            {
                FilePath = path,
                FileName = Path.GetFileName(path),
                Format = "TXT",
                Config = DefaultConfigFor(DefaultPaperFor("TXT")),
                Source = JobSource.Mcp,
            };
            _queue.AddForApproval(new[] { job });
        }
        catch { /* test seed lỗi (vd mất quyền temp) — bỏ qua, không làm hỏng app */ }
    }

    /// <summary>
    /// Khôi phục hàng đợi in từ lần chạy trước (queue.json). Lọc bỏ file không còn tồn tại.
    /// Job người dùng → AddOnly (Queued, chờ bấm in); job từ AI (MCP) → AddForApproval giữ trạng thái
    /// chờ duyệt (AwaitingApproval). Vì PrintJob.State không set được từ UI nên restore = tạo job MỚI.
    /// </summary>
    private void RestorePendingJobs()
    {
        var restored = QueueStore.Load();
        if (restored.Count == 0) return;

        var jobs = restored
            .Where(e => File.Exists(e.FilePath))   // lọc file không còn tồn tại
            .Select(e => new PrintJob
            {
                FilePath = e.FilePath,
                FileName = e.FileName,
                Format = e.Format,
                Config = e.Config,
                Source = e.Source,
                CreatedAt = e.CreatedAt,
            }).ToList();
        if (jobs.Count == 0) return;

        var approval = jobs.Where(j => j.Source == JobSource.Mcp).ToList();
        var rest = jobs.Except(approval).ToList();
        if (approval.Count > 0) _queue.AddForApproval(approval);
        if (rest.Count > 0) _queue.AddOnly(rest);
        UpdateFooter();
        ShowToast(L10n.F(Keys.Persist.RestoredToast, jobs.Count));
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
                    AddNotification(NotificationKind.Update, L10n.F(Keys.Notify.UpdateAvailable, info.Version),
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
        Message = L10n.S(Keys.Banner.PrinterScanError),
        Hint = L10n.S(Keys.Banner.PrinterScanHint),
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
                L10n.F(Keys.Banner.PrinterUnresponsive, unresponsive),
                L10n.S(Keys.Banner.PrinterUnresponsiveHint));

        // GIỮ máy in user đã chọn (theo TÊN, dùng record MỚI trong list mới) — KHÔNG tự đổi máy khi rescan.
        // Lỗi cũ: rescan tự reset về máy default → thường là "Microsoft Print to PDF" → mọi job in ra PDF.
        var prevName = SelectedPrinter?.Name;
        var kept = prevName is null ? null
            : printers.FirstOrDefault(p => p.Name.Equals(prevName, StringComparison.OrdinalIgnoreCase));
        if (kept is not null)
        {
            SelectedPrinter = kept;
            PrinterCombo.SelectedItem = kept;
            UpdatePrinterDot();
            ShowPrinterReminder();
            return;
        }

        // Auto-pick ưu tiên máy VẬT LÝ (tránh tự chọn "Microsoft Print to PDF" khi default offline).
        // Hệ toàn máy ảo vẫn chọn được nhờ fallback cuối.
        SelectedPrinter = printers.FirstOrDefault(p => p.IsDefault && p.IsAvailable && !p.IsVirtual)
                      ?? printers.FirstOrDefault(p => p.IsDefault && p.IsAvailable)
                      ?? printers.FirstOrDefault(p => p.IsDefault && !p.IsVirtual)
                      ?? printers.FirstOrDefault(p => p.IsDefault)
                      ?? printers.FirstOrDefault(p => p.IsAvailable && !p.IsVirtual)
                      ?? printers.FirstOrDefault(p => p.IsAvailable)
                      ?? printers.FirstOrDefault(p => !p.IsVirtual)
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
            PrinterStatusDot.ToolTip = L10n.S(Keys.Main.PrinterStatusDotNoPrinter);
            return;
        }
        PrinterStatusDot.Fill = p.IsAvailable ? Brushes.Green : Brushes.Red;
        PrinterStatusDot.ToolTip = p.StatusDetail is null
            ? L10n.F(Keys.Main.PrinterStatusReady, p.Name)
            : L10n.F(Keys.Main.PrinterStatusWithDetail, p.Name, p.StatusDetail);
    }

    /// <summary>Phím Delete (phím tắt, không có nút UI) — xóa các file đang chọn khỏi hàng đợi.</summary>
    private void DeleteSelection_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var targets = JobList.SelectedItems.OfType<PrintJob>().ToList();
        if (targets.Count == 0) return;
        foreach (var job in targets)
            _queue.RemoveJob(job);
        UpdateFooter();
        ShowToast(L10n.F(Keys.Toast.DeletedFiles, targets.Count));
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

        // Đóng popup Pages đang mở (nếu khác) — track bằng FIELD, không duyệt container (ListBox grouped không đáng tin)
        if (_openPagePopup is not null && _openPagePopup != pop) _openPagePopup.IsOpen = false;
        _openPagePopup = pop;

        // Sheet cần in (Excel): probe danh sách sheet của file → hiện combo (ẩn nếu không phải Excel)
        _ = PopulateSheetComboAsync(cell, job);

        pop.IsOpen = true;
    }

    /// <summary>Đổi màu nút Print chính: null = style mặc định (RoundedBtn); hex = nền màu (Pause đỏ / Resume xanh lá).</summary>
    private void SetPrintButtonColor(string? hex)
    {
        _footer.SetPrintButtonColor(hex);
    }

    /// <summary>✕ Đóng popup Pages của dòng hiện tại.</summary>
    private void PagesClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.FindName("PagesPopup") is Popup pop)
        {
            pop.IsOpen = false;
            if (_openPagePopup == pop) _openPagePopup = null;
        }
    }

    /// <summary>Bấm chuột trái → CHỈ đóng popup Pages khi click vào phần tử THUỘC main window (row, toolbar…).
    /// Popup panel + dropdown của ComboBox sheet là CỬA SỔ RIÊNG — không nằm trong visual tree của main
    /// window → KHÔNG đóng (bấm combo sheet chọn được, không bị tắt).</summary>
    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_openPagePopup is not { IsOpen: true }) return;
        if (e.OriginalSource is DependencyObject src && IsVisualDescendantOf(src, this))
            _openPagePopup.IsOpen = false;
    }

    private static bool IsVisualDescendantOf(DependencyObject node, DependencyObject ancestor)
    {
        while (node is not null)
        {
            if (node == ancestor) return true;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    /// <summary>Probe danh sách sheet của file Excel để hiện combo "Sheet cần in" trong popup ô Pages.</summary>
    private async Task PopulateSheetComboAsync(FrameworkElement cell, PrintJob job)
    {
        try
        {
            if (cell.FindName("PSheetLabel") is not System.Windows.Controls.TextBlock label
                || cell.FindName("PSheetCombo") is not ComboBox combo)
                return;

            var isExcel = job.Format is "XLS" or "XLSX" or "XLSM";
            if (!isExcel)
            {
                label.Visibility = Visibility.Collapsed;
                combo.Visibility = Visibility.Collapsed;
                return;
            }

            // LOADING STATE: hiện "Đang đọc sheet…" (disabled) ngay — probe .xls lần đầu ~3s
            label.Visibility = Visibility.Visible;
            combo.Visibility = Visibility.Visible;
            combo.IsEnabled = false;
            combo.Items.Clear();
            combo.Items.Add(new ComboBoxItem { Content = L10n.S(Keys.Common.SheetLoading), Tag = "__loading__" });
            combo.SelectedIndex = 0;

            var sheets = await OfficeComPrintEngine.ListSheetsAsync(job.FilePath);
            if (sheets.Length == 0)
            {
                label.Visibility = Visibility.Collapsed;
                combo.Visibility = Visibility.Collapsed;
                return;
            }
            combo.IsEnabled = true;
            combo.Items.Clear();
            combo.Items.Add(new ComboBoxItem { Content = L10n.S(Keys.Common.SheetAll), Tag = "" });
            foreach (var s in sheets)
                combo.Items.Add(new ComboBoxItem { Content = s, Tag = s });

            // Giữ lựa chọn đã lưu nếu còn trong danh sách
            var cur = job.Config.SheetName;
            var selected = string.IsNullOrEmpty(cur)
                ? combo.Items.OfType<ComboBoxItem>().FirstOrDefault()
                : combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, cur, StringComparison.OrdinalIgnoreCase));
            if (selected is not null) combo.SelectedItem = selected;

            label.Visibility = Visibility.Visible;
            combo.Visibility = Visibility.Visible;
        }
        catch { }
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
        // Sheet cần in (Excel): "Tất cả" (rỗng) → in toàn bộ; còn lại tên sheet cụ thể
        var sheet = (fe.FindName("PSheetCombo") as ComboBox)?.SelectedItem is ComboBoxItem sci ? (string)sci.Tag : null;
        foreach (var j in targets)
        {
            j.Config.PageRange = range;
            j.Config.SheetName = string.IsNullOrEmpty(sheet) ? null : sheet;
        }

        pop.IsOpen = false;
        if (_openPagePopup == pop) _openPagePopup = null;
        JobList.Items.Refresh();
        var sheetTxt = string.IsNullOrEmpty(sheet) ? "" : L10n.F(Keys.Toast.AppliedPagesPopupSheet, sheet);
        ShowToast(L10n.F(Keys.Toast.AppliedPagesPopup, (range == "All" ? L10n.S(Keys.Main.PagesAllLower) : range), sheetTxt, targets.Count));
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
                ? L10n.F(Keys.Toast.AddedWithSkip, supported.Count, skipped)
                : null);
        else if (skipped > 0)
            ShowToast(L10n.F(Keys.Toast.SkippedFormat, skipped));
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
            ShowBanner(ErrorCodes.FileNotFound, L10n.S(Keys.Banner.PasteError), ex.Message);
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
                ? L10n.F(Keys.Toast.FilesAddedFromFolder, toAdd.Count, folderCount).TrimEnd('.')
                : L10n.F(Keys.Toast.FilesAdded, toAdd.Count).TrimEnd('.');
            AddFiles(toAdd, baseText + SkipSummary(unsupported, missing));
        }
        else if (unsupported > 0 || missing > 0)
        {
            ShowToast(L10n.F(Keys.Toast.SkippedOnly, SkippedList(unsupported, missing)));
        }
        return (toAdd.Count, unsupported, missing, folderCount);
    }

    /// <summary>Hậu tố toast: ", bỏ qua X..." / "." khi không có gì bị bỏ qua.</summary>
    private static string SkipSummary(int unsupported, int missing)
    {
        if (unsupported <= 0 && missing <= 0) return ".";
        return L10n.F(Keys.Toast.SkipSummarySome, SkippedList(unsupported, missing));
    }

    /// <summary>Ghép cụm "N file không hỗ trợ định dạng và M đường dẫn không tồn tại".</summary>
    private static string SkippedList(int unsupported, int missing)
    {
        var parts = new List<string>();
        if (unsupported > 0) parts.Add(L10n.F(Keys.Toast.SkippedUnsupported, unsupported));
        if (missing > 0) parts.Add(L10n.F(Keys.Toast.SkippedMissing, missing));
        return string.Join(L10n.S(Keys.Toast.SkippedListJoin), parts);
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
            ShowToast(L10n.F(Keys.Toast.AppliedConfig, targets.Count));
        }
        else
        {
            dlg.Result.CopyInto(_defaultConfig);
            ShowToast(L10n.S(Keys.Toast.DefaultConfig));
        }
    }

    /// <summary>Khổ giấy mặc định theo loại file (Penpot gap): bản vẽ A3, hóa đơn A5; còn lại theo file.</summary>
    private static string DefaultPaperFor(string format) => format switch
    {
        "DWG" or "DXF" or "PLT" or "DWT" => "A3",
        "TXT" or "CSV" => "A5", // hóa đơn/biên nhận dạng ngắn
        // Office (Excel/Word/PPT) có CẤU HÌNH IN SẴN trong file (khổ giấy/chiều/print area) → GIỮ NGUYÊN
        // (AsDocument = "theo tài liệu"). Ép A4/portrait làm PDF/in ra sai so với file (bug v0.1.6).
        "XLS" or "XLSX" or "XLSM" or "DOC" or "DOCX" or "RTF" or "PPT" or "PPTX" or "PPS" or "PPSX" => PaperCatalog.AsDocument,
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
        var dup = 0;
        foreach (var p in paths)
        {
            try
            {
                if (!File.Exists(p)) { ShowBanner(ErrorCodes.FileNotFound, L10n.F(Keys.Banner.FileNotFound, p), ""); continue; }
                // Dedup: cùng file (path không phân biệt hoa thường) đã có trong danh sách → KHÔNG tạo row 2
                if (_queue.Jobs.Any(j => j.FilePath.Equals(p, StringComparison.OrdinalIgnoreCase))) { dup++; continue; }
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
                ShowBanner(ErrorCodes.FileCorrupted, L10n.F(Keys.Banner.FileAddError, Path.GetFileName(p)), ex.Message);
            }
        }
        UpdateFooter();
        if (added > 0)
        {
            var msg = toast ?? L10n.F(Keys.Toast.FilesAdded, added);
            if (dup > 0) msg += L10n.F(Keys.Toast.DedupSuffix, dup);
            ShowToast(msg);
        }
        else if (dup > 0)
        {
            ShowToast(L10n.F(Keys.Toast.NoDuplicates, dup));
        }
    }

    // ===== Multi-select helpers =====
    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var count = JobList.SelectedItems.Count;
        BulkCountText.Text = L10n.F(Keys.Main.BulkCountFormat, count);
        BulkBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SyncSelectAllState();
        UpdateFooter(); // in-ngữ-cảnh: nút Print (N) cập nhật theo selection
        if (count > 0)
        {
            FooterHint.Text = L10n.S(Keys.Main.FooterHintSelection);
            var first = JobList.SelectedItems.OfType<PrintJob>().FirstOrDefault();
            BulkSummaryText.Text = first is null
                ? ""
                : L10n.F(Keys.Main.BulkSummaryFormat, first.Config.SummaryText);
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
            return OrderByDisplay(selected).ToList();   // click phải vào file trong nhóm → cả nhóm, theo thứ tự hiển thị
        return new List<PrintJob> { clicked }; // click phải file lẻ → chỉ file đó
    }

    // Cập nhật tiêu đề menu theo số file đang chọn (ContextMenu.Opened)
    private void RowContextMenu_Opening(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.ContextMenu is not ContextMenu menu) return;
        var n = GetTargetJobs(fe).Count;

        if (menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "CtxOpen") is { } open)
            open.Header = n > 1 ? L10n.F(Keys.Main.CtxOpenPlural, n) : L10n.S(Keys.Main.CtxOpen);
        if (menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "CtxEdit") is { } edit)
            edit.Header = n > 1 ? L10n.F(Keys.Main.CtxEditPlural, n) : L10n.S(Keys.Main.CtxEdit);
        if (menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "CtxPrint") is { } print)
            print.Header = n > 1 ? L10n.F(Keys.Main.CtxPrintPlural, n) : L10n.S(Keys.Main.CtxPrint);
        if (menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "CtxItemSettings") is { } itemSettings)
            itemSettings.Header = n > 1 ? L10n.F(Keys.Main.CtxItemSettingsPlural, n) : L10n.S(Keys.Main.CtxItemSettings);
        if (menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "CtxRemove") is { } remove)
            remove.Header = n > 1 ? L10n.F(Keys.Main.CtxRemovePlural, n) : L10n.S(Keys.Main.CtxRemove);
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
        ShowToast(L10n.F(Keys.Toast.CtxPrintQueued, targets.Count));
        ApplySelectedPrinter(targets);
        StartPrintBatch(targets.ToList(), L10n.F(Keys.Main.ActionThisBatch, targets.Count));
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
            ShowToast(L10n.F(Keys.Toast.AppliedPageRangeCtx, dlg.PageRange, targets.Count));
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
        // Nút 3 trạng thái: đang tạm dừng → Resume; đang in → Pause (lỡ bấm in thì dừng ngay); rảnh → in
        if (_queue.IsPaused)
        {
            _queue.Resume();
            UpdateFooter();
            ShowToast(L10n.S(Keys.Toast.Resumed));
            return;
        }
        if (Jobs.Any(j => j.State is JobState.Converting or JobState.Spooling))
        {
            _queue.Pause();
            UpdateFooter();
            ShowToast(L10n.S(Keys.Toast.Paused));
            return;
        }

        // In theo thứ tự đang HIỂN THỊ (sort + nhóm thư mục) — không theo thứ tự chèn vào Jobs.
        var selected = OrderByDisplay(JobList.SelectedItems.OfType<PrintJob>())
            .Where(j => j.State == JobState.Queued).ToList();
        if (selected.Count > 0)
        {
            PrintJobs(selected, L10n.F(Keys.Main.ActionSelectedFiles, selected.Count));
            return;
        }

        // Không chọn → in tất cả job ĐÁNG IN (Queued + Done/Error/Cancelled — để print all có thể
        // in lại file đã in khi user đồng ý qua confirm). PrintJobs sẽ hỏi nếu có file Done.
        var ready = OrderByDisplay(Jobs.Where(j => j.State is JobState.Queued or JobState.Done or JobState.Error or JobState.Cancelled)).ToList();
        if (ready.Count == 0)
        {
            ShowBanner(ErrorCodes.NoFilesSelected, L10n.S(Keys.Banner.NoFilesSelected), L10n.S(Keys.Banner.PrintAllHint));
            return;
        }
        PrintJobs(ready, L10n.F(Keys.Main.ActionAllFiles, ready.Count));
    }

    // Hủy lô: hủy toàn bộ job chờ in (Queued + AwaitingApproval) + job đang in (Converting/Spooling)
    private void CancelBatch_Click(object sender, RoutedEventArgs e)
    {
        var pending = Jobs
            .Where(j => j.State is JobState.Queued or JobState.AwaitingApproval or JobState.Converting or JobState.Spooling)
            .ToList();
        if (pending.Count == 0) { ShowToast(L10n.S(Keys.Stop.BatchNothingToCancel)); return; }

        var running = pending.Count(j => j.State is JobState.Converting or JobState.Spooling);
        var icon = running > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question;
        var message = running > 0
            ? L10n.F(Keys.Stop.CancelConfirm, pending.Count) + "\n" + L10n.S(Keys.Stop.CancelRunning)
            : L10n.F(Keys.Stop.CancelConfirm, pending.Count);
        var ask = MessageBox.Show(message,
            L10n.S(Keys.Stop.CancelBatch), MessageBoxButton.YesNo, icon);
        if (ask != MessageBoxResult.Yes) return;

        _queue.CancelPending();                       // job Queued → Cancelled
        foreach (var j in pending)                    // các job còn lại chưa bị đổi state bởi CancelPending
        {
            if (j.State == JobState.AwaitingApproval) _queue.RejectJob(j);
            else if (j.State is JobState.Converting or JobState.Spooling) _queue.CancelJob(j);
        }
        ShowToast(L10n.S(Keys.Stop.BatchCancelled));
        UpdateFooter();
    }

    // ===== Màn duyệt job AwaitingApproval (job từ AI/MCP chờ người duyệt) =====

    /// <summary>Cập nhật thanh duyệt: ẩn khi không có job chờ duyệt, hiện + đếm khi có.</summary>
    private void UpdateApprovalBar()
    {
        if (ApprovalBar is null) return; // XAML chưa dựng xong
        var n = _queue.Jobs.Count(j => j.State == JobState.AwaitingApproval);
        ApprovalBar.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        ApprovalCount.Text = $"({L10n.N(n)})";   // "(N)" — số file đang chờ duyệt
    }

    /// <summary>Duyệt TẤT CẢ job chờ duyệt (xác nhận trước) — đẩy vào hàng đợi để in.</summary>
    private void ApproveAll_Click(object sender, RoutedEventArgs e)
    {
        var pending = _queue.Jobs.Where(j => j.State == JobState.AwaitingApproval).ToList();
        if (pending.Count == 0) return;
        var ask = MessageBox.Show(L10n.F(Keys.Approve.ApproveAllConfirm, pending.Count),
            L10n.S(Keys.Approve.Title), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ask != MessageBoxResult.Yes) return;

        var approved = 0;
        foreach (var job in pending)
            if (_queue.ApproveJob(job)) approved++;
        ShowToast(L10n.F(Keys.Approve.ApprovedToast, approved));
    }

    /// <summary>Từ chối TẤT CẢ job chờ duyệt (xác nhận trước) — chuyển Cancelled, không in.</summary>
    private void RejectAll_Click(object sender, RoutedEventArgs e)
    {
        var pending = _queue.Jobs.Where(j => j.State == JobState.AwaitingApproval).ToList();
        if (pending.Count == 0) return;
        var ask = MessageBox.Show(L10n.F(Keys.Approve.RejectAllConfirm, pending.Count),
            L10n.S(Keys.Approve.Title), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ask != MessageBoxResult.Yes) return;

        var rejected = 0;
        foreach (var job in pending)
            if (_queue.RejectJob(job)) rejected++;
        ShowToast(L10n.F(Keys.Approve.RejectedToast, rejected));
    }

    /// <summary>✕ đóng thanh duyệt (chỉ ẩn tạm — job chờ duyệt vẫn còn trong danh sách).</summary>
    private void ApprovalClose_Click(object sender, RoutedEventArgs e)
        => ApprovalBar.Visibility = Visibility.Collapsed;

    private void PrintJobs(List<PrintJob> jobs, string action)
    {
        _orchestrator.PrintJobs(jobs, action);
    }

    /// <summary>Sắp xếp lại batch theo thứ tự đang HIỂN THỊ (sort + nhóm thư mục của user), không theo
    /// thứ tự chèn vào Jobs — WPF sort/group nằm ở View, Jobs giữ thứ tự chèn ban đầu → nếu không ép,
    /// in tuần tự chạy sai thứ tự mắt thấy ("file dưới cùng in trước"). Quét đệ quy qua các nhóm:
    /// top-to-bottom đúng như list đang hiện.</summary>
    private IEnumerable<PrintJob> OrderByDisplay(IEnumerable<PrintJob> jobs)
    {
        return _orchestrator.OrderByDisplay(jobs);
    }

    /// <summary>Đường thực thi CHUNG cho mọi lệnh in (nút Print, In file này…) — ĐÚNG 1 nơi việc
    /// đẩy job, refresh, và fire completion. Trước đây tách rời ở từng nút → sửa chỗ này quên chỗ
    /// kia (vd completion chỉ chạy cho nút Print, không cho context menu).</summary>
    private void StartPrintBatch(List<PrintJob> ready, string action)
    {
        _orchestrator.StartPrintBatch(ready, action);
    }

    /// <summary>Ép máy in đang chọn lên job — máy in thanh công cụ luôn thắng máy cũ đã ghi trong config.</summary>
    private void ApplySelectedPrinter(IEnumerable<PrintJob> jobs)
    {
        _orchestrator.ApplySelectedPrinter(jobs);
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

    /// <summary>Nút "Cấu hình" (Presets) — mở trình quản lý preset; chọn Áp dụng → cập nhật config mặc định cho file mới,
    /// và nếu có file đang chọn thì copy preset vào config từng file đó.</summary>
    private void ManagePresets_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new PresetManagerWindow { Owner = this };
        dlg.ShowDialog();
        if (dlg.SelectedPreset is null) return;

        var cfg = dlg.SelectedPreset.ToPrintConfig();
        cfg.CopyInto(_defaultConfig);
        var targets = JobList.SelectedItems.OfType<PrintJob>().ToList();
        if (targets.Count > 0)
        {
            foreach (var job in targets)
                cfg.CopyInto(job.Config);
            JobList.Items.Refresh();
        }
        ShowToast(L10n.S(Keys.Preset.Applied));
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
            UpdateApprovalBar();
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
                L10n.F(Keys.Notify.BatchDone, done),
                $"{DateTime.Now:HH:mm}");
            ShowToast(L10n.F(Keys.Toast.BatchDone, done));

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

    /// <summary>Lô in bị DỪNG do 1 file lỗi (stop-on-error): các file sau giữ Queued chờ Resume.
    /// Báo rõ file lỗi + còn bao nhiêu file chờ — KHÔNG báo "in xong" (không đúng), KHÔNG treo chờ.</summary>
    private void OnBatchStopped(int done, PrintJob? failed)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var waiting = Jobs.Count(j => j.State == JobState.Queued);
            var name = failed?.FileName ?? L10n.S(Keys.Common.FileFallback);
            AddNotification(NotificationKind.Warning,
                L10n.F(Keys.Notify.BatchStopped, name),
                L10n.F(Keys.Notify.BatchStoppedDetail, done, waiting));
            ShowToast(L10n.F(Keys.Toast.BatchStopped, name, done, waiting));
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
        _footer.UpdateNotificationBadge();
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
            AddNotification(NotificationKind.Warning, L10n.S(Keys.Notify.UpdateLatest), AppVersion);
            return;
        }
        AddNotification(NotificationKind.Update, L10n.F(Keys.Notify.UpdateAvailable, info.Version),
            info.Name, act: () => PromptAndInstallUpdate(info));
    }

    /// <summary>Xác nhận + tải + xác thực SHA-256 + chạy installer (GUI, không cài im lặng).</summary>
    private async void PromptAndInstallUpdate(UpdateInfo info)
    {
        var ask = MessageBox.Show(
            L10n.F(Keys.Banner.UpdateConfirm, info.Version, info.Notes), L10n.S(Keys.Banner.UpdateConfirmTitle),
            MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (ask != MessageBoxResult.Yes) return;

        var path = await info.DownloadAsync(CancellationToken.None);
        if (path is null) { MessageBox.Show(L10n.S(Keys.Banner.UpdateDownloadFail), "Printonator", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (!await info.VerifySha256Async(path, info.InstallerSha256))
        {
            MessageBox.Show(L10n.S(Keys.Banner.UpdateChecksumFail), "Printonator", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!info.LaunchInstaller(path))
            MessageBox.Show(L10n.S(Keys.Banner.UpdateLaunchFail), "Printonator", MessageBoxButton.OK, MessageBoxImage.Error);
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
        _footer.UpdateFooter();
    }

    /// <summary>Empty state (Kéo thả file...) hiện khi hàng đợi trống VÀ không đang tìm kiếm.</summary>
    private void UpdateEmptyState()
    {
        _footer.UpdateEmptyState();
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
            ShowBanner(ErrorCodes.SpoolerFailed, L10n.S(Keys.Banner.SortError), ex.Message);
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
        ResetHeader(SortName, L10n.S(Keys.Main.ColName));
        ResetHeader(SortPages, L10n.S(Keys.Main.ColPages));
        ResetHeader(SortSettings, L10n.S(Keys.Main.ColSettings));
        ResetHeader(SortStatus, L10n.S(Keys.Main.ColStatus));

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
        _footer.ShowToast(message);
    }

    private void FadeToast(double to)
    {
        _footer.FadeToast(to);
    }

    private void ShowBanner(string? code, string message, string detail)
    {
        _footer.ShowBanner(code, message, detail);
    }
    private void HideBanner() => _footer.HideBanner();

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
                try
                {
                    await Task.Delay(8000, _lifeCts.Token);
                    _lifeCts.Token.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException) { return; } // app đóng — im lặng
                if (!File.Exists(job.FilePath)) return;
                var now = File.GetLastWriteTimeUtc(job.FilePath);
                if (now <= last) return;
                job.WasReloaded = true;
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                Dispatcher.Invoke(() => { JobList.Items.Refresh(); ShowToast(L10n.F(Keys.Toast.FileReloaded, job.FileName)); });
            });
        }
        catch (Exception ex)
        {
            ShowBanner(ErrorCodes.FileNotFound, L10n.F(Keys.Banner.FileOpenError, job.FileName), ex.Message);
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
        return L10n.S(Keys.Main.FolderLeafFallback);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}