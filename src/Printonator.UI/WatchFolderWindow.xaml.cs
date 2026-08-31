using System.Windows;
using System.Windows.Media;
using Printonator.UI.Localization;

namespace Printonator.UI;

/// <summary>
/// Cửa sổ theo dõi thư mục (T2.6): chọn 1 folder làm printing server — mọi file mới
/// thả vào được tự động đưa vào hàng đợi in. Duy nhất 1 folder đang watch.
/// Khi đóng cửa sổ → lưu snapshot (folder + enabled) vào watch.json.
/// </summary>
public partial class WatchFolderWindow : Window
{
    private readonly WatchFolderService _service;

    private static readonly Brush ActiveBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush InactiveBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Brush WarningBrush = new SolidColorBrush(Color.FromRgb(0xCA, 0x8A, 0x04));

    public WatchFolderWindow(WatchFolderService service)
    {
        _service = service;
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        var folder = _service.Folder;
        var watching = _service.IsWatching;
        var failed = _service.WatcherFailed;

        if (string.IsNullOrWhiteSpace(folder))
        {
            CurrentFolderText.Text = L10n.S(Keys.Watch.NoFolder);
            CurrentFolderText.ToolTip = null;
        }
        else
        {
            CurrentFolderText.Text = folder;
            CurrentFolderText.ToolTip = folder;
        }

        if (watching)
        {
            StatusText.Text = L10n.S(Keys.Watch.StatusActive);
            StatusText.Foreground = ActiveBrush;
        }
        else if (failed)
        {
            // Folder đã chọn nhưng watcher KHÔNG mở được (mất quyền/chưa tồn tại, retry cạn) — báo vàng.
            StatusText.Text = L10n.S(Keys.Watch.StatusWarning);
            StatusText.Foreground = WarningBrush;
        }
        else
        {
            StatusText.Text = L10n.S(Keys.Watch.StatusInactive);
            StatusText.Foreground = InactiveBrush;
        }
        StopWatchBtn.IsEnabled = watching;
    }

    private void SelectFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = L10n.S(Keys.Watch.SelectFolder),
            Multiselect = false,
        };
        if (dlg.ShowDialog(this) != true) return;
        var folder = dlg.FolderName;
        if (string.IsNullOrWhiteSpace(folder)) return;
        var full = System.IO.Path.GetFullPath(folder);

        _service.StartWatch(full);
        Reload();
    }

    private void StopWatch_Click(object sender, RoutedEventArgs e)
    {
        _service.StopWatch();
        Reload();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        // Lưu snapshot trước khi đóng — gọi im lặng (không làm hỏng đóng cửa sổ)
        try
        {
            WatchFolderService.SaveConfig(WatchFolderService.FilePath,
                new WatchFolderService.WatchConfig { Folder = _service.Folder, Enabled = _service.IsConfigured });
        }
        catch { }
        DialogResult = true;
        Close();
    }
}