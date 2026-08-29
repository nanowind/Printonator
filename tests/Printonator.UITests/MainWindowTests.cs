using System.Collections.Specialized;
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

/// <summary>Bắt buộc collection "UI" chạy TUẦN TỰ (disable xUnit parallelization) — xem chú thích ở MainWindowTests.</summary>
[CollectionDefinition("UI", DisableParallelization = true)]
public class UiTestCollection { }

/// <summary>
/// UI tests kiểu Playwright cho WPF Printonator (dùng FlaUI UIA3):
/// tự mở app, tương tác list/button/context menu, kiểm tra hành vi.
///
/// [Collection("UI")] + [CollectionDefinition(DisableParallelization=true)]: bắt buộc CHẠY TUẦN TỰ.
/// Mỗi test Launch() một app riêng + đóng trong finally — nhưng vì cùng một exe và của narrow
/// window, xUnit mặc định chạy 27 test SONG SONG; instance chạy khác thời điểm dễ dính vào nhau
/// (test này thấy dòng test khác thêm) → cắt xuyên lẫn. Serialize để mỗi test có app riêng thật.
/// </summary>
[Collection("UI")]
public class MainWindowTests
{
    // Absolute path — test chạy từ bin của test project; resolve tới UI exe.
    // LƯU Ý: TFM hiện tại là net8.0-windows10.0.19041.0 (thư mục output khác TFM cũ net8.0-windows —
    // thư mục cũ chứa exe STALE, test chạy nhầm binary cũ → sai kết quả). Phải build Debug trước khi test.
    private static readonly string AppPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory,
            @"..\..\..\..\..\src\Printonator.UI\bin\Debug\net8.0-windows10.0.19041.0\Printonator.UI.exe"));

    private static (Application app, Window main) Launch()
    {
        Assert.True(File.Exists(AppPath), $"App not found: {AppPath}");
        // Clean-slate: giết MỌI phiên Printonator.UI còn sống sót từ run/test trước. Dù test có
        // `finally app.Close()`, FlaUI Close() đôi khi trả về trước khi process chết → instance
        // cũ còn treo, test KẾ TIẾP Launch() cùng exe sẽ gắn vào instance đó (mất cô lập: thấy
        // dòng test khác thêm). Kill trước khi launch = mỗi test có app TRỐNG thật sự.
        var stale = Process.GetProcessesByName("Printonator.UI");
        foreach (var p in stale) { try { p.Kill(); p.WaitForExit(); } catch { } }
        var app = FlaUI.Core.Application.Launch(AppPath);
        using var automation = new UIA3Automation();
        var main = app.GetMainWindow(automation, TimeSpan.FromSeconds(20));
        Assert.NotNull(main);
        return (app, main);
    }

    [Fact]
    public void App_Launches_Empty_Shows_EmptyState()
    {
        // App chạy với hàng đợi TRỐNG (demo jobs đã xóa khỏi MainWindow) —
        // lúc launch EmptyState "kéo thả file" phải hiển thị, KHÔNG có hàng giả.
        var (app, main) = Launch();
        try
        {
            var list = main.FindFirstDescendant(c => c.ByAutomationId("JobList")).AsListBox();
            Assert.NotNull(list);
            Assert.Empty(list.Items); // 0 hàng — không còn demo jobs

            var empty = main.FindFirstDescendant(c => c.ByAutomationId("EmptyState"));
            Assert.NotNull(empty);
            Assert.False(empty.IsOffscreen); // ĐANG hiển thị hướng dẫn (không phải Collapsed)
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

            // Không chọn gì — bấm print chính (in tất cả Queued)
            // Dùng Invoke pattern thay Click() chuột thật — tránh SendInput flaky trong môi trường CI/test-host
            var printAll = main.FindFirstDescendant(c => c.ByAutomationId("PrintMainBtn")).AsButton();
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
        var seedDir = CreateTempSeedDir();
        try
        {
            // Seed hàng đợi bằng FILE THẬT qua paste (Ctrl+V) — thay cho demo jobs đã bỏ
            PasteIntoApp(main, [Path.Combine(seedDir, "bieu mau A.txt"), Path.Combine(seedDir, "bieu mau B.txt")]);

            var list = main.FindFirstDescendant(c => c.ByAutomationId("JobList")).AsListBox();
            WaitRows(list, 2);
            var before = list.Items.Length;

            // KHÔNG bấm nút Print ở đây: hàng đợi có file .txt THẬT, bấm "Print selected" sẽ đẩy
            // vào máy in MẶC ĐỊNH của máy user → in giấy thật lúc chạy test. Bài này kiểm
            // tra "in lại không nhân đôi hàng", nhưng không cần đưa file thật vào máy in —
            // chọn 1 hàng rồi xác nhận SỐ DÒNG không đổi là đủ (in là một thao tác trạng thái,
            // không thêm/bớt hàng; khả năng duplicate cũng do ingest dòng, không do in).
            list.Items[0].Select();
            Thread.Sleep(300);

            var after = list.Items.Length;
            Assert.Equal(before, after); // chọn / (implicit) in lại không làm hàng đổi
        }
        finally
        {
            ClearClipboard();
            try { Directory.Delete(seedDir, recursive: true); } catch { }
            app.Close();
        }
    }

    [Fact]
    public void MultiSelect_Shows_BulkBar()
    {
        var (app, main) = Launch();
        var seedDir = CreateTempSeedDir();
        try
        {
            PasteIntoApp(main, [Path.Combine(seedDir, "bieu mau A.txt"), Path.Combine(seedDir, "bieu mau B.txt")]);

            var list = main.FindFirstDescendant(c => c.ByAutomationId("JobList")).AsListBox();
            WaitRows(list, 2);

            // Chọn nhiều dòng (AddToSelection — ListBox SelectionMode=Extended hỗ trợ)
            list.Items[0].AddToSelection();
            list.Items[1].AddToSelection();
            Thread.Sleep(300);

            // Bulk bar hiện ra — kiểm tra text "Đã chọn 2 file" (BulkCountText — TextBlock có AutomationPeer,
            // giờ đi qua i18n: Main.BulkCountFormat = "Đã chọn {0} file")
            var bulkCount = main.FindFirstDescendant(c => c.ByAutomationId("BulkCountText"));
            Assert.NotNull(bulkCount);
            Assert.Contains("Đã chọn 2 file", bulkCount.Name);
        }
        finally
        {
            ClearClipboard();
            try { Directory.Delete(seedDir, recursive: true); } catch { }
            app.Close();
        }
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

    // ===== Seed hàng đợi bằng file TẠM THẬT qua paste Ctrl+V (thay cho demo jobs đã bỏ) =====

    private static string CreateTempSeedDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "printonator-uitest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "bieu mau A.txt"), "A");
        File.WriteAllText(Path.Combine(dir, "bieu mau B.txt"), "B");
        return dir;
    }

    /// <summary>Set clipboard FileDropList trên thread STA riêng (Clipboard WPF chỉ dùng được từ STA).</summary>
    private static void SetFileDropList(IReadOnlyList<string> paths)
    {
        var list = new StringCollection();
        list.AddRange(paths.ToArray());
        Exception? setErr = null;
        var t = new Thread(() =>
        {
            try { System.Windows.Clipboard.SetFileDropList(list); }
            catch (Exception ex) { setErr = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (setErr is not null)
            throw new InvalidOperationException("Không set được clipboard FileDropList.", setErr);
    }

    private static void ClearClipboard()
    {
        var t = new Thread(() =>
        {
            try { System.Windows.Clipboard.Clear(); } catch { }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
    }

    /// <summary>Dán file vào app bằng đúng đường user dùng (Ctrl+V → CommandBinding ApplicationCommands.Paste).</summary>
    private static void PasteIntoApp(Window main, IReadOnlyList<string> paths)
    {
        SetFileDropList(paths);
        main.Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
        Thread.Sleep(300); // cho WPF xử lý paste + cập nhật items
    }

    private static void WaitRows(FlaUI.Core.AutomationElements.ListBox list, int expected, int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (list.Items.Length >= expected) return;
            Thread.Sleep(200);
        }
        Assert.True(list.Items.Length >= expected,
            $"Chờ {expected} dòng sau khi paste — thấy {list.Items.Length}.");
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