using System.Windows;
using System.Windows.Controls;
using Printonator.Core.Models;

namespace Printonator.UI;

/// <summary>Dialog nhập page range — hỗ trợ section cho DOCX (S2:1-3 = section 2 trang 1-3).</summary>
public partial class PageRangeDialog : Window
{
    private readonly PrintJob _job;

    public string PageRange { get; private set; } = "All";

    public PageRangeDialog(PrintJob job)
    {
        _job = job;
        InitializeComponent();
        Title = $"Chọn trang — {job.FileName}";
        FileNameText.Text = job.FileName;
        PageRangeBox.Text = job.Config.PageRange;

        if (job.Sections.Count > 0)
        {
            SectionInfo.Text = string.Join("  ·  ",
                job.Sections.Select(s => $"S{s.Index}: doc {s.FirstPhysicalPage}-{s.LastPhysicalPage}"));
            SectionInfo.Visibility = Visibility.Visible;
        }

        UpdatePreview();
    }

    /// <summary>
    /// Preview trực tiếp: "→ Will print physical pages X-Y" (Penpot gap).
    /// Resolve bằng cách tạm áp range vào job rồi khôi phục để Cancel không đổi cấu hình.
    /// </summary>
    private void PageRangeBox_TextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        // TextChanged có thể fix NGAY khi XAML đang dựng (TextBox.Text="All" lúc parse) —
        // lúc đó PreviewText (khai báo sau) chưa được tạo → phải guard, không crash
        if (_job is null || PageRangeBox is null || PreviewText is null) return;

        var old = _job.Config.PageRange;
        try
        {
            _job.Config.PageRange = PageRangeBox.Text.Trim();
            var r = _job.ResolvePhysicalPages();
            if (r.IsSuccess)
            {
                var pages = r.Value!;
                var shown = pages.Length > 20 ? string.Join(",", pages.Take(20)) + $"… ({pages.Length} trang)" : string.Join(",", pages);
                PreviewText.Text = $"→ Will print physical pages: {shown}";
                PreviewText.Foreground = System.Windows.Media.Brushes.SeaGreen;
                PreviewText.Visibility = Visibility.Visible;
            }
            else
            {
                PreviewText.Text = $"✕ {r.Error!.Message}";
                PreviewText.Foreground = System.Windows.Media.Brushes.Firebrick;
                PreviewText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _job.Config.PageRange = old;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var typed = PageRangeBox.Text.Trim();
        var old = _job.Config.PageRange;
        try
        {
            _job.Config.PageRange = typed;
            var r = _job.ResolvePhysicalPages();
            if (!r.IsSuccess && r.Error is not null)
            {
                ErrorText.Text = r.Error.Message + "  " + r.Error.Hint;
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
            PageRange = typed;
            DialogResult = true;
        }
        finally
        {
            _job.Config.PageRange = old; // áp thật sự ở caller (CtxPageRange_Click / từng job)
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}