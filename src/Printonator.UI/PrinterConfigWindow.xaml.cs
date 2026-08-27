using System.Windows;
using Printonator.Core.Models;
using Printonator.Spool.Printing;

namespace Printonator.UI;

/// <summary>
/// Màn "Printers &amp; paper setup" theo design Penpot:
/// danh sách máy in + trạng thái + khổ giấy + khả năng + Scan printers + cảnh báo offline.
/// </summary>
public partial class PrinterConfigWindow : Window
{
    private readonly PrinterService _service = new();
    private int _refreshGeneration;   // bấm Scan liên tục — scan cũ không ghi đè kết quả scan mới

    public PrinterConfigWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        // Quét NỀN + KHÔNG treo UI (máy in mạng bị firewall chặn có thể treo lâu) — PrinterService tự
        // giới hạn 15s bên trong; cửa sổ đóng giữa chừng → bỏ qua kết quả.
        var gen = ++_refreshGeneration;
        var r = await Task.Run(() => _service.ListPrinters());
        if (gen != _refreshGeneration || !IsLoaded) return;
        try { Dispatcher.Invoke(() => ApplyResult(r)); } catch { /* cửa sổ đóng — bỏ qua */ }
    }

    private void ApplyResult(Result<List<PrinterInfo>> r)
    {
        if (!r.IsSuccess)
        {
            OfflineBannerText.Text = r.Error!.Message + "  " + r.Error.Hint;
            OfflineBanner.Visibility = Visibility.Visible;
            PrinterList.ItemsSource = null;
            HeaderSub.Text = "Lỗi đọc máy in";
            return;
        }

        var printers = r.Value!;
        PrinterList.ItemsSource = printers;
        var offline = printers.Count(p => !p.IsAvailable);
        var available = printers.Count(p => p.IsAvailable);
        HeaderSub.Text = $"{printers.Count} máy in · {available} sẵn sàng · {offline} ngoại tuyến/lỗi";

        if (offline > 0)
        {
            OfflineBannerText.Text =
                $"Có {offline} máy in ngoại tuyến hoặc lỗi — job gửi vào các máy này sẽ KHÔNG được in. " +
                "Kiểm tra máy bật, mở cửa, đủ giấy/mực, kết nối. Bấm 'Scan printers' để nạp lại. " +
                "Máy ảo (PDF/XPS) là kênh xuất file, không in giấy.";
            OfflineBanner.Visibility = Visibility.Visible;
        }
        else
        {
            OfflineBanner.Visibility = Visibility.Collapsed;
        }
    }

    private void Scan_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void Back_Click(object sender, RoutedEventArgs e) => Close();

    // ===== Native dialogs của driver (printui.dll) — in như Print Conductor =====

    private void Prefs_Click(object sender, RoutedEventArgs e)
        => OpenNative(SafePrinter(sender), "/e", "Printing Preferences");

    private void Props_Click(object sender, RoutedEventArgs e)
        => OpenNative(SafePrinter(sender), "/p", "Printer Properties");

    private static string? SafePrinter(object sender) => (sender as FrameworkElement)?.Tag?.ToString();

    private void OpenNative(string? printerName, string arg, string label)
    {
        if (string.IsNullOrWhiteSpace(printerName)) return;

        var r = arg == "/e"
            ? PrinterDialogs.OpenPrintingPreferences(printerName)
            : PrinterDialogs.OpenPrinterProperties(printerName);

        if (!r.IsSuccess)
        {
            OfflineBannerText.Text = $"✕ {r.Error!.Message} — {r.Error.Hint}";
            OfflineBanner.Visibility = Visibility.Visible;
        }
    }
}