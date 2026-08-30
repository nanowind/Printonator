using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Printonator.Core.Models;
using Printonator.Core.Presets;
using Printonator.Spool.Printing;
using Printonator.UI.Localization;

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

        // ===== Chế độ Lite (mặc định): ẩn tính năng Phase 2 khỏi cửa sổ cấu hình =====
        if (!ModeResolver.IsFull)
        {
            AdvancedBatchPanel.Visibility = Visibility.Collapsed;   // Cover page + Merge (tab Cơ bản)
            WatermarkPanel.Visibility = Visibility.Collapsed;       // Watermark (tab Nâng cao)
        }

        Title = _targets.Count == 1
            ? L10n.F(Keys.Settings.WindowTitleTarget, _targets[0].FileName)
            : L10n.F(Keys.Settings.WindowTitlePlural, _targets.Count);
        TitleText.Text = _targets.Count == 1
            ? _targets[0].FileName
            : L10n.F(Keys.Settings.TitlePlural, _targets.Count);
        ApplyBtn.Content = _targets.Count > 0
            ? L10n.F(Keys.Settings.ApplyBtnTarget, _targets.Count)
            : L10n.S(Keys.Settings.ApplyBtnDefault);

        PopulatePrinterDrivenLists();
        LoadFromConfig(_source);
        RefreshRangePreview();
        LoadProfiles();
        _ = LoadExcelSheetsAsync();   // file Excel → probe danh sách sheet cho dropdown
    }

    // ============ Sheet cần in (Excel) — probe danh sách sheet, áp cho cả lô file Excel ============

    private static bool IsExcelFormat(string format)
        => format is "XLS" or "XLSX" or "XLSM";

    private async Task LoadExcelSheetsAsync()
    {
        try
        {
            var excel = _targets.FirstOrDefault(j => IsExcelFormat(j.Format));
            if (excel is null) return;   // không phải file Excel → ẩn combo sheet

            // LOADING STATE: hiện "Đang đọc sheet…" (disabled) NGAY — probe .xls lần đầu ~3s, không để
            // combo trống khiến user tưởng lag / bấm liên tục.
            SheetLabel.Visibility = Visibility.Visible;
            SheetCombo.Visibility = Visibility.Visible;
            SheetHint.Visibility = Visibility.Visible;
            SheetCombo.IsEnabled = false;
            SheetCombo.Items.Clear();
            SheetCombo.Items.Add(Item(L10n.S(Keys.Common.SheetLoading), "__loading__"));
            SheetCombo.SelectedIndex = 0;

            var sheets = await OfficeComPrintEngine.ListSheetsAsync(excel.FilePath);
            if (sheets.Length == 0 || !IsLoaded)
            {
                SheetLabel.Visibility = Visibility.Collapsed;
                SheetCombo.Visibility = Visibility.Collapsed;
                SheetHint.Visibility = Visibility.Collapsed;
                return;
            }
            SheetCombo.IsEnabled = true;
            SheetCombo.Items.Clear();
            SheetCombo.Items.Add(Item(L10n.S(Keys.Common.SheetAll), ""));
            foreach (var s in sheets) SheetCombo.Items.Add(Item(s, s));
            SelectTag(SheetCombo, _source.SheetName, "");
        }
        catch { /* probe lỗi → ẩn combo (in toàn bộ sheet) */ }
    }

    // ============ Nạp danh sách phụ thuộc máy in (khổ giấy + khay) ============

    private void PopulatePrinterDrivenLists()
    {
        // Khổ giấy: "theo máy in" (rỗng) → "theo tài liệu" (khổ gốc) → danh sách máy in / chuẩn
        var sizes = _printer is { SupportedPaperSizes.Length: > 0 } p
            ? PaperCatalog.FromPrinter(p.SupportedPaperSizes)
            : PaperCatalog.StandardSizes();
        PaperCombo.Items.Add(Item(L10n.S(Keys.Option.PaperAsPrinter), ""));
        PaperCombo.Items.Add(Item(L10n.S(Keys.Option.PaperAsDocument), PaperCatalog.AsDocument));
        foreach (var s in sizes)
            PaperCombo.Items.Add(Item(s, PaperCatalog.SizeName(s)));

        // Khay giấy: "theo máy in" (rỗng) + tray thân thiện từ máy in
        PaperSourceCombo.Items.Add(Item(L10n.S(Keys.Option.TrayAsPrinter), ""));
        if (_printer is { Trays.Length: > 0 } pp)
        {
            foreach (var t in pp.Trays)
                PaperSourceCombo.Items.Add(Item(t, t));
        }
        else
        {
            PaperSourceCombo.Items.Add(Item(L10n.S(Keys.Option.TrayUnknown), "__none__"));
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

        SelectTag(SheetCombo, cfg.SheetName, "");

        FitToPageWideCheck.IsChecked = cfg.FitToPageWide;
        AutoOrientationCheck.IsChecked = cfg.AutoOrientation;
        CoverPageCheck.IsChecked = cfg.CoverPage;
        MergeCheck.IsChecked = cfg.MergeIntoOneFile;
        WatermarkText.Text = cfg.WatermarkText ?? "";
        WatermarkOpacitySlider.Value = Math.Clamp(cfg.WatermarkOpacity, 0.1, 1.0);
        UpdateWatermarkOpacityText();

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
                    RangePreview.Text = L10n.S(Keys.Settings.PreviewRangeAll);
                else
                    RangePreview.Text = L10n.S(Keys.Settings.PreviewRangePrompt);
            }
            return;
        }

        var representative = _representative;
        if (representative.Sections.Count > 0 && SectionInfo is not null)
        {
            SectionInfo.Text = string.Join("  ·  ", representative.Sections.Select(
                s => L10n.F(Keys.Common.SectionInfoFormat, s.Index, s.FirstPhysicalPage, s.LastPhysicalPage)));
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
                    ? string.Join(",", pages.Take(20)) + L10n.F(Keys.Common.PagesCountSuffix, pages.Length)
                    : string.Join(",", pages);
                RangePreview.Text = L10n.F(Keys.Settings.PreviewWillPrint, shown);
                RangePreview.Foreground = System.Windows.Media.Brushes.SeaGreen;
            }
            else
            {
                RangePreview.Text = L10n.F(Keys.Common.PreviewErrorFormat, r.Error!.Message);
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

    // ============ Watermark opacity slider: hiện giá trị phần trăm ============

    private void WatermarkOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (WatermarkOpacityValue is null) return;   // gọi trước InitializeComponent xong XAML binding
        UpdateWatermarkOpacityText();
    }

    private void UpdateWatermarkOpacityText()
    {
        if (WatermarkOpacityValue is null) return;
        WatermarkOpacityValue.Text = $"{Math.Round(WatermarkOpacitySlider.Value * 100)}%";
    }

    // ============ Profile (printer template) ============

    private void LoadProfiles()
    {
        _loadingProfile = true;
        try
        {
            var current = SelectedTag(ProfileCombo);
            ProfileCombo.Items.Clear();
            ProfileCombo.Items.Add(Item(L10n.S(Keys.Settings.ProfileNone), ""));
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
            ProfileNameBox.ToolTip = L10n.S(Keys.Settings.ProfileEmptyNameError);
            return;
        }
        var cfg = _source.Clone();
        WriteConfigInto(cfg);
        if (!_store.Save(cfg.ToPreset(name)))
        {
            ProfileNameBox.ToolTip = L10n.S(Keys.Settings.ProfileSaveError);
            return;
        }
        SavePanel.Visibility = Visibility.Collapsed;
        LoadProfiles();
        SelectTag(ProfileCombo, name, "");
        ShowToast(L10n.F(Keys.Settings.ProfileSaved, name));
    }

    private void CancelSaveProfile_Click(object sender, RoutedEventArgs e) => SavePanel.Visibility = Visibility.Collapsed;

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = SelectedTag(ProfileCombo);
        if (string.IsNullOrEmpty(name)) return;
        _store.Delete(name);
        ProfileCombo.SelectedIndex = 0;
        LoadProfiles();
        ShowToast(L10n.F(Keys.Settings.ProfileDeleted, name));
    }

    // ============ Xuất / Nhập profile (.printonator) ============

    private void ExportPreset_Click(object sender, RoutedEventArgs e)
    {
        var all = _store.Load();
        if (all.Count == 0)
        {
            ShowBanner(L10n.S(Keys.Preset.ExportEmpty));
            return;
        }

        // Export preset đang chọn (combo) — nếu đang "Không dùng profile" thì export toàn bộ.
        var name = SelectedTag(ProfileCombo);
        var selected = all.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        var toExport = selected.Count > 0 ? selected : all;

        var dlg = new SaveFileDialog
        {
            Filter = L10n.S(Keys.Preset.FileDialogFilter),
            DefaultExt = ".printonator",
            FileName = toExport.Count == 1 ? toExport[0].Name : "printonator-presets",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            PresetExporter.Export(toExport, dlg.FileName);
            ShowToast(L10n.F(Keys.Preset.ExportCount, toExport.Count));
        }
        catch
        {
            ShowBanner(L10n.S(Keys.Preset.ExportFail));
        }
    }

    private void ImportPreset_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = L10n.S(Keys.Preset.FileDialogFilter),
            DefaultExt = ".printonator",
            Multiselect = false,
        };
        if (dlg.ShowDialog(this) != true) return;

        // Import không ném: file hỏng → PresetExporter đổi tên .corrupt + trả danh sách rỗng
        var presets = PresetExporter.Import(dlg.FileName);

        if (presets.Count == 0)
        {
            ShowBanner(L10n.S(Keys.Preset.ImportEmpty));
            return;
        }

        foreach (var p in presets)
            _store.Save(p);   // upsert theo tên — preset trùng tên bị thay, tên khác được thêm mới
        LoadProfiles();
        ShowToast(L10n.S(Keys.Preset.ImportSuccess));
    }

    private void ShowBanner(string message)
    {
        NativeErrText.Text = message;
        NativeErrText.Foreground = System.Windows.Media.Brushes.Firebrick;
        NativeErrText.Visibility = Visibility.Visible;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) => { timer.Stop(); NativeErrText.Visibility = Visibility.Collapsed; };
        timer.Start();
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
                Message = L10n.S(Keys.Settings.PrinterNotSelectedError),
                Hint = L10n.S(Keys.Settings.PrinterNotSelectedHint),
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
            NativeErrText.Text = L10n.F(Keys.Common.NativeErrFormat, r.Error!.Message, r.Error.Hint);
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

        // Sheet cần in (Excel): rỗng/"Tất cả" → in toàn bộ; còn lại tên sheet cụ thể (áp cho cả lô Excel)
        var sheet = SelectedTag(SheetCombo);
        cfg.SheetName = string.IsNullOrEmpty(sheet) ? null : sheet;

        cfg.FitToPageWide = FitToPageWideCheck.IsChecked == true;
        cfg.AutoOrientation = AutoOrientationCheck.IsChecked == true;
        cfg.CoverPage = CoverPageCheck.IsChecked == true;
        cfg.MergeIntoOneFile = MergeCheck.IsChecked == true;
        var watermark = WatermarkText.Text.Trim();
        cfg.WatermarkText = string.IsNullOrEmpty(watermark) ? null : watermark;
        cfg.WatermarkOpacity = Math.Round(Math.Clamp(WatermarkOpacitySlider.Value, 0.1, 1.0), 2);
        cfg.WatermarkPosition = "center";

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