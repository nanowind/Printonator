using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Navigation;
using Printonator.Core.Models;

namespace Printonator.UI;

/// <summary>
/// Cửa sổ báo in xong cả lô — thay cho hành vi tự xóa âm thầm: đưa lựa chọn "Xóa file đã in
/// khỏi hàng đợi" (mặc định bật) cho user quyết, đồng thời hiển thị ngắn gọn thông tin sản phẩm,
/// bản quyền/lisence và liên hệ — để người dùng luôn biết về app và có thể rate/liên hệ.
/// </summary>
public partial class PrintDoneWindow : Window
{
    public bool RemoveDone { get; private set; } = true;

    private PrintDoneWindow()
    {
        InitializeComponent();
    }

    /// <summary>Mở popup "đã in xong" — trả về true nếu user muốn xóa các file đã in khỏi hàng đợi.</summary>
    public static bool Show(Window owner, int done, IReadOnlyList<PrintJob> success, string version)
    {
        var dlg = new PrintDoneWindow { Owner = owner };
        dlg.DoneText.Text = $"Đã in xong {done} file thành công.";
        dlg.VersionText.Text = $"Bản {version}";
        if (done == 0) dlg.DoneText.Text = "Lô in hoàn tất (không có file nào thành công mới).";
        dlg.ShowDialog();
        return dlg.RemoveDone;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        RemoveDone = RemoveDoneChk.IsChecked == true;
        DialogResult = true;
        Close();
    }

    private void Hyperlink_Navigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch { }
        e.Handled = true;
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);
        UpdateStatusText.Text = "Đang kiểm tra…";
        UpdateStatusText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        var info = await new UpdateChecker(current).CheckAsync(CancellationToken.None);
        if (info is null)
        {
            UpdateStatusText.Text = "Bạn đang dùng bản mới nhất.";
            return;
        }
        var ask = MessageBox.Show(
            $"Có bản mới {info.Version} (bạn đang dùng {current}).\n\n{info.Notes}\n\nTải và cài ngay?",
            "Printonator — Bản cập nhật", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (ask != MessageBoxResult.Yes || string.IsNullOrEmpty(info.InstallerUrl)) return;

        // Dùng đường chung tiện (UpdateInfo.Download/Verify/Launch) — KHÔNG cài im lặng,
        // user tự xác nhận từng bước của trình cài (GUI).
        UpdateStatusText.Text = "Đang tải bản cập nhật…";
        var installerPath = await info.DownloadAsync(CancellationToken.None);
        if (installerPath is null)
        {
            UpdateStatusText.Text = "Không tải được bản cập nhật. Thử lại.";
            return;
        }
        if (!await info.VerifySha256Async(installerPath, info.InstallerSha256))
        {
            MessageBox.Show("Bản tải về không khớp checksum — bị lỗi/không tin cậy. Đã hủy cài đặt.",
                            "Printonator", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!info.LaunchInstaller(installerPath))
            MessageBox.Show("Không khởi động được trình cài đặt.", "Printonator",
                            MessageBoxButton.OK, MessageBoxImage.Error);
    }

}