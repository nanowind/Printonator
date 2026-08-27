using System.Windows;
using Printonator.Core.Models;

namespace Printonator.UI;

/// <summary>
/// Hộp thoại xác nhận trước khi in HÀNG LOẠT — hiện tổng tờ ước tính + máy in + lưu ý
/// (trường hợp ApplySelectedPrinter sẽ ghi đè máy in của mọi job). Người dùng In/Hủy.
/// Hẹn giờ: chỉ HIỆN khi ước tính vượt ngưỡng (xem caller) — tránh làm chậm lô nhỏ.
/// </summary>
public partial class PrintConfirmWindow : Window
{
    public bool Confirmed { get; private set; }

    private PrintConfirmWindow()
    {
        InitializeComponent();
    }

    /// <summary>Ước tính tổng số TỜ in (page×copies, chia cho PagesPerSheet). "Ước tính" vì một số loại
    /// file (Office) chưa có page count chính xác — chỉ đếm trang đã nắm được.</summary>
    public static int EstimateSheets(IEnumerable<PrintJob> jobs)
    {
        var total = 0;
        foreach (var j in jobs)
        {
            var pages = j.ResolvePhysicalPages();
            var perCopy = pages.IsSuccess ? Math.Max(pages.Value!.Length, 1) : Math.Max(j.PageCount, 1);
            var copies = Math.Max(j.Config.Copies, 1);
            var perSheet = Math.Max(j.Config.PagesPerSheet, 1);
            var sheets = (int)Math.Ceiling((double)perCopy * copies / perSheet);
            total += sheets;
        }
        return total;
    }

    /// <summary>Mở hộp thoại; trả về true khi người dùng bấm "In".</summary>
    public static bool Show(Window owner, string printer, IReadOnlyList<PrintJob> jobs, int sheets)
    {
        var dlg = new PrintConfirmWindow { Owner = owner };
        dlg.BodyText.Text =
            $"Có {jobs.Count} file sẵn sàng in.\n\n" +
            $"Tổng cộng khoảng {sheets:N0} tờ giấy ước tính (theo số bản và trang đã cấu hình).";
        dlg.PrinterNote.Text =
            $"Máy in: {printer}\nÁp dụng máy in này cho tất cả {jobs.Count} file.";
        dlg.ShowDialog();
        return dlg.Confirmed;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
        Close();
    }
}