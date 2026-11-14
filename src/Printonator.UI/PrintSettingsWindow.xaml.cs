using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
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
    // 2 cờ Excel (FitToPageWide/AutoOrientation) KHÔNG có control trong dialog này (chỉnh bằng chip ở action
    // bar mỗi row). Giá trị lấy từ preset user chọn, giữ tạm ở đây rồi chép vào cfg LÚC ÁP DỤNG — vì _source
    // là THAM CHIẾU tới targets[0].Config (không clone), ghi ngay lúc chọn preset sẽ khiến nút Hủy cũng đổi cấu hình file.
    private bool _fitWideFromPreset;
    private bool _autoOrientFromPreset;
    private readonly PrintJob? _representative;
    private readonly PresetStore _store = new();
    private bool _loadingProfile;
    // Đang có 1 lần đọc cài đặt driver chưa xong — chặn bấm lần 2 mở thêm OpenPrinter (xem TakeFromPrinter_Click).
    private bool _readingPrinterDefaults;
    // Lần render preview trước — chỉ dùng để biết GIÁ TRỊ NÀO vừa đổi mà chạy animation tương ứng.
    // null = chưa render lần nào → render đầu tiên không animate.
    private PrintPreviewModel? _lastPreview;

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
            AdvancedBatchPanel.Visibility = Visibility.Collapsed;   // Merge (tab Cơ bản)
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
        // Khởi tạo 2 cờ Excel từ config của file: chúng đi xuyên qua dialog (bật bằng chip ở row) và
        // Apply_Click ghi lại chính 2 biến này — không khởi tạo thì mở dialog rồi Apply sẽ XOÁ cờ của job.
        _autoOrientFromPreset = _source.AutoOrientation;
        _fitWideFromPreset = _source.FitToPageWide;
        RefreshRangePreview();
        LoadProfiles();
        RefreshPreview();
        UpdateOrientationLock();
        _ = LoadExcelSheetsAsync();   // file Excel → probe danh sách sheet cho dropdown
    }

    /// <summary>
    /// Excel + "Tự động chiều giấy" đang bật → chiều giấy do NỘI DUNG quyết, lựa chọn Dọc/Ngang của
    /// user bị engine ghi đè (OfficeComPrintEngine.ApplyExcelPageExtras chạy sau ApplyExcelSetup).
    /// Trước đây combo vẫn mở và hint vẫn hứa "ép cả batch" — user chọn xong in ra khác, không hiểu vì sao.
    /// Khoá combo + nói rõ lý do thay vì im lặng ghi đè.
    /// Cờ auto có MỘT nguồn sự thật là _autoOrientFromPreset, khởi tạo từ _source.AutoOrientation ở
    /// constructor rồi bị preset/"No profile" ghi đè. KHÔNG đọc thẳng _source ở đây: _source là tham
    /// chiếu tới config của file nên sẽ khoá dính — chọn preset tắt auto cũng không mở lại được, mà
    /// Apply lại ghi false, khiến câu "chiều giấy do nội dung quyết định" thành nói dối.
    /// </summary>
    private void UpdateOrientationLock()
    {
        if (OrientationCombo is null || OrientationHint is null) return;
        var autoOn = _autoOrientFromPreset;
        // Phải là CẢ LÔ đều Excel: auto-orientation chỉ ghi đè chiều giấy trong nhánh Excel
        // (OfficeComPrintEngine.ApplyExcelPageExtras), còn PDF/Word/ảnh vẫn tôn trọng chiều user chọn.
        // Khoá combo vì lô CÓ MỘT file Excel sẽ làm file PDF trong lô không đặt được chiều giấy nữa.
        var locked = autoOn && _targets.Count > 0 && _targets.All(j => j.IsExcel);
        OrientationCombo.IsEnabled = !locked;
        OrientationHint.Text = L10n.S(locked
            ? Keys.Settings.HintAutoOrientationLock
            : Keys.Settings.HintOrientation);
    }

    // ============ Sheet cần in (Excel) — probe danh sách sheet, áp cho cả lô file Excel ============

    /// <summary>File Excel trong lô. So khớp trùng ngữ nghĩa <see cref="PrintJob.IsExcel"/> (không phân
    /// biệt hoa thường; CSV không tính vì CSV có thể rơi xuống engine browser nên 2 option Excel là no-op).</summary>
    private static bool IsExcelFormat(string format)
        => format.Equals("XLS", StringComparison.OrdinalIgnoreCase)
           || format.Equals("XLSX", StringComparison.OrdinalIgnoreCase)
           || format.Equals("XLSM", StringComparison.OrdinalIgnoreCase);

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
            TakeFromPrinterBtn.IsEnabled = false;
        }
        else
        {
            PrinterNameText.Text = _printer.Name;
        }

        // KHÔNG ẩn dòng Color/Duplex ở đây: lúc này combo chưa được nạp giá trị (LoadFromConfig chạy sau),
        // nên chưa biết đang yêu cầu gì. Việc ẩn nằm ở UpdateCapabilityVisibility (cuối LoadFromConfig).
    }

    private static void SetRowVisible(bool visible, params FrameworkElement[] parts)
    {
        foreach (var p in parts)
            p.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
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
        // Collation không còn item "AsPrinter" (chỉ ByDocuments/ByPages) → preset cũ mang
        // AsPrinter phải rơi về ByDocuments (tương đương trung thực với hành vi Office).
        SelectTag(CollationCombo, cfg.Collation.ToString(), "ByDocuments");

        SelectTag(PaperCombo, cfg.PaperSize, "A4"); // không tìm thấy (rỗng) → giữ item "Theo máy in" index 0
        if (string.IsNullOrEmpty(cfg.PaperSize)) PaperCombo.SelectedIndex = 0;
        SelectTag(PaperSourceCombo, cfg.PaperSource ?? "", "");

        SelectTag(SheetCombo, cfg.SheetName, "");

        // KHÔNG còn tick "Fit cột" / "Tự xoay chiều giấy" ở đây: 2 cờ FitToPageWide/AutoOrientation đi xuyên
        // qua dialog (Apply_Click clone _source — vốn đã mang sẵn 2 cờ này — rồi MainWindow CopyInto job.Config).
        // 2 cờ này chỉnh bằng chip ở action bar từng row. Đừng "thêm lại" checkbox vào cửa sổ này.
        // KHÔNG còn tick "In trang bìa" ở đây: cờ CoverPage đi xuyên qua dialog (Apply_Click clone _source
        // — vốn đã mang sẵn cờ này — rồi MainWindow CopyInto job.Config). Tick bìa nằm ở thanh công cụ
        // dashboard. Đừng "thêm lại" checkbox bìa vào cửa sổ này.
        MergeCheck.IsChecked = cfg.MergeIntoOneFile;
        WatermarkText.Text = cfg.WatermarkText ?? "";
        WatermarkOpacitySlider.Value = Math.Clamp(cfg.WatermarkOpacity, 0.1, 1.0);
        UpdateWatermarkOpacityText();
        SelectTag(WatermarkPositionCombo, cfg.WatermarkPosition, "center");

        SelectTag(OrientationCombo, cfg.Orientation.ToString(), "Portrait");
        SelectTag(QualityCombo, cfg.Quality.ToString(), "AsPrinter");
        SelectTag(ScaleModeCombo, cfg.ScaleMode.ToString(), "AsDocument");
        ZoomBox.Text = cfg.ScalePercent.ToString();
        UpdateZoomState();

        // Booklet không còn trong combo (chưa engine nào imposition thật) — preset cũ có cờ này
        // được quy về 2 trang/tờ, đúng hành vi cũ khi engine đọc.
        if (cfg.Booklet) SelectTag(PerSheetCombo, "2", "1");
        else SelectTag(PerSheetCombo, Math.Max(cfg.PagesPerSheet, 1).ToString(), "1");

        UpdateCapabilityVisibility();
    }

    /// <summary>Ẩn dòng Color/Duplex khi máy in CHẮC CHẮN không làm được VÀ giá trị đang chọn là trung tính.
    /// Nếu user/preset đang yêu cầu màu hoặc 2 mặt thì GIỮ dòng lại — ẩn đi sẽ khiến yêu cầu đó không còn
    /// nhìn thấy cũng không tắt được (giá trị vẫn được WriteConfigInto ghi lại).</summary>
    private void UpdateCapabilityVisibility()
    {
        var hideColor = !ModeResolver.IsFull
            && _printer?.ColorSupport == CapabilitySupport.NotSupported
            && SelectedTag(ColorModeCombo) is "AsPrinter" or "AsDocument" or "Grayscale";
        var hideDuplex = !ModeResolver.IsFull
            && _printer?.DuplexSupport == CapabilitySupport.NotSupported
            && SelectedTag(DuplexCombo) is "AsPrinter" or "Simplex";

        SetRowVisible(!hideColor, ColorLabel, ColorModeCombo, ColorHint);
        SetRowVisible(!hideDuplex, DuplexLabel, DuplexCombo, DuplexHint);
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

    /// <summary>Chọn item có ĐÚNG tag đó. false = không có item nào khớp (selection giữ nguyên) —
    /// để nơi gọi biết field nào KHÔNG điền được thay vì báo thành công chung chung.</summary>
    private static bool TrySelectTag(ComboBox combo, string? tag)
    {
        if (tag is null) return false;
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if ((combo.Items[i] as ComboBoxItem)?.Tag?.ToString() == tag) { combo.SelectedIndex = i; return true; }
        }
        return false;
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
        RefreshPreview();
    }

    private void Copies_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (CopiesError is null) return;
        // Lỗi CHỈ nháy khi đã có nội dung mà SAI (non-rỗng && (không parse được || ngoài 1..999)).
        // Rỗng = đang gõ dở → ẨN lỗi; ValidateCopies lúc bấm Apply vẫn chặn + hiện lỗi nếu vẫn rỗng.
        var t = CopiesBox.Text.Trim();
        var invalid = t.Length > 0 && (!int.TryParse(t, out var n) || n is < 1 or > 999);
        CopiesError.Visibility = invalid ? Visibility.Visible : Visibility.Collapsed;
        RefreshPreview();   // số bản đổi → caption "×N" đổi
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

        // MỘT nguồn sự thật cho cả dialog: dùng đúng resolver của panel preview (range + lẻ/chẵn) trên
        // cấu hình DRAFT — không ghi vào Config của job đang in, và không bao giờ lệch với panel bên cạnh.
        var r = representative.ResolvePrintPages(DraftConfig());
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

    private static string NormalizeRange(string text)
    {
        var t = text?.Trim() ?? "";
        return t.Length == 0 ? "All" : t;
    }

    // ============ Xem trước khi in (sơ đồ tờ giấy) ============

    /// <summary>Vẽ lại sơ đồ theo ĐÚNG những gì form đang hiển thị. Gọi từ mọi option làm đổi tờ giấy.</summary>
    private void RefreshPreview()
    {
        // Event của XAML bắn ra trong lúc parse (trước khi field kịp gán) — xem guard ở RefreshRangePreview.
        if (SheetBorder is null) return;
        var cfg = DraftConfig();
        RenderPreview(PrintPreviewModel.Build(_representative, cfg), cfg);
    }

    /// <summary>Cấu hình DRAFT = form hiện tại áp lên bản clone của _source (WriteConfigInto ghi vào clone),
    /// nên gõ trong form không hề đụng cấu hình thật — nút Hủy vẫn sạch như cũ.</summary>
    private PrintConfig DraftConfig()
    {
        var draft = _source.Clone();
        WriteConfigInto(draft);
        return draft;
    }

    /// <summary>Mọi combo đổi khổ giấy / chiều / 2 mặt / số trang mỗi tờ / màu / lẻ-chẵn đều đi qua đây.
    /// CỐ Ý không hook ZoomBox, QualityCombo, PaperSourceCombo: chúng không làm đổi hình tờ giấy.</summary>
    private void PreviewOption_Changed(object sender, SelectionChangedEventArgs e)
    {
        RefreshRangePreview();   // lẻ/chẵn đổi → dòng "Sẽ in trang" phải đổi theo, không để lệch panel
        RefreshPreview();
    }

    private void PreviewToggle_Click(object sender, RoutedEventArgs e)
    {
        // Collapsed (không phải Hidden) để cột Auto co về 0 — cửa sổ hẹp lại y như trước khi có preview.
        PreviewPanel.Visibility = PreviewPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void RenderPreview(PrintPreviewModel m, PrintConfig cfg)
    {
        const double Play = 190;    // chiều cao vùng sân khấu (khớp Grid Height trong XAML)
        const double MaxSide = 170; // cạnh LỚN NHẤT của tờ giấy, chừa 10px mỗi bên

        // --- Lỗi: không vẽ tờ nào, chỉ nói lý do + VIỆC CẦN LÀM (Hint) ---
        if (m.IsError)
        {
            SheetBorder.Visibility = Visibility.Collapsed;
            BackSheetBorder.Visibility = Visibility.Collapsed;
            AssumedSheetOutline.Visibility = Visibility.Collapsed;
            FlipEdgeLine.Visibility = Visibility.Collapsed;
            GrayOverlay.Visibility = Visibility.Collapsed;
            ThumbHost.Children.Clear();
            PreviewError.Text = string.IsNullOrWhiteSpace(m.ErrorHint)
                ? m.ErrorMessage ?? L10n.S(Keys.Settings.PreviewNothing)
                : $"{m.ErrorMessage} {m.ErrorHint}";
            PreviewError.Visibility = Visibility.Visible;
            PreviewCaption.Text = "";    // lý do đã nằm ở PreviewError, không lặp 2 lần
            PreviewSkippedText.Visibility = Visibility.Collapsed;
            _lastPreview = m;
            return;
        }

        SheetBorder.Visibility = Visibility.Visible;
        PreviewError.Visibility = Visibility.Collapsed;

        // --- Khổ giấy: giữ đúng tỉ lệ thật, cạnh lớn vừa 170 ---
        var wMm = Math.Max(m.SheetWidthMm, 1);
        var hMm = Math.Max(m.SheetHeightMm, 1);
        var scale = Math.Min(MaxSide / wMm, MaxSide / hMm);
        var w = Math.Round(wMm * scale);
        var h = Math.Round(hMm * scale);
        if (h > Play) h = Play;    // an toàn: không tràn khỏi vùng sân khấu

        // Animation 1: khổ giấy đổi → tờ giấy co giãn sang kích thước mới (180ms).
        // !IsLoaded (đang nạp form trong ctor / host test không show) → prev = null = không animate:
        // mở dialog không thấy hình tự lớn lên, và không có storyboard nào sống ngoài cửa sổ.
        var prev = IsLoaded ? _lastPreview : null;
        var animateSize = prev is not null && !prev.IsError
            && (Math.Abs(prev.SheetWidthMm - m.SheetWidthMm) > 0.01 || Math.Abs(prev.SheetHeightMm - m.SheetHeightMm) > 0.01);
        SetSize(SheetBorder, w, h, animateSize);
        // Tờ mặt sau cùng khổ, lệch 6px để nhìn ra 2 tờ.
        SetSize(BackSheetBorder, w, h, false);
        BackSheetBorder.Visibility = m.BackSideFlipped ? Visibility.Visible : Visibility.Collapsed;

        // Khổ phỏng đoán (AsDocument/AsPrinter/không có trong danh mục): viền xám ĐỨT nét cùng khung tờ,
        // ngầm hiểu "hình này chỉ là ước lượng"; viền liền = đúng khổ đã chọn.
        SheetBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
        AssumedSheetOutline.Width = w;
        AssumedSheetOutline.Height = h;
        AssumedSheetOutline.Visibility = m.PaperIsAssumed ? Visibility.Visible : Visibility.Collapsed;

        // --- Đường lật cạnh (chỉ khi in 2 mặt) ---
        FlipEdgeLine.Visibility = m.BackSideFlipped ? Visibility.Visible : Visibility.Collapsed;
        var horizontal = m.FlipEdgeHorizontal;
        var animateEdge = m.BackSideFlipped && prev is not null && prev.BackSideFlipped && prev.FlipEdgeHorizontal != horizontal;
        // Dọn clock nằm NGOÀI guard BackSideFlipped: chuyển 2 mặt → Simplex trước đây bỏ qua bước này,
        // để lại animation 150ms sống trên Line đã Collapsed (vô hình nhưng vẫn chạy).
        if (!animateEdge)
        {
            FlipEdgeLine.BeginAnimation(Line.X1Property, null);
            FlipEdgeLine.BeginAnimation(Line.Y1Property, null);
            FlipEdgeLine.BeginAnimation(Line.X2Property, null);
            FlipEdgeLine.BeginAnimation(Line.Y2Property, null);
        }
        if (m.BackSideFlipped)
        {
            // Trục lật nằm GIỮA tờ, không nằm trên viền (viền bị đường kẻ đè lên thì không thấy gì):
            // ngang = trục ngang giữa tờ, dọc = trục dọc giữa tờ.
            var (ax1, ay1, ax2, ay2) = horizontal
                ? (0d, h / 2, w, h / 2)
                : (w / 2, 0d, w / 2, h);

            if (animateEdge)
            {
                // Animation 2: trục lật quét từ giữa-theo-chiều-dọc sang giữa-theo-chiều-ngang, 150ms.
                FlipEdgeLine.BeginAnimation(Line.X1Property, To(ax1, 150));
                FlipEdgeLine.BeginAnimation(Line.Y1Property, To(ay1, 150));
                FlipEdgeLine.BeginAnimation(Line.X2Property, To(ax2, 150));
                FlipEdgeLine.BeginAnimation(Line.Y2Property, To(ay2, 150));
            }
            FlipEdgeLine.X1 = ax1;
            FlipEdgeLine.Y1 = ay1;
            FlipEdgeLine.X2 = ax2;
            FlipEdgeLine.Y2 = ay2;
        }

        // --- Chia tờ thành lưới theo số trang mỗi tờ (định dạng không hỗ trợ N-up → lưới 1×1) ---
        var (cols, rows) = PaperCatalog.NUpGrid(m.PagesPerSheetHonoured ? m.PagesPerSheet : 1);
        BuildThumbs(m, cols, rows);

        // Animation 3: số trang/tờ đổi → lưới mới hiện lên (opacity 0→1 + scale 1.18→1), 140ms.
        var animateGrid = prev is not null && !prev.IsError && prev.PagesPerSheet != m.PagesPerSheet;
        if (animateGrid)
        {
            ThumbHost.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = EaseOut() });
            ThumbScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1.18, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = EaseOut() });
            ThumbScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1.18, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = EaseOut() });
        }
        else
        {
            ThumbHost.BeginAnimation(UIElement.OpacityProperty, null);
            ThumbScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ThumbScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ThumbHost.Opacity = 1;
            ThumbScale.ScaleX = 1;
            ThumbScale.ScaleY = 1;
        }

        // --- Caption: số tờ (+ số bản khi > 1) + nói rõ chỗ preview CHỈ LÀ ƯỚC LƯỢNG ---
        var caption = m.IsUnknownPageCount
            ? L10n.S(Keys.Settings.PreviewUnknown)
            : L10n.F(Keys.Settings.PreviewSheets, m.Sheets.Count);
        if (m.Copies > 1) caption = $"{caption} ×{m.Copies}";
        // Khổ giấy phỏng đoán là TRẠNG THÁI MẶC ĐỊNH (PaperSize=AsDocument) — thiếu chữ này thì tờ A4
        // vẽ ra trông y như đã chọn A4.
        if (m.PaperIsAssumed) caption = $"{caption} {L10n.S(Keys.Settings.PreviewEstimated)}";
        // N-up chỉ có thật với PDF (xem PagesPerSheetHonoured) — không nói ra thì "3 tờ" là lời hứa suông.
        if (!m.PagesPerSheetHonoured) caption = $"{caption} {L10n.S(Keys.Settings.PreviewNupPdfOnly)}";
        PreviewCaption.Text = caption;

        // Dòng "bỏ N trang" chỉ nói về bộ lọc lẻ/chẵn — bộ lọc đang TẮT thì không có gì để nói.
        PreviewSkippedText.Text = L10n.F(Keys.Settings.PreviewSkipped, m.SkippedCount);
        PreviewSkippedText.Visibility = cfg.Parity != PageParityFilter.All && m.SkippedCount > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        _lastPreview = m;
    }

    /// <summary>Dựng lại toàn bộ trang thu nhỏ trên tờ. Chỉ 1 tờ được vẽ (tờ đang xem) — cần minh hoạ
    /// "tờ này chứa trang nào", không phải liệt kê cả tệp.</summary>
    private void BuildThumbs(PrintPreviewModel m, int cols, int rows)
    {
        ThumbHost.Rows = rows;
        ThumbHost.Columns = cols;
        ThumbHost.Children.Clear();
        GrayOverlay.Visibility = m.Grayscale ? Visibility.Visible : Visibility.Collapsed;

        var sheet = m.Sheets.Count > 0 ? m.Sheets[0] : null;
        if (sheet is null) return;

        foreach (var thumb in sheet.Thumbs.Take(cols * rows))
            ThumbHost.Children.Add(MakeThumb(thumb, m.Grayscale));
    }

    private static Border MakeThumb(PreviewThumb thumb, bool grayscale)
    {
        var lineBrush = new SolidColorBrush(grayscale ? Color.FromRgb(0xB0, 0xB0, 0xB0) : Color.FromRgb(0xC8, 0xC8, 0xC8));
        var card = new Border
        {
            Margin = new Thickness(2),
            Background = new SolidColorBrush(thumb.Kept ? Colors.White : Color.FromRgb(0xEF, 0xEF, 0xEF)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
            BorderThickness = new Thickness(1),
        };

        var body = new Grid();
        // Badge số trang (góc trên trái); "?" = chưa biết số trang, "—" = trang bị bỏ qua.
        var badge = new TextBlock
        {
            Text = thumb.Kept ? (thumb.PageNumber?.ToString() ?? "?") : "—",
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x6B)),
            Margin = new Thickness(3, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        body.Children.Add(badge);

        // 3 vạch xám = "chữ" trên trang, để hình đọc ra là trang giấy chứ không phải ô trống.
        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
        for (var i = 0; i < 3; i++)
        {
            lines.Children.Add(new Rectangle
            {
                Height = 1.5,
                Fill = lineBrush,
                Margin = new Thickness(0, 2, i == 2 ? 10 : 0, 0),   // dòng cuối ngắn hơn
            });
        }
        body.Children.Add(lines);
        card.Child = body;
        return card;
    }

    /// <summary>Đặt kích thước tờ; animate=true thì co giãn mượt từ kích thước hiện tại (180ms).</summary>
    private static void SetSize(FrameworkElement el, double w, double h, bool animate)
    {
        if (!animate)
        {
            el.BeginAnimation(FrameworkElement.WidthProperty, null);
            el.BeginAnimation(FrameworkElement.HeightProperty, null);
            el.Width = w;
            el.Height = h;
            return;
        }
        // FillBehavior mặc định (HoldEnd): giữ đúng giá trị cuối sau khi chạy xong.
        el.BeginAnimation(FrameworkElement.WidthProperty, To(w, 180));
        el.BeginAnimation(FrameworkElement.HeightProperty, To(h, 180));
    }

    private static DoubleAnimation To(double to, int ms) => new(to, TimeSpan.FromMilliseconds(ms))
    {
        EasingFunction = EaseOut(),
    };

    private static CubicEase EaseOut() => new() { EasingMode = EasingMode.EaseOut };

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
            // Pre-select profile theo config đang mở (ProfileName) — không phải combo hiện tại (rỗng lúc
            // mới mở → luôn về "No profile" dù config đã áp preset "phuc1"...).
            var current = SelectedTag(ProfileCombo) ?? _source.ProfileName;
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
        // "No profile" → BỎ preset: reset mọi field về cấu hình mặc định (không phải giữ giá trị preset
        // rồi chỉ xóa tên — trước đây chọn xong không về default được, field vẫn mang cấu hình phuc1).
        if (string.IsNullOrEmpty(name))
        {
            LoadFromConfig(new PrintConfig());
            // "No profile" = về mặc định: 2 cờ Excel cũng phải về false (không có control để user tự tắt).
            _fitWideFromPreset = false;
            _autoOrientFromPreset = false;
            UpdateOrientationLock();   // về mặc định → mở lại combo chiều giấy
            return;
        }
        var preset = _store.Load().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (preset is not null)
        {
            LoadFromConfig(preset.ToPrintConfig());
            // 2 cờ Excel không còn control trong dialog (chỉnh bằng chip ở row). GIỮ TẠM giá trị preset ở đây
            // rồi chép vào cfg lúc ÁP DỤNG — KHÔNG ghi thẳng vào _source, vì _source là THAM CHIẾU tới
            // targets[0].Config (không clone) nên ghi ngay sẽ làm nút Hủy cũng đổi cấu hình file.
            _fitWideFromPreset = preset.FitToPageWide;
            _autoOrientFromPreset = preset.AutoOrientation;
            UpdateOrientationLock();   // preset vừa bật/tắt tự xoay chiều → khoá/mở lại combo chiều giấy
        }
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
        cfg.FitToPageWide = _fitWideFromPreset;   // preset đang chọn (xem ProfileCombo_SelectionChanged)
        cfg.AutoOrientation = _autoOrientFromPreset;
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

    /// <summary>Điền 4 combo phụ thuộc máy in bằng cài đặt ĐANG LƯU trong driver. Chỉ đọc: không lưu preset,
    /// không đụng các field khác của form (KHÔNG gọi LoadFromConfig — nó ghi đè cả copies/page range/...).</summary>
    private async void TakeFromPrinter_Click(object sender, RoutedEventArgs e)
    {
        // Chạy TRƯỚC khi disable nút: không có máy in thì thoát luôn, finally bên dưới không được
        // chạy (nếu chạy sẽ bật lại nút mà constructor đã disable ở dòng ~138).
        if (_printer is null) return;
        // Bấm lần 2 trong lúc lần 1 còn kẹt: không mở thêm 1 OpenPrinter nữa (mỗi lần kẹt = 1 thread treo
        // vĩnh viễn — WaitAsync chỉ bỏ CHỜ, không hủy P/Invoke).
        if (_readingPrinterDefaults) return;
        _readingPrinterDefaults = true;

        TakeFromPrinterBtn.IsEnabled = false;
        Task? read = null;
        try
        {
            var name = _printer.Name;
            // P/Invoke OpenPrinter/GetPrinter có thể TREO vô hạn với máy in mạng bị firewall chặn
            // (PrinterService.ListPrinters cũng vì thế mà chạy trên worker thread + watchdog 4s).
            // Đọc trên UI thread sẽ đóng băng cả dialog, kể cả nút Hủy — đừng "tối giản" thành gọi trực tiếp.
            var typed = Task.Run(() => PrinterDialogs.ReadPrinterDefaults(name));
            read = typed;
            var r = await typed.WaitAsync(TimeSpan.FromSeconds(4));

            if (!r.IsSuccess || r.Value is not { } d)
            {
                ShowBanner(r.Error is { } err
                    ? L10n.F(Keys.Common.NativeErrFormat, err.Message, err.Hint)
                    : L10n.S(Keys.Settings.TakeFromPrinterFailed));
                return;
            }

            // Chiều giấy: CHỈ điền khi form đang ép 1 chiều CỤ THỂ. Đang "theo tài liệu"/"theo máy in" thì giữ
            // nguyên — dmOrientation=1 (Portrait) là mặc định của gần như MỌI driver, điền nó vào biến "theo
            // tài liệu" thành Portrait cứng và ApplyWordPaper ép wdOrientPortrait cho cả file ngang.
            // Bỏ qua khi combo đang bị khoá (Excel + tự xoay chiều): giá trị điền vào cũng bị engine ghi đè,
            // và điền vào combo disabled chỉ làm user tưởng nút đã lấy được chiều giấy.
            if (OrientationCombo.IsEnabled && SelectedTag(OrientationCombo) is "Portrait" or "Landscape")
                TrySelectTag(OrientationCombo, d.Orientation.ToString());

            // Khổ giấy: bấm nút này là để lấy khổ CỦA MÁY IN → driver trả khổ KHÔNG có trong list (list dựng
            // từ khả năng máy in + khổ chuẩn) thì THÊM vào rồi chọn, thay vì im lặng để nguyên khổ cũ mà
            // vẫn báo "đã điền".
            var paper = d.PaperName;
            var paperOk = !string.IsNullOrWhiteSpace(paper);
            if (paperOk && !TrySelectTag(PaperCombo, paper))
            {
                PaperCombo.Items.Add(Item(PaperCatalog.Describe(paper!), paper!));
                PaperCombo.SelectedIndex = PaperCombo.Items.Count - 1;
            }
            var colorOk = TrySelectTag(ColorModeCombo, d.ColorMode.ToString());
            var duplexOk = TrySelectTag(DuplexCombo, d.DuplexMode.ToString());

            UpdateCapabilityVisibility();   // khả năng máy in không đổi, nhưng giá trị combo vừa đổi
            // Chỉ báo "đã điền theo máy in" khi khổ giấy THẬT SỰ đổi (khớp list hoặc vừa được thêm) — nếu
            // driver không trả khổ đọc được thì giữ nguyên khổ cũ và nói rõ, không hứa điều không làm.
            if (paperOk && colorOk && duplexOk)
                ShowToast(L10n.S(Keys.Settings.TakeFromPrinterDone));
            else
                ShowBanner(L10n.S(Keys.Settings.TakeFromPrinterFailed));
        }
        catch (TimeoutException)
        {
            // Driver không trả lời trong 4s: bỏ chờ, báo lỗi — task nền tự kết thúc khi driver nhả.
            ShowBanner(L10n.S(Keys.Settings.TakeFromPrinterFailed));
        }
        finally
        {
            // Nút chỉ bật lại khi lần đọc đã KẾT THÚC. Bấm lúc đang kẹt mà bật lại ngay thì lần bấm thứ hai mở
            // thêm 1 OpenPrinter nữa — mỗi lần kẹt là 1 thread treo vĩnh viễn (WaitAsync chỉ bỏ CHỜ, không hủy).
            if (read is null || read.IsCompleted)
            {
                _readingPrinterDefaults = false;
                TakeFromPrinterBtn.IsEnabled = true;
            }
            else
            {
                _ = read.ContinueWith(_ =>
                {
                    // Cửa sổ/Dispatcher đã đóng (người dùng đóng dialog trong 4s chờ) → BeginInvoke ném
                    // InvalidOperationException trong continuation KHÔNG ai await → nuốt im lặng.
                    if (Dispatcher.HasShutdownStarted) return;
                    Dispatcher.BeginInvoke(() =>
                    {
                        _readingPrinterDefaults = false;
                        TakeFromPrinterBtn.IsEnabled = true;
                    });
                }, TaskScheduler.Default);
            }
        }
    }

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
        // 2 cờ Excel lấy từ preset đã chọn (không có control trong dialog — xem ProfileCombo_SelectionChanged).
        // Chép ở ĐÂY (không phải lúc chọn preset) để nút Hủy không làm đổi cấu hình file.
        cfg.FitToPageWide = _fitWideFromPreset;
        cfg.AutoOrientation = _autoOrientFromPreset;
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

        // FitToPageWide / AutoOrientation KHÔNG ghi ở đây — giữ nguyên 2 cờ có sẵn trong cfg
        // (xem LoadFromConfig). Chip ở action bar từng row là nơi chỉnh 2 cờ này.
        // CoverPage KHÔNG ghi ở đây — giữ nguyên cờ có sẵn trong cfg (xem LoadFromConfig).
        cfg.MergeIntoOneFile = MergeCheck.IsChecked == true;
        var watermark = WatermarkText.Text.Trim();
        cfg.WatermarkText = string.IsNullOrEmpty(watermark) ? null : watermark;
        cfg.WatermarkOpacity = Math.Round(Math.Clamp(WatermarkOpacitySlider.Value, 0.1, 1.0), 2);
        cfg.WatermarkPosition = SelectedTag(WatermarkPositionCombo) ?? "center";

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
        // Booklet luôn false: combo không còn mục booklet (xem LoadFrom). Giữ field false để
        // preset cũ không mang cờ chết sang lần lưu tiếp theo.
        cfg.Booklet = false;
        cfg.PagesPerSheet = int.TryParse(perSheet, out var n) ? Math.Clamp(n, 1, 16) : 1;

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