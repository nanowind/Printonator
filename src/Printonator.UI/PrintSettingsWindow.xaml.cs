using System.Windows;
using System.Windows.Controls;
using Printonator.Core.Models;
using Printonator.Core.Presets;
using Printonator.Spool.Printing;

namespace Printonator.UI;

/// <summary>
/// Panel cấu hình in mới (Print Conductor-style Item/Common settings):
/// profile (printer template), copies, page range All/Ranges, color mode, duplex,
/// collation, paper size, paper source (khay từ máy in), orientation, scale mode,
/// pages per sheet + nút mở dialog NATIVE của driver (Printing Preferences /
/// Printer Properties). Áp cho các file đang chọn hoặc đặt mặc định cho file mới.
/// </summary>
public partial class PrintSettingsWindow : Window
{
    private readonly IReadOnlyList<PrintJob> _targets;
    private readonly PrinterInfo? _printer;
    private readonly PrintConfig _source;
    private readonly PrintJob? _representative;
    private readonly PresetStore _store = new();
    private bool _loadingProfile;

    /// <summary>Cấu hình sau khi bấm Áp dụng — MainWindow copy vào từng job.</summary>
    public PrintConfig Result { get; private set; } = new();

    public PrintSettingsWindow(IReadOnlyList<PrintJob> targets, PrintConfig source, PrinterInfo? printer)
    {
        _targets = targets ?? [];
        _source = source ?? new PrintConfig();
        _printer = printer;
        _representative = _targets.FirstOrDefault(j => j.Sections.Count > 0) ?? _targets.FirstOrDefault();

        InitializeComponent();

        Title = _targets.Count == 1 ? $"Cấu hình in — {_targets[0].FileName}" : $"Cấu hình in — {_targets.Count} file";
        TitleText.Text = _targets.Count == 1
            ? _targets[0].FileName
            : $"Đang cấu hình {_targets.Count} file đã chọn";
        ApplyBtn.Content = _targets.Count > 0 ? $"Áp dụng cho {_targets.Count} file" : "Đặt mặc định cho file mới";

        PopulatePrinterDrivenLists();
        LoadFromConfig(_source);
        RefreshRangePreview();
        LoadProfiles();
    }

    // ============ Nạp danh sách phụ thuộc máy in (khổ giấy + khay) ============

    private void PopulatePrinterDrivenLists()
    {
        // Khổ giấy: "theo máy in" (rỗng) → "theo tài liệu" (khổ gốc) → danh sách máy in / chuẩn
        var sizes = _printer is { SupportedPaperSizes.Length: > 0 } p
            ? PaperCatalog.FromPrinter(p.SupportedPaperSizes)
            : PaperCatalog.StandardSizes();
        PaperCombo.Items.Add(Item("Theo máy in (As in printer)", ""));
        PaperCombo.Items.Add(Item("Theo tài liệu (khổ gốc từng trang)", PaperCatalog.AsDocument));
        foreach (var s in sizes)
            PaperCombo.Items.Add(Item(s, PaperCatalog.SizeName(s)));

        // Khay giấy: "theo máy in" (rỗng) + tray thân thiện từ máy in
        PaperSourceCombo.Items.Add(Item("Theo máy in (máy tự chọn khay)", ""));
        if (_printer is { Trays.Length: > 0 } pp)
        {
            foreach (var t in pp.Trays)
                PaperSourceCombo.Items.Add(Item(t, t));
        }
        else
        {
            PaperSourceCombo.Items.Add(Item("(máy in không báo khay)", "__none__"));
            PaperSourceCombo.IsEnabled = false;
        }

        // Dòng máy in + nút native
        if (_printer is null)
        {
            PrefsBtn.IsEnabled = false;
            PropsBtn.IsEnabled = false;
        }
        else
        {
            PrinterNameText.Text = _printer.Name;
        }
    }

    private static ComboBoxItem Item(string display, object tag) => new() { Content = display, Tag = tag };

    // ============ Đổ giá trị từ cấu hình hiện tại vào form ============

    private void LoadFromConfig(PrintConfig cfg)
    {
        CopiesBox.Text = Math.Max(cfg.Copies, 1).ToString();

        var isAll = string.IsNullOrWhiteSpace(cfg.PageRange) || cfg.PageRange.Equals("All", StringComparison.OrdinalIgnoreCase);
        RangeAll.IsChecked = isAll;
        RangeCustom.IsChecked = !isAll;
        RangeBox.Text = isAll ? "All" : cfg.PageRange;

        SelectTag(ParityCombo, cfg.Parity.ToString(), "All");
        SelectTag(ColorModeCombo, cfg.ColorMode.ToString(), "AsPrinter");
        SelectTag(DuplexCombo, cfg.DuplexMode.ToString(), "AsPrinter");
        SelectTag(CollationCombo, cfg.Collation.ToString(), "AsPrinter");

        SelectTag(PaperCombo, cfg.PaperSize, "A4"); // không tìm thấy (rỗng) → giữ item "Theo máy in" index 0
        if (string.IsNullOrEmpty(cfg.PaperSize)) PaperCombo.SelectedIndex = 0;
        SelectTag(PaperSourceCombo, cfg.PaperSource ?? "", "");

        SelectTag(OrientationCombo, cfg.Orientation.ToString(), "Portrait");
        SelectTag(QualityCombo, cfg.Quality.ToString(), "AsPrinter");
        SelectTag(ScaleModeCombo, cfg.ScaleMode.ToString(), "AsDocument");
        ZoomBox.Text = cfg.ScalePercent.ToString();
        UpdateZoomState();

        if (cfg.Booklet) SelectTag(PerSheetCombo, "booklet", "1");
        else SelectTag(PerSheetCombo, Math.Max(cfg.PagesPerSheet, 1).ToString(), "1");
    }

    private static void SelectTag(ComboBox combo, string? tag, string fallbackTag)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if ((combo.Items[i] as ComboBoxItem)?.Tag?.ToString() == tag) { combo.SelectedIndex = i; return; }
        }
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if ((combo.Items[i] as ComboBoxItem)?.Tag?.ToString() == fallbackTag) { combo.SelectedIndex = i; return; }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static string? SelectedTag(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    // ============ Page range preview (như PageRangeDialog) ============

    private void Range_Changed(object sender, RoutedEventArgs e)
    {
        RangeBox.IsEnabled = RangeCustom.IsChecked == true;
        if (RangeCustom.IsChecked == true) RangeBox.Focus();
        RefreshRangePreview();
        // Ẩn/hiện lỗi cú pháp theo nội dung đang gõ (không chờ đến lúc Áp dụng)
        if (RangeErrText is not null)
        {
            var invalid = RangeCustom.IsChecked == true && !IsValidPageRangeSyntax(RangeBox.Text);
            RangeErrText.Visibility = invalid ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void Copies_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (CopiesError is null) return;
        // Lỗi CHỈ nháy khi đã có nội dung mà SAI (non-rỗng && (không parse được || ngoài 1..999)).
        // Rỗng = đang gõ dở → ẨN lỗi; ValidateCopies lúc bấm Apply vẫn chặn + hiện lỗi nếu vẫn rỗng.
        var t = CopiesBox.Text.Trim();
        var invalid = t.Length > 0 && (!int.TryParse(t, out var n) || n is < 1 or > 999);
        CopiesError.Visibility = invalid ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshRangePreview()
    {
        if (RangePreview is null || _representative is null)
        {
            if (RangePreview is not null)
            {
                if (RangeAll.IsChecked == true)
                    RangePreview.Text = "→ In toàn bộ trang của file.";
                else
                    RangePreview.Text = "→ Nhập trang: 1,3 · 2-5 · 1-2,7 · S2:1-3";
            }
            return;
        }

        var representative = _representative;
        if (representative.Sections.Count > 0 && SectionInfo is not null)
        {
            SectionInfo.Text = string.Join("  ·  ",
                representative.Sections.Select(s => $"S{s.Index}: doc {s.FirstPhysicalPage}-{s.LastPhysicalPage}"));
            SectionInfo.Visibility = Visibility.Visible;
        }

        var spec = RangeAll.IsChecked == true ? "All" : NormalizeRange(RangeBox.Text);
        var old = representative.Config.PageRange;
        try
        {
            representative.Config.PageRange = spec;
            var r = representative.ResolvePhysicalPages();
            if (r.IsSuccess)
            {
                var pages = r.Value!;
                var shown = pages.Length > 20
                    ? string.Join(",", pages.Take(20)) + $"… ({pages.Length} trang)"
                    : string.Join(",", pages);
                RangePreview.Text = $"→ Sẽ in trang: {shown}";
                RangePreview.Foreground = System.Windows.Media.Brushes.SeaGreen;
            }
            else
            {
                RangePreview.Text = $"✕ {r.Error!.Message}";
                RangePreview.Foreground = System.Windows.Media.Brushes.Firebrick;
            }
        }
        finally
        {
            representative.Config.PageRange = old;
        }
    }

    private static string NormalizeRange(string text)
    {
        var t = text?.Trim() ?? "";
        return t.Length == 0 ? "All" : t;
    }

    // ============ Scale mode: bật/tắt ZoomBox ============

    private void ScaleMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateZoomState();
    }

    private void UpdateZoomState()
    {
        var isZoom = SelectedTag(ScaleModeCombo) == "Zoom";
        ZoomBox.IsEnabled = isZoom;
        if (isZoom && (!int.TryParse(ZoomBox.Text, out var z) || z < 10))
            ZoomBox.Text = "100";
        ZoomBox.Focusable = isZoom;
    }

    // ============ Profile (printer template) ============

    private void LoadProfiles()
    {
        _loadingProfile = true;
        try
        {
            var current = SelectedTag(ProfileCombo);
            ProfileCombo.Items.Clear();
            ProfileCombo.Items.Add(Item("(Không dùng profile)", ""));
            var presets = _store.Load().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var p in presets)
                ProfileCombo.Items.Add(Item(p.Name, p.Name));

            if (current is not null)
                for (var i = 0; i < ProfileCombo.Items.Count; i++)
                    if ((ProfileCombo.Items[i] as ComboBoxItem)?.Tag?.ToString() == current) { ProfileCombo.SelectedIndex = i; return; }
            ProfileCombo.SelectedIndex = 0;
        }
        finally
        {
            _loadingProfile = false;
        }
    }

    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProfile) return;
        var name = SelectedTag(ProfileCombo);
        if (string.IsNullOrEmpty(name)) return;
        var preset = _store.Load().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (preset is not null) LoadFromConfig(preset.ToPrintConfig());
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        SavePanel.Visibility = SavePanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (SavePanel.Visibility == Visibility.Visible)
        {
            ProfileNameBox.Text = SelectedTag(ProfileCombo) ?? "";
            ProfileNameBox.Focus();
            ProfileNameBox.SelectAll();
        }
    }

    private void ConfirmSaveProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = ProfileNameBox.Text.Trim();
        if (name.Length == 0)
        {
            ProfileNameBox.ToolTip = "Tên profile không được để trống.";
            return;
        }
        var cfg = _source.Clone();
        WriteConfigInto(cfg);
        if (!_store.Save(cfg.ToPreset(name)))
        {
            ProfileNameBox.ToolTip = "Không lưu được profile.";
            return;
        }
        SavePanel.Visibility = Visibility.Collapsed;
        LoadProfiles();
        SelectTag(ProfileCombo, name, "");
        ShowToast($"Đã lưu profile \"{name}\".");
    }

    private void CancelSaveProfile_Click(object sender, RoutedEventArgs e) => SavePanel.Visibility = Visibility.Collapsed;

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = SelectedTag(ProfileCombo);
        if (string.IsNullOrEmpty(name)) return;
        _store.Delete(name);
        ProfileCombo.SelectedIndex = 0;
        LoadProfiles();
        ShowToast($"Đã xóa profile \"{name}\".");
    }

    // ============ Dialog native driver ============

    private void Prefs_Click(object sender, RoutedEventArgs e) => OpenNative("/e", "Printing Preferences");

    private void Props_Click(object sender, RoutedEventArgs e) => OpenNative("/p", "Printer Properties");

    private void OpenNative(string arg, string label)
    {
        Result<bool> r;
        if (_printer is null)
        {
            r = Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.PrinterNotFound,
                Category = PrintErrorCategory.Config,
                Message = "Chưa chọn máy in.",
                Hint = "Chọn máy in ở thanh công cụ chính.",
            });
        }
        else if (arg == "/e")
        {
            r = PrinterDialogs.OpenPrintingPreferences(_printer.Name);
        }
        else
        {
            r = PrinterDialogs.OpenPrinterProperties(_printer.Name);
        }

        if (!r.IsSuccess)
        {
            NativeErrText.Text = $"✕ {r.Error!.Message} — {r.Error.Hint}";
            NativeErrText.Foreground = System.Windows.Media.Brushes.Firebrick;
            NativeErrText.Visibility = Visibility.Visible;
        }
        else
        {
            NativeErrText.Visibility = Visibility.Collapsed;
        }
    }

    // ============ Áp dụng ============

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        // Chặn đóng khi copy hoặc page range sai — hiện đủ lỗi, KHÔNG âm thầm sửa
        var copiesOk = ValidateCopies();
        var rangeOk = ValidatePageRange();
        if (!copiesOk || !rangeOk)
        {
            if (!copiesOk) CopiesBox.Focus();
            else RangeBox.Focus();
            return;
        }
        var cfg = _source.Clone();
        WriteConfigInto(cfg);
        Result = cfg;
        DialogResult = true;
    }

    private bool ValidateCopies()
    {
        var valid = int.TryParse(CopiesBox.Text.Trim(), out var n) && n is >= 1 and <= 999;
        CopiesError.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        return valid;
    }

    private bool ValidatePageRange()
    {
        // Chế độ "Tất cả" hoặc range trống = hợp lệ (ngầm hiểu All, giữ hành vi cũ)
        var valid = RangeCustom.IsChecked != true
            || string.IsNullOrWhiteSpace(RangeBox.Text)
            || IsValidPageRangeSyntax(RangeBox.Text);
        RangeErrText.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        return valid;
    }

    /// <summary>
    /// Kiểm tra CÚ PHÁP page range — grammar khớp ParseRange/ResolvePhysicalPages trong
    /// PrintJob.cs: "All" · 1,3 · 2-5 · 1-2,7 · S2:1-3 (s&gt;e được phép, Core tự đảo).
    /// Không resolve trang vật lý ở đây: mỗi file page count khác nhau — bound check
    /// là việc của engine lúc in (như PageRangeDialog).
    /// </summary>
    private static bool IsValidPageRangeSyntax(string? text)
    {
        var spec = text?.Trim() ?? "";
        if (spec.Length == 0 || spec.Equals("All", StringComparison.OrdinalIgnoreCase)) return true;

        // Section mode: "S2:1-3"
        if (spec.StartsWith("S", StringComparison.OrdinalIgnoreCase) && spec.Contains(':'))
        {
            var parts = spec.Split(':');
            if (!int.TryParse(parts[0].TrimStart('S', 's'), out _)) return false;
            return IsValidPageList(parts.Length > 1 ? parts[1] : "");
        }

        return IsValidPageList(spec);
    }

    /// <summary>Danh sách trang "1,3" · "2-5" — tách phần tử như Core ParseRange (phần tử rỗng = sai).</summary>
    private static bool IsValidPageList(string spec)
    {
        foreach (var part in spec.Split(',', StringSplitOptions.TrimEntries))
        {
            if (part.Length == 0) return false;
            if (part.Contains('-'))
            {
                var b = part.Split('-');
                if (b.Length != 2 || !int.TryParse(b[0], out _) || !int.TryParse(b[1], out _)) return false;
                // s > e hợp lệ (Core tự swap) — đây chỉ là kiểm tra cú pháp
            }
            else if (!int.TryParse(part, out _))
            {
                return false;
            }
        }
        return true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Ghi toàn bộ giá trị form vào PrintConfig (dùng cho cả Áp dụng lẫn Lưu profile).</summary>
    private void WriteConfigInto(PrintConfig cfg)
    {
        cfg.Copies = int.TryParse(CopiesBox.Text, out var copies) && copies >= 1 ? Math.Min(copies, 999) : 1;
        cfg.PageRange = RangeAll.IsChecked == true ? "All" : NormalizeRange(RangeBox.Text);

        cfg.Parity = SelectedTag(ParityCombo) switch
        {
            "Odd" => PageParityFilter.Odd,
            "Even" => PageParityFilter.Even,
            _ => PageParityFilter.All,
        };

        cfg.ColorMode = SelectedTag(ColorModeCombo) switch
        {
            "Color" => PrintColorMode.Color,
            "Grayscale" => PrintColorMode.Grayscale,
            "AsDocument" => PrintColorMode.AsDocument,
            _ => PrintColorMode.AsPrinter,
        };

        cfg.DuplexMode = SelectedTag(DuplexCombo) switch
        {
            "Simplex" => PrintDuplexMode.Simplex,
            "LongEdge" => PrintDuplexMode.LongEdge,
            "ShortEdge" => PrintDuplexMode.ShortEdge,
            _ => PrintDuplexMode.AsPrinter,
        };

        cfg.Collation = SelectedTag(CollationCombo) switch
        {
            "ByDocuments" => PrintCollation.ByDocuments,
            "ByPages" => PrintCollation.ByPages,
            _ => PrintCollation.AsPrinter,
        };

        // Khổ giấy: "" = theo máy in/tài liệu (engine bỏ qua, giữ của file)
        cfg.PaperSize = SelectedTag(PaperCombo) ?? "";

        // Khay giấy: "" = máy tự chọn
        var tray = SelectedTag(PaperSourceCombo);
        cfg.PaperSource = string.IsNullOrEmpty(tray) || tray == "__none__" ? null : tray;

        cfg.Orientation = SelectedTag(OrientationCombo) switch
        {
            "AsDocument" => PrintOrientation.AsDocument,
            "AsPrinter" => PrintOrientation.AsPrinter,
            "Landscape" => PrintOrientation.Landscape,
            _ => PrintOrientation.Portrait,
        };

        cfg.Quality = SelectedTag(QualityCombo) switch
        {
            "High" => PrintQuality.High,
            "Low" => PrintQuality.Low,
            "Draft" => PrintQuality.Draft,
            "Medium" => PrintQuality.Medium,
            _ => PrintQuality.AsPrinter,
        };

        cfg.ScaleMode = SelectedTag(ScaleModeCombo) switch
        {
            "ShrinkToPrintable" => PrintScaleMode.ShrinkToPrintable,
            "FitToPrintable" => PrintScaleMode.FitToPrintable,
            "Original" => PrintScaleMode.Original,
            "Fill" => PrintScaleMode.Fill,
            "Zoom" => PrintScaleMode.Zoom,
            _ => PrintScaleMode.AsDocument,
        };
        cfg.ScalePercent = cfg.ScaleMode == PrintScaleMode.Zoom && int.TryParse(ZoomBox.Text, out var zp) && zp >= 10
            ? Math.Min(zp, 999)
            : 100;

        var perSheet = SelectedTag(PerSheetCombo) ?? "1";
        if (perSheet == "booklet")
        {
            cfg.Booklet = true;
            cfg.PagesPerSheet = 2;
        }
        else
        {
            cfg.Booklet = false;
            cfg.PagesPerSheet = int.TryParse(perSheet, out var n) ? Math.Clamp(n, 1, 16) : 1;
        }

        cfg.ProfileName = SelectedTag(ProfileCombo) is { Length: > 0 } profile ? profile : null;
    }

    // ====== Toast nhỏ trong cửa sổ (không cần MainWindow) ======
    private void ShowToast(string message)
    {
        NativeErrText.Text = "✓ " + message;
        NativeErrText.Foreground = System.Windows.Media.Brushes.SeaGreen;
        NativeErrText.Visibility = Visibility.Visible;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) => { timer.Stop(); NativeErrText.Visibility = Visibility.Collapsed; };
        timer.Start();
    }
}