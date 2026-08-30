using System.Windows;
using System.Windows.Controls;
using Printonator.UI.Localization;

namespace Printonator.UI;

/// <summary>
/// Cửa sổ quản lý thư mục theo dõi (T2.6): xem danh sách, thêm, xóa, đặt cờ tự in.
/// Mọi thay đổi (thêm/xóa/đổi autoPrint) tác động ngay lên WatchFolderService.
/// Khi đóng cửa sổ → lưu snapshot vào watch.json.
/// </summary>
public partial class WatchFolderWindow : Window
{
    private readonly WatchFolderService _service;
    private readonly Dictionary<string, bool> _items;   // folder → autoPrint (hiển thị trong ListBox)

    public WatchFolderWindow(WatchFolderService service)
    {
        _service = service;
        InitializeComponent();
        _items = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        _items.Clear();
        foreach (var kv in _service.Snapshot())
            _items[kv.Key] = kv.Value;

        // ListBox hiển thị cặp KeyValuePair<string,bool> — template chỉ show Key (đường dẫn)
        WatchList.ItemsSource = _items.ToList();
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SyncButtons();
    }

    private KeyValuePair<string, bool>? Current
    {
        get
        {
            if (WatchList.SelectedItem is KeyValuePair<string, bool> kv) return kv;
            return null;
        }
    }

    private void SyncButtons()
    {
        var hasSelection = Current is not null;
        RemoveBtn.IsEnabled = hasSelection;
        if (hasSelection)
            AutoPrintCheck.IsChecked = Current.Value.Value;
        else
            AutoPrintCheck.IsChecked = false;
    }

    private void WatchList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => SyncButtons();

    private void AutoPrint_Changed(object sender, RoutedEventArgs e)
    {
        var sel = Current;
        if (sel is null) return;
        var autoPrint = AutoPrintCheck.IsChecked == true;
        _service.StartWatch(sel.Value.Key, autoPrint);
        _items[sel.Value.Key] = autoPrint;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = L10n.S(Keys.Watch.AddButton),
            Multiselect = false,
        };
        if (dlg.ShowDialog(this) != true) return;
        var folder = dlg.FolderName;
        if (string.IsNullOrWhiteSpace(folder)) return;
        var full = System.IO.Path.GetFullPath(folder);

        if (_items.ContainsKey(full))
        {
            // Đã có → chọn lại trong danh sách
            var existing = _items.First(kv => kv.Key.Equals(full, StringComparison.OrdinalIgnoreCase));
            WatchList.SelectedItem = existing;
            return;
        }

        var autoPrint = AutoPrintCheck.IsChecked == true;
        _service.StartWatch(full, autoPrint);
        _items[full] = autoPrint;
        WatchList.ItemsSource = _items.ToList();
        EmptyText.Visibility = Visibility.Collapsed;
        WatchList.SelectedItem = _items.First(kv => kv.Key.Equals(full, StringComparison.OrdinalIgnoreCase));
        SyncButtons();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var sel = Current;
        if (sel is null) return;
        _service.StopWatch(sel.Value.Key);
        _items.Remove(sel.Value.Key);
        WatchList.ItemsSource = _items.ToList();
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SyncButtons();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        // Lưu snapshot trước khi đóng — gọi im lặng (không làm hỏng đóng cửa sổ)
        try { WatchFolderService.SaveWatches(WatchFolderService.FilePath, _service.Snapshot()); } catch { }
        DialogResult = true;
        Close();
    }
}