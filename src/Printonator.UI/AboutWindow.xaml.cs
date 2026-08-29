using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Microsoft.Win32;
using Printonator.UI.Localization;

namespace Printonator.UI;

/// <summary>
/// Cửa sổ thông tin (nút Info ở footer góc phải): version, changelog, license, liên hệ.
/// ▲ changelog đọc từ GitHub Releases để luôn mới.
/// </summary>
public partial class AboutWindow : Window
{
    private AboutWindow()
    {
        InitializeComponent();
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = L10n.F(Keys.About.VersionText, v is null ? "1.0.0" : v.ToString(3));
        LoadLanguages();
    }

    /// <summary>Nạp danh sách ngôn ngữ vào combo — hiển thị tên BẢN NGỮ (Tiếng Việt / English / 中文 / Русский / 日本語).</summary>
    private void LoadLanguages()
    {
        var items = new ObservableCollection<ComboBoxItem>
        {
            Item("vi-VN", "Tiếng Việt"),
            Item("en-US", "English"),
            Item("zh-CN", "中文 (简体)"),
            Item("ru-RU", "Русский"),
            Item("ja-JP", "日本語"),
        };
        LanguageCombo.ItemsSource = items;

        // Chọn ngôn ngữ hiện tại (đọc từ CultureResolver — đã resolve lúc startup)
        var cur = L10n.CurrentCulture.Name;
        foreach (var it in items)
            if (it.Tag is string tag && tag.Equals(cur, StringComparison.OrdinalIgnoreCase))
            { LanguageCombo.SelectedItem = it; break; }
    }

    private static ComboBoxItem Item(object tag, string display) => new() { Content = display, Tag = tag };

    /// <summary>Đổi ngôn ngữ → ghi registry + nhắc khởi động lại app để áp dụng.</summary>
    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.SelectedItem is not ComboBoxItem { Tag: string cultureTag }) return;
        if (cultureTag.Equals(L10n.CurrentCulture.Name, StringComparison.OrdinalIgnoreCase)) return; // chọn cùng ngôn ngữ

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(CultureResolver.RegistrySubKey);
            key?.SetValue(CultureResolver.RegistryValueName, cultureTag, RegistryValueKind.String);
        }
        catch { /* không ghi được registry — ngôn ngữ đổi lần này KHÔNG có tác dụng, hiện msg */ }

        var ask = MessageBox.Show(S("About.LanguageRestartPrompt"),
                                  S("About.LanguageRestartTitle"),
                                  MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (ask == MessageBoxResult.Yes)
        {
            try
            {
                System.Diagnostics.Process.Start(System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName);
            }
            catch { }
            Application.Current.Shutdown();
        }
    }

    /// <summary>Chuỗi theo ngôn ngữ hiện tại (dùng tạm trong About cho hộp thoại restart — catalog đã có key).</summary>
    private static string S(string key) => L10n.S(key);

    /// <summary>Mở cửa sổ info có chủ (main window) — modal.</summary>
    public static void Show(Window owner)
    {
        var dlg = new AboutWindow { Owner = owner };
        _ = dlg.PopulateChangeLogAsync();
        dlg.ShowDialog();
    }

    /// <summary>Tải changelog từ GitHub Releases. LUÔN đặt kết quả rõ ràng (không để "Đang tải…" mãi).</summary>
    private async Task PopulateChangeLogAsync()
    {
        var fallbackMessage = L10n.S(Keys.About.ChangeLogFail);   // mặc định nếu không có release/dữ liệu
        try
        {
            var url = "https://api.github.com/repos/nanowind/Printonator/releases?per_page=1";
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            req.Headers.Add("User-Agent", "Printonator");
            req.Headers.Add("Accept", "application/vnd.github+json");
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                ChangeLogText.Text = L10n.S(Keys.About.ChangeLogNetworkFail);
                return;
            }
            var json = await resp.Content.ReadAsStringAsync();

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var lines = new List<string>();
            // Chỉ hiện changelog BẢN MỚI NHẤT (release đầu tiên) — nhiều phiên bản gộp lại dài ngoằng
            var latest = doc.RootElement.EnumerateArray().FirstOrDefault();
            if (latest.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var tag = latest.TryGetProperty("tag_name", out var t) ? t.GetString() : "";
                var body = latest.TryGetProperty("body", out var b) ? b.GetString() : "";
                lines.Add(L10n.F(Keys.About.ChangeLogLatest, tag));
                if (!string.IsNullOrWhiteSpace(body))
                    foreach (var l in body.Split('\n').Where(x => !string.IsNullOrWhiteSpace(x)))
                        lines.Add($"   {l.Trim()}");
            }

            if (lines.Count == 0)
            {
                // Không có release nào -> hiện default rõ ràng
                ChangeLogText.Text = fallbackMessage;
                return;
            }
            ChangeLogText.Text = string.Join("\n", lines).TrimEnd();
        }
        catch
        {
            ChangeLogText.Text = fallbackMessage;
        }
    }

    private void Hyperlink_Navigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
        e.Handled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}