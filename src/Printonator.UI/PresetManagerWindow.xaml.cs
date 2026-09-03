using System.Windows;
using Printonator.Core.Models;
using Printonator.Core.Presets;
using Printonator.UI.Localization;

namespace Printonator.UI;

/// <summary>
/// Quản lý preset (cấu hình in có tên): danh sách + đổi tên + xóa + áp dụng.
/// "Áp dụng" đóng cửa sổ và trả preset qua <see cref="SelectedPreset"/> để caller dùng.
/// </summary>
public partial class PresetManagerWindow : Window
{
    private readonly PresetStore _store = new();
    private List<Preset> _presets = [];

    /// <summary>Preset người dùng chọn "Áp dụng" — null nếu đóng cửa sổ không áp dụng.</summary>
    public Preset? SelectedPreset { get; private set; }

    /// <summary>User bấm "No profile" — muốn BỎ preset, về cấu hình mặc định (không chọn preset nào).</summary>
    public bool ClearProfile { get; private set; }

    public PresetManagerWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        _presets = _store.Load().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        PresetList.ItemsSource = _presets;
        EmptyText.Visibility = _presets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NameBox.Clear();
        SyncButtons();
    }

    private Preset? Current => PresetList.SelectedItem as Preset;

    private void SyncButtons()
    {
        var hasSelection = Current is not null;
        RenameBtn.IsEnabled = hasSelection;
        DeleteBtn.IsEnabled = hasSelection;
        ApplyBtn.IsEnabled = hasSelection;

        // Sau khi đổi/xóa → ra list, text box mất context → đặt lại tên mặc định cho kế tiếp
        if (hasSelection && string.IsNullOrWhiteSpace(NameBox.Text))
            NameBox.Text = Current!.Name;
        if (!hasSelection)
            NameBox.Clear();
    }

    private void PresetList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => SyncButtons();

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        var preset = Current;
        if (preset is null) { ShowStatus(L10n.S(Keys.Preset.NoneSelected)); return; }

        var newName = NameBox.Text.Trim();
        if (newName.Length == 0) return;
        if (newName.Equals(preset.Name, StringComparison.OrdinalIgnoreCase)) return;

        // PresetStore upsert theo tên → đổi tên trùng 1 preset KHÁC sẽ GHI ĐÈ preset đó → chặn trước.
        if (_presets.Any(p => !p.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase)
                              && p.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
        {
            ShowStatus(L10n.S(Keys.Settings.ProfileSaveError));
            return;
        }

        // PresetStore chỉ upsert theo tên → rename = save bản mới (đè tên mới) + xóa bản cũ.
        var renamed = preset with { Name = newName };
        if (!_store.Save(renamed)) return;
        _store.Delete(preset.Name);

        _presets = _store.Load().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        PresetList.ItemsSource = _presets;
        // Reload từ đĩa sinh object mới → tìm lại theo tên (not reference equality)
        PresetList.SelectedItem = _presets.FirstOrDefault(p => p.Name.Equals(newName, StringComparison.OrdinalIgnoreCase));
        if (PresetList.SelectedItem is null) SyncButtons();
        ShowStatus(L10n.S(Keys.Preset.Renamed));
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var preset = Current;
        if (preset is null) { ShowStatus(L10n.S(Keys.Preset.NoneSelected)); return; }

        _store.Delete(preset.Name);
        Reload();
        ShowStatus(L10n.S(Keys.Preset.Deleted));
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var preset = Current;
        if (preset is null) { ShowStatus(L10n.S(Keys.Preset.NoneSelected)); return; }

        SelectedPreset = preset;
        DialogResult = true;
        Close();
    }

    /// <summary>"No profile": bỏ preset đang áp — về cấu hình mặc định (ClearProfile=true để MainWindow reset).</summary>
    private void NoProfile_Click(object sender, RoutedEventArgs e)
    {
        ClearProfile = true;
        DialogResult = true;
        Close();
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Foreground = (System.Windows.Media.Brush?)TryFindResource("ReadyBrush") ?? StatusText.Foreground;
    }
}