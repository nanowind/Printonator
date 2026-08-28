using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

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
        VersionText.Text = v is null ? "Bản 1.0.0" : $"Bản {v.ToString(3)}";
    }

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
        string? fallbackMessage = "Bạn đang dùng bản mới nhất.";   // mặc định nếu không có release/dữ liệu
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
                ChangeLogText.Text = "Không tải được lịch sử (lỗi mạng). Bạn đang dùng bản mới nhất.";
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
                lines.Add($"◆ {tag}");
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
            ChangeLogText.Text = "Không tải được lịch sử thay đổi. Bạn đang dùng bản mới nhất.";
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