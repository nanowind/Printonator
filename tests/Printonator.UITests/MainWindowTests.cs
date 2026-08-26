using System.Diagnostics;
using System.IO;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Printonator.Core.Models;
using Printonator.UI;
using Xunit;

namespace Printonator.UITests;

/// <summary>
/// UI tests kiểu Playwright cho WPF Printonator (dùng FlaUI UIA3):
/// tự mở app, tương tác list/button/context menu, kiểm tra hành vi.
/// </summary>
public class MainWindowTests
{
    // Absolute path — test chạy từ bin của test project; resolve tới UI exe
    private static readonly string AppPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory,
            @"..\..\..\..\..\src\Printonator.UI\bin\Debug\net8.0-windows\Printonator.UI.exe"));

    private static (Application app, Window main) Launch()
    {
        Assert.True(File.Exists(AppPath), $"App not found: {AppPath}");
        var app = FlaUI.Core.Application.Launch(AppPath);
        using var automation = new UIA3Automation();
        var main = app.GetMainWindow(automation, TimeSpan.FromSeconds(20));
        Assert.NotNull(main);
        return (app, main);
    }

    [Fact]
    public void App_Launches_With_DemoJobs()
    {
        var (app, main) = Launch();
        try
        {
            var list = main.FindFirstDescendant(c => c.ByAutomationId("JobList")).AsListBox();
            Assert.NotNull(list);
            var rows = list.Items;
            Assert.True(rows.Length >= 6, $"Expected >=6 demo jobs, got {rows.Length}");
        }
        finally { app.Close(); }
    }

    [Fact]
    public void PrintAll_DoesNot_Duplicate_Rows()
    {
        var (app, main) = Launch();
        try
        {
            var list = main.FindFirstDescendant(c => c.ByAutomationId("JobList")).AsListBox();
            var before = list.Items.Length;

            // Không chọn gì — bấm Print all (in tất cả Queued)
            // Dùng Invoke pattern thay Click() chuột thật — tránh SendInput flaky trong môi trường CI/test-host
            var printAll = main.FindFirstDescendant(c => c.ByAutomationId("PrintAllBtn")).AsButton();
            printAll.Invoke();
            Thread.Sleep(2000);

            var after = list.Items.Length;
            Assert.Equal(before, after); // KHÔNG duplicate dòng
        }
        finally { app.Close(); }
    }

    [Fact]
    public void PrintSelected_DoesNot_Duplicate_Rows()
    {
        var (app, main) = Launch();
        try
        {
            var list = main.FindFirstDescendant(c => c.ByAutomationId("JobList")).AsListBox();
            var before = list.Items.Length;

            // chọn dòng đầu
            list.Items[0].Select();
            // bấm Print selected — Invoke pattern (tránh SendInput flaky)
            var printBtn = main.FindFirstDescendant(c => c.ByAutomationId("PrintSelectedBtn")).AsButton();
            printBtn.Invoke();

            // chờ vài giây cho process
            Thread.Sleep(1500);
            var after = list.Items.Length;
            Assert.Equal(before, after); // KHÔNG duplicate
        }
        finally { app.Close(); }
    }

    [Fact]
    public void MultiSelect_Shows_BulkBar()
    {
        var (app, main) = Launch();
        try
        {
            var list = main.FindFirstDescendant(c => c.ByAutomationId("JobList")).AsListBox();
            Assert.True(list.Items.Length > 0);

            // Chọn nhiều dòng (Ctrl+Click — ListBox SelectionMode=Extended hỗ trợ)
            list.Items[0].AddToSelection();
            list.Items[1].AddToSelection();
            Thread.Sleep(300);

            // Bulk bar hiện ra — kiểm tra text "2 files selected" (BulkCountText — TextBlock có AutomationPeer)
            var bulkCount = main.FindFirstDescendant(c => c.ByAutomationId("BulkCountText"));
            Assert.NotNull(bulkCount);
            Assert.Contains("2 files selected", bulkCount.Name);
        }
        finally { app.Close(); }
    }

    [Fact]
    public void Dropdown_Click_Opens_And_Selects()
    {
        var (app, main) = Launch();
        try
        {
            var combo = main.FindFirstDescendant(c => c.ByAutomationId("PrinterCombo")).AsComboBox();
            Assert.NotNull(combo);

            // CLICK CHUỘT THẬT — bắt đúng regression z-order
            // (template cũ: Chrome Border chặn click → dropdown không bao giờ mở → items rỗng)
            var rect = combo.BoundingRectangle;
            Assert.False(rect.IsEmpty, "Không lấy được vị trí combo");
            FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point(
                (int)(rect.Left + rect.Width / 2),
                (int)(rect.Top + rect.Height / 2)));

            // Dropdown mở ⇒ các item xuất hiện trong UIA tree
            var items = WaitComboItems(combo);
            Assert.True(items.Length > 0, "Click vào combo KHÔNG mở được dropdown (items rỗng)");

            // Chọn item đầu ⇒ không ném lỗi (template cho phép chọn)
            combo.Items[0].Select();
        }
        finally { app.Close(); }
    }

    [Fact]
    public void PrintSettingsWindow_Constructor_Loads()
    {
        // Smoke-test XAML + logic của cửa sổ cấu hình mới mà KHÔNG cần mouse automation:
        // dựng Thật trên STA thread với Application resources (theme) giống app chạy thật.
        Exception? err = null;
        var t = new Thread(() =>
        {
            try
            {
                var app = new Printonator.UI.App(); // khởi tạo Application.Current
                // Test host không resolve relative pack URI của App.xaml → nạp theme thủ công
                app.Resources.MergedDictionaries.Clear();
                foreach (var theme in new[] { "Colors.xaml", "Buttons.xaml", "ComboBox.xaml" })
                    app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
                    {
                        Source = new Uri($"pack://application:,,,/Printonator.UI;component/Themes/{theme}", UriKind.Absolute),
                    });
                var cfg = new PrintConfig
                {
                    Copies = 2,
                    PaperSize = "A3",
                    ColorMode = PrintColorMode.Grayscale,
                    PageRange = "1-5",
                };
                var w = new PrintSettingsWindow([], cfg, null);
                w.Measure(new System.Windows.Size(1100, 900)); // buộc layout để phát hiện lỗi binding
                w.Close();
            }
            catch (Exception ex)
            {
                err = ex;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();

        Assert.Null(err);
    }

    private static FlaUI.Core.AutomationElements.ComboBoxItem[] WaitComboItems(FlaUI.Core.AutomationElements.ComboBox combo)
    {
        for (var i = 0; i < 40; i++)
        {
            var items = combo.Items;
            if (items.Length > 0) return items;
            Thread.Sleep(200);
        }
        return combo.Items;
    }
}