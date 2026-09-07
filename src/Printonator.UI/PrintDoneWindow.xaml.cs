using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Navigation;
using Printonator.Core.Models;
using Printonator.UI.Localization;

namespace Printonator.UI;

/// <summary>
/// Cửa sổ báo in xong cả lô — thay cho hành vi tự xóa âm thầm: đưa lựa chọn "Xóa file đã in
/// khỏi hàng đợi" (mặc định bật) cho user quyết, đồng thời hiển thị ngắn gọn thông tin sản phẩm,
/// bản quyền/lisence và liên hệ — để người dùng luôn biết về app và có thể rate/liên hệ.
/// </summary>
public partial class PrintDoneWindow : Window
{
    public bool RemoveDone { get; private set; } = true;

    // Đóng bằng nút X (title bar) ≠ bấm OK: KHÔNG xóa file đã in (giữ an toàn — không mất job
    // khỏi queue khi user chưa xác nhận). Ok_Click set DialogResult=true → không vào đây.
    private bool _closedByOk;

    private PrintDoneWindow()
    {
        InitializeComponent();
        Closing += (_, e) =>
        {
            if (!_closedByOk)
            {
                RemoveDone = false;   // đóng bằng X → giữ file trong queue
                RemoveDoneChk.IsChecked = false; // phản ánh đúng hành vi (không xóa)
            }
        };
    }

    /// <summary>Mở popup "đã in xong" — trả về true nếu user muốn xóa các file đã in khỏi hàng đợi.</summary>
    public static bool Show(Window owner, int done, IReadOnlyList<PrintJob> success, string version)
    {
        var dlg = new PrintDoneWindow { Owner = owner };
        dlg.DoneText.Text = L10n.F(Keys.Done.DoneText, done);
        dlg.VersionText.Text = L10n.F(Keys.Done.VersionText, version);
        if (done == 0) dlg.DoneText.Text = L10n.S(Keys.Done.DoneTextEmpty);
        dlg.ShowDialog();
        return dlg.RemoveDone;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _closedByOk = true;
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
        UpdateStatusText.Text = L10n.S(Keys.Done.Checking);
        UpdateStatusText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        var info = await new UpdateChecker(current).CheckAsync(CancellationToken.None);
        if (info is null)
        {
            UpdateStatusText.Text = L10n.S(Keys.Done.Latest);
            return;
        }
        var ask = MessageBox.Show(
            L10n.F(Keys.Done.UpdateConfirm, info.Version, current, info.Notes),
            L10n.S(Keys.Done.UpdateConfirmTitle), MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (ask != MessageBoxResult.Yes || string.IsNullOrEmpty(info.InstallerUrl)) return;

        // Dùng đường chung tiện (UpdateInfo.Download/Verify/Launch) — KHÔNG cài im lặng,
        // user tự xác nhận từng bước của trình cài (GUI).
        UpdateStatusText.Text = L10n.S(Keys.Done.Downloading);
        var installerPath = await info.DownloadAsync(CancellationToken.None);
        if (installerPath is null)
        {
            UpdateStatusText.Text = L10n.S(Keys.Done.DownloadFailed);
            return;
        }
        if (!await info.VerifySha256Async(installerPath, info.InstallerSha256))
        {
            MessageBox.Show(L10n.S(Keys.Done.ChecksumFail),
                            "Printonator", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!info.LaunchInstaller(installerPath))
            MessageBox.Show(L10n.S(Keys.Done.LaunchFail), "Printonator",
                            MessageBoxButton.OK, MessageBoxImage.Error);
    }

}