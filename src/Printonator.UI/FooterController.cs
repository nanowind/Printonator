using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Printonator.Core;
using Printonator.Core.Models;
using Printonator.UI.Localization;

namespace Printonator.UI;

/// <summary>
/// Quản lý footer, banner, toast, notification badge — nhận control references qua tham số,
/// KHÔNG phụ thuộc MainWindow.
/// </summary>
public sealed class FooterController
{
    private readonly TextBlock _footerStats;
    private readonly ProgressBar _footerProgress;
    private readonly TextBlock _progressText;
    private readonly System.Windows.Shell.TaskbarItemInfo _taskbarInfo;
    private readonly Button _printMainBtn;
    private readonly StackPanel _emptyState;
    private readonly TextBox _searchBox;
    private readonly Border _toast;
    private readonly TextBlock _toastText;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly Border _errorBanner;
    private readonly TextBlock _errorBannerText;
    private readonly Button _retryBtn;
    private readonly TextBlock _errorBannerIcon;
    private readonly TextBlock _bellBadge;
    private readonly Border _bellBadgeBorder;
    private readonly TextBlock _notifEmptyText;
    private readonly ObservableCollection<AppNotification> _notifications;
    private readonly PrintQueue _queue;
    private readonly Func<System.Collections.IList> _selectedItemsGetter;
    private readonly Func<int> _selectedItemsCount;

    /// <summary>Callback để MainWindow đồng bộ checkbox chọn-tất-cả sau khi UpdateFooter.</summary>
    public Action? SyncSelectAllStateCallback { get; set; }

    /// <summary>Mã lỗi thuộc loại "warn/offline thật" → banner giữ nền vàng; các mã còn lại là lỗi thật → nền đỏ.</summary>
    private static readonly HashSet<string> WarningBannerCodes = new(
        [ErrorCodes.PrinterOffline, ErrorCodes.PrinterNotFound, ErrorCodes.PrinterNoPermission,
         ErrorCodes.SpoolerBusy, ErrorCodes.SpoolerFailed, ErrorCodes.EngineNotFound,
         ErrorCodes.EngineTimeout, ErrorCodes.OfficeAppBusy, ErrorCodes.NoFilesSelected],
        StringComparer.Ordinal);

    public FooterController(
        TextBlock footerStats, ProgressBar footerProgress, TextBlock progressText,
        System.Windows.Shell.TaskbarItemInfo taskbarInfo, Button printMainBtn,
        StackPanel emptyState, TextBox searchBox,
        Border toast, TextBlock toastText,
        Border errorBanner, TextBlock errorBannerText, Button retryBtn, TextBlock errorBannerIcon,
        TextBlock bellBadge, Border bellBadgeBorder, TextBlock notifEmptyText,
        ObservableCollection<AppNotification> notifications,
        PrintQueue queue,
        Func<System.Collections.IList> selectedItemsGetter,
        Func<int> selectedItemsCount)
    {
        _footerStats = footerStats;
        _footerProgress = footerProgress;
        _progressText = progressText;
        _taskbarInfo = taskbarInfo;
        _printMainBtn = printMainBtn;
        _emptyState = emptyState;
        _searchBox = searchBox;
        _toast = toast;
        _toastText = toastText;
        _errorBanner = errorBanner;
        _errorBannerText = errorBannerText;
        _retryBtn = retryBtn;
        _errorBannerIcon = errorBannerIcon;
        _bellBadge = bellBadge;
        _bellBadgeBorder = bellBadgeBorder;
        _notifEmptyText = notifEmptyText;
        _notifications = notifications;
        _queue = queue;
        _selectedItemsGetter = selectedItemsGetter;
        _selectedItemsCount = selectedItemsCount;

        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            FadeToast(0);
        };
    }

    public void SetPrintButtonColor(string? hex)
    {
        if (hex is null)
        {
            _printMainBtn.ClearValue(Control.BackgroundProperty);
            _printMainBtn.ClearValue(Control.BorderBrushProperty);
            _printMainBtn.ClearValue(Control.ForegroundProperty);
            return;
        }
        var brush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(hex));
        _printMainBtn.Background = brush;
        _printMainBtn.BorderBrush = brush;
        _printMainBtn.Foreground = Brushes.White;
    }

    public void UpdateFooter()
    {
        var total = _queue.Jobs.Count;
        var done = _queue.Jobs.Count(j => j.State == JobState.Done);
        var err = _queue.Jobs.Count(j => j.State == JobState.Error);
        _footerStats.Text = L10n.F(Keys.Main.FooterStatsFormat, total, done, err);

        var inRun = _queue.Jobs.Count(j => j.State is not JobState.Queued and not JobState.AwaitingApproval);
        var percent = inRun > 0 ? (int)(done * 100.0 / inRun) : 0;
        _footerProgress.Value = Math.Clamp(percent, 0, 100);
        _progressText.Text = L10n.F(Keys.Main.FooterProgressFormat, percent);
        _taskbarInfo.ProgressValue = percent / 100.0;
        _taskbarInfo.ProgressState =
            _queue.Jobs.Any(j => j.State is JobState.Converting or JobState.Spooling)
                ? System.Windows.Shell.TaskbarItemProgressState.Normal
                : (total > 0 ? System.Windows.Shell.TaskbarItemProgressState.None : System.Windows.Shell.TaskbarItemProgressState.None);

        var printing = _queue.Jobs.Any(j => j.State is JobState.Converting or JobState.Spooling);
        if (_queue.IsPaused)
        {
            _printMainBtn.Content = L10n.S(Keys.Main.PrintMainBtnResume);
            SetPrintButtonColor("#16A34A");
        }
        else if (printing)
        {
            _printMainBtn.Content = L10n.S(Keys.Main.PrintMainBtnPause);
            SetPrintButtonColor("#DC2626");
        }
        else
        {
            var queued = _queue.Jobs.Count(j => j.State == JobState.Queued);
            var selQueued = _selectedItemsGetter().OfType<PrintJob>().Count(j => j.State == JobState.Queued);
            _printMainBtn.Content = selQueued > 0
                ? L10n.F(Keys.Main.PrintMainBtnSelected, selQueued)
                : (queued > 0 ? L10n.F(Keys.Main.PrintMainBtnAll, queued) : L10n.S(Keys.Main.PrintMainButton));
            SetPrintButtonColor(null);
        }

        SyncSelectAllStateCallback?.Invoke();
        UpdateEmptyState();
    }

    public void UpdateEmptyState()
    {
        if (_emptyState is null) return;
        var empty = _queue.Jobs.Count == 0 && string.IsNullOrWhiteSpace(_searchBox?.Text);
        _emptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowToast(string message)
    {
        _toastText.Text = message;
        _toast.Visibility = Visibility.Visible;
        _toast.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    public void FadeToast(double to)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(300));
        if (to <= 0)
            anim.Completed += (_, _) => _toast.Visibility = Visibility.Collapsed;
        _toast.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// <summary>Mã lỗi → tên nút retry (null = không retry được). MainWindow đọc Button.Tag để quyết hành động.</summary>
    internal static string? RetryButtonLabel(string? code)
    {
        if (code is null) return null;
        return code switch
        {
            ErrorCodes.SpoolerFailed => L10n.S(Keys.Main.BannerRetryButton),
            ErrorCodes.PrinterNotFound => L10n.S(Keys.Main.BannerRetryButton),
            _ => null,
        };
    }

    public void ShowBanner(string? code, string message, string detail)
    {
        _errorBannerText.Text = detail.Length > 0
            ? L10n.F(Keys.Main.BannerErrorFormat, message, detail)
            : message;
        _retryBtn.Content = null;
        _retryBtn.Visibility = Visibility.Collapsed;

        // Cấu hình nút retry theo mã lỗi — Tag chứa code để MainWindow quyết hành động
        var label = RetryButtonLabel(code);
        if (label is not null)
        {
            _retryBtn.Content = label;
            _retryBtn.Tag = code;
            _retryBtn.Visibility = Visibility.Visible;
        }

        if (code is null || WarningBannerCodes.Contains(code))
            ResetBannerToWarn();
        else
            SetBannerToError();

        _errorBanner.Visibility = Visibility.Visible;
    }

    private void ResetBannerToWarn()
    {
        if (TryFindResource("WarnBgBrush") is Brush bg) _errorBanner.Background = bg;
        _errorBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(0xEA, 0xB3, 0x08));
        _errorBannerText.Foreground = new SolidColorBrush(Color.FromRgb(0x92, 0x40, 0x0E));
        if (_errorBannerIcon is not null) _errorBannerIcon.Foreground = TryFindResource("WarnBrush") as Brush;
    }

    private void SetBannerToError()
    {
        if (TryFindResource("ErrorBgBrush") is Brush bg) _errorBanner.Background = bg;
        if (TryFindResource("ErrorBrush") is Brush err)
        {
            _errorBanner.BorderBrush = err;
            _errorBannerText.Foreground = err;
            if (_errorBannerIcon is not null) _errorBannerIcon.Foreground = err;
        }
    }

    public void HideBanner() => _errorBanner.Visibility = Visibility.Collapsed;

    public void UpdateNotificationBadge()
    {
        var unread = _notifications.Count(n => !n.Read);
        _bellBadge.Text = unread.ToString();
        _bellBadgeBorder.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        _notifEmptyText.Visibility = _notifications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Brush? TryFindResource(string key)
    {
        try
        {
            if (System.Windows.Application.Current?.TryFindResource(key) is Brush b)
                return b;
        }
        catch { }
        return null;
    }
}
