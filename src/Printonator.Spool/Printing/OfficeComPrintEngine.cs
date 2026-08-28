using System.Reflection;
using System.Runtime.InteropServices;
using Printonator.Core;
using Printonator.Core.Models;

namespace Printonator.Spool.Printing;

/// <summary>
/// Engine in file Office bằng CHÍNH APP GỐC của máy user (như Print Conductor làm):
/// Word/Excel/PowerPoint qua COM automation (PrintOut) — giữ đúng page setup, section,
/// printer-specific của file; không ép convert PDF. Chạy trên STA thread + timeout
/// (COM mất phản hồi khi app bận → không kẹt hàng đợi). Không nuốt lỗi.
/// Chỉ nhận format có app gốc (CanHandle); không có app → shell fallback (SpoolPrintEngine).
/// </summary>
public sealed class OfficeComPrintEngine : IPrintEngine
{
    private const int TimeoutSeconds = 60;
    private const string PrinterUnsetMarker = "[PRINTER_UNSET]";
    private static readonly string[] OfficeFormats = ["DOCX", "DOC", "RTF", "XLSX", "XLS", "XLSM", "CSV", "PPTX", "PPT", "PPSX", "PPS"];

    /// <summary>Kiểm tra app gốc — tách ra để test (máy CI không có Office).</summary>
    private readonly Func<string, OfficeAppKind> _appDetector;

    public OfficeComPrintEngine(Func<string, OfficeAppKind>? appDetector = null)
        => _appDetector = appDetector ?? InstalledApps.AppForFormat;

    public bool CanHandle(string format)
    {
        var f = format.ToUpperInvariant();
        return OfficeFormats.Contains(f) && _appDetector(f) != OfficeAppKind.None;
    }

    public Task<Result<bool>> PrintAsync(PrintJob job, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.Config.PrinterName))
            return Task.FromResult(Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.PrinterNotFound,
                Category = PrintErrorCategory.Config,
                Message = "Chưa chọn máy in.",
                Hint = "Chọn máy in (hoặc để 'mặc định').",
            }));

        // COM Office bắt buộc chạy trên STA thread — chạy nền + timeout để không kẹt hàng đợi.
        // spawnedOfficePids: theo dõi PID của instance Office MÀ ENGINE TỰ TẠO (không phải của user
        // đang mở). Nếu in bị timeout/abandon STA mà PrintOut chưa trả → app.Quit không kịp chạy,
        // instance đó thành mồ côi (24 WINWORD/12 EXCEL đã thấy). Track PID để await-side kill ĐÚNG
        // process do engine tạo khi bị bỏ rơi — không đụng Office thật của user.
        var spawnedOfficePids = new List<int>();
        var tcs = new TaskCompletionSource<Result<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                var r = PrintOnSta(job, spawnedOfficePids);
                // Office không set ActivePrinter được máy đã chọn (máy WSD/shared/offline — Office hạn chế)
                // nhưng SHELL printto vẫn in được → FALLBACK shell (đã xác minh in ra giấy LBP242/243 thật).
                if (!r.IsSuccess && r.Error?.Detail?.Contains(PrinterUnsetMarker, StringComparison.Ordinal) == true)
                {
                    OfficeLog($"Office không đặt được máy in → fallback SpoolPrintEngine (shell printto): {job.FileName}");
                    r = new SpoolPrintEngine().PrintAsync(job, ct).GetAwaiter().GetResult();
                }
                tcs.TrySetResult(r);
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetResult(Result<bool>.Fail(new PrintError
                {
                    Code = ErrorCodes.EngineTimeout,
                    Category = PrintErrorCategory.System,
                    Message = $"In {job.FileName} bị hủy giữa chừng.",
                    Hint = "Bấm in lại nếu cần.",
                }));
            }
            catch (Exception ex)
            {
                try
                {
                    // Debug log exception gốc (chỉ local, không đưa lên AI)
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "printonator-office.log"),
                        $"{DateTimeOffset.Now:O} {job.FileName}: {ex}\n");
                }
                catch { }
                tcs.TrySetResult(Result<bool>.Fail(new PrintError
                {
                    Code = ErrorCodes.OfficeAppBusy,
                    Category = PrintErrorCategory.App,
                    Message = $"App gốc lỗi khi in {job.FileName}.",
                    Hint = "Kiểm tra app đã đóng hết cửa sổ chờ vô hạn; thử in lại hoặc chọn máy khác.",
                    Detail = ex.ToString(),
                }));
            }
        })
        {
            IsBackground = true,
            Name = "OfficeComPrint",
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        return WaitWithTimeoutAsync(tcs.Task, job, ct, spawnedOfficePids);
    }

    private static async Task<Result<bool>> WaitWithTimeoutAsync(
        Task<Result<bool>> work, PrintJob job, CancellationToken ct, IReadOnlyCollection<int> spawnedOfficePids)
    {
        try
        {
            return await work.WaitAsync(TimeSpan.FromSeconds(TimeoutSeconds), ct);
        }
        catch (TimeoutException)
        {
            // Không giết được STA thread đang mượn Office — TRẢ LỖI RÕ + DỌN instance mồ côi:
            // vì PrintOut chưa trả nên app.Quit trong finally không chạy, instance Word/Excel
            // (mà CHÍNH engine này tạo) sẽ treo vô hình. Kill ĐÚNG PID engine đã spawn — tuyệt
            // đối không đụng Office người dùng đang mở (PID không nằm trong danh sách này).
            foreach (var pid in spawnedOfficePids)
            {
                try
                {
                    var p = System.Diagnostics.Process.GetProcessById(pid);
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                }
                catch { }
            }
            return Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.EngineTimeout,
                Category = PrintErrorCategory.System,
                Message = $"Hết thời gian chờ {job.FileName} in qua app gốc ({TimeoutSeconds}s). Đã dọn phiên in đang treo.",
                Hint = "App văn phòng có thể bị kẹt (dialog chờ). Thử in lại.",
            });
        }
        catch (OperationCanceledException)
        {
            return Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.EngineTimeout,
                Category = PrintErrorCategory.System,
                Message = $"In {job.FileName} đã bị hủy.",
                Hint = "Bấm in lại nếu cần.",
            });
        }
    }

    private static Result<bool> PrintOnSta(PrintJob job, ICollection<int> spawnedOfficePids)
    {
        var kind = InstalledApps.AppForFormat(job.Format);
        if (kind == OfficeAppKind.None)
            return Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.EngineNotFound,
                Category = PrintErrorCategory.App,
                Message = "Không tìm thấy app gốc để in file này.",
                Hint = "Cài MS Office hoặc dùng fallback shell.",
            });

        var pn = job.Config.PrinterName!; // đã guard non-empty ở trên
        var printer = pn.Equals("mặc định", StringComparison.OrdinalIgnoreCase)
            || pn.Equals("default", StringComparison.OrdinalIgnoreCase)
            ? null
            : pn;

        OfficeLog($"PrintOnSta kind={kind} jobPrinter='{job.Config.PrinterName}' resolved={(printer ?? "NULL(->mặc định OS)")} file={job.FileName}");

        return kind switch
        {
            OfficeAppKind.Word => PrintWithWord(job, printer, spawnedOfficePids),
            OfficeAppKind.Excel => PrintWithExcel(job, printer, spawnedOfficePids),
            OfficeAppKind.PowerPoint => PrintWithPowerPoint(job, printer, spawnedOfficePids),
            _ => Result<bool>.Fail(new PrintError
            {
                Code = ErrorCodes.EngineNotFound,
                Category = PrintErrorCategory.App,
                Message = "Định dạng không hỗ trợ app gốc.",
                Hint = "Dùng fallback shell.",
            }),
        };
    }

    /// <summary>
    /// Snapshot PID hiện có của process tên cho trước (vd WINWORD) TRƯỚC khi engine mở COM,
    /// và sau khi mở, trả về PID mới do chính engine tạo (không nằm trong snapshot trước) —
    /// để timeout có thể kill ĐÚNG process mình tạo, không đụng cái user đang mở sẵn.
    /// Snapshot này phải lấy NGAY TRƯỚC CreateApp, và detect ngay SAU CreateApp.
    /// </summary>
    private static HashSet<int> SnapshotOfficePids(string processName)
    {
        try
        {
            return new HashSet<int>(System.Diagnostics.Process.GetProcessesByName(processName).Select(p => p.Id));
        }
        catch { return new HashSet<int>(); }
    }

    /// <summary>Trả về PID process tên cho trước KHÔNG nằm trong snapshot (== engine vừa tạo), hoặc null.</summary>
    private static int? NewOfficePid(string processName, HashSet<int> before)
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(processName))
                if (!before.Contains(p.Id)) return p.Id;
        }
        catch { }
        return null;
    }

    // ============ Word ============
    private static Result<bool> PrintWithWord(PrintJob job, string? printer, ICollection<int> spawnedOfficePids)
    {
        var before = SnapshotOfficePids("WINWORD");
        var app = CreateApp("Word.Application");
        Try(() => { if (NewOfficePid("WINWORD", before) is { } pid) spawnedOfficePids.Add(pid); });
        app.Visible = false;
        app.DisplayAlerts = 0; // wdAlertsNone
        dynamic? doc = null;
        try
        {
            // Máy PDF ảo → ExportAsFixedFormat (không cần ActivePrinter). Máy vật lý → kiểm tra ActivePrinter
            // SAU khi mở doc (cần doc để cắt PDF fallback). KHÔNG in lén sang máy default (thường là PDF).
            var (printToFile, outputPath) = PdfOutputArgs(job);

            // KHÔNG mở Visible:false — Word.PrintOut cần "document window active" (0x800A11FD)
            doc = app.Documents.Open(job.FilePath, ReadOnly: true, AddToRecentFiles: false);
            doc.Activate();

            // Số trang THẬT từ Word (wdStatisticPages=2) — bắt buộc trước khi resolve page range
            Try(() => { var n = (int)doc.ComputeStatistics(2); if (n > 0) job.PageCount = n; });

            // Page setup: khổ giấy + chiều giấy theo cấu hình
            ApplyWordPaper(doc, job.Config.PaperSize, job.Config.Orientation);

            var (all, pages) = WordPrintArgs(job);
            var copies = Math.Max(job.Config.Copies, 1);

            if (!printToFile && !SetPrinter(app, printer))
            {
                // Word không set ActivePrinter (máy WSD/shared...) → FALLBACK shell. Shell chỉ in ĐƯỢC CẢ
                // FILE (không áp page range). → Có range: báo lỗi rõ; không range: marker → shell in file gốc.
                var hasRange = PrintJob.ParsePageRange(job.Config.PageRange) is { IsSuccess: true, Value.Length: > 0 };
                if (hasRange)
                    return Result<bool>.Fail(new PrintError
                    {
                        Code = ErrorCodes.PrinterNotFound,
                        Category = PrintErrorCategory.Printer,
                        Message = $"Không áp được page range \"{job.Config.PageRange}\" khi in qua shell (Word không đặt được máy in \"{printer}\" trên máy này).",
                        Hint = "Bỏ page range để in toàn bộ, hoặc chọn máy in khác mà Word đặt ActivePrinter được.",
                    });
                return Result<bool>.Fail(new PrintError
                {
                    Code = ErrorCodes.PrinterNotFound,
                    Category = PrintErrorCategory.Printer,
                    Message = $"Word không đặt được máy in \"{printer}\".",
                    Hint = "Kiểm tra máy in còn hoạt động/đang kết nối, hoặc chọn máy in khác.",
                    Detail = PrinterUnsetMarker,
                });
            }
            // Ghi nhận trung thực: Word COM không phân biệt HƯỚNG lật cạnh (chỉ có ManualDuplexPrint bool) —
            // ShortEdge qua shim Duplex (LongEdge|ShortEdge → true) cũng in 2 mặt theo driver-default.
            // Không sửa logic; muốn chính xác hướng lật phải qua render/fallback có cờ riêng.
            // Máy in ảo (PDF/XPS) → export PDF TRỰC TIẾP bằng Word (không qua driver PDF — hết lỗi
            // "báo xong không ra file" vì ActivePrinter của PDF printer bị Word/Excel từ chối set).
            if (printToFile)
            {
                doc.ExportAsFixedFormat(OutputFileName: outputPath, ExportFormat: 17 /* wdExportFormatPDF */);
                return Result<bool>.Ok(true);
            }
            if (all)
                doc.PrintOut(Background: false, Range: 0 /* wdPrintAllDocument */,
                    Copies: copies, ManualDuplexPrint: job.Config.Duplex);
            else
                // wdPrintRangeOfPages (4) + Pages string — chỉ dùng bool/int/string
                // (object/Missing/positional 19 làm COM binder crash "argument 0")
                doc.PrintOut(Background: false, Range: 4 /* wdPrintRangeOfPages */, Pages: pages,
                    Copies: copies, ManualDuplexPrint: job.Config.Duplex);
            return Result<bool>.Ok(true);
        }
        finally
        {
            Try(() => doc?.Close(SaveChanges: 0));
            Try(() => app.Quit());
            Try(() => Marshal.FinalReleaseComObject(app));
        }
    }

    /// <summary>
    /// Chuẩn bị tham số in Word: all=true → in toàn bộ; else Pages string
    /// (ResolvePhysicalPages chạy với PageCount THẬT của Word đã set trước đó).
    /// </summary>
    private static (bool all, string pages) WordPrintArgs(PrintJob job)
    {
        var r = job.ResolvePhysicalPages();
        if (!r.IsSuccess || r.Value!.Length == 0)
            return (true, "");
        return (false, string.Join(",", r.Value));
    }

    private static void ApplyWordPaper(dynamic doc, string paperSize, PrintOrientation orientation)
    {
        Try(() =>
        {
            if (WordPaperCode.TryGetValue(paperSize.Trim(), out var wdCode))
                doc.PageSetup.PaperSize = wdCode;
            // AsDocument/AsPrinter: không ép chiều — giữ chiều trong file hoặc driver quyết
            if (orientation == PrintOrientation.Portrait)
                doc.PageSetup.Orientation = 0; // wdOrientPortrait
            else if (orientation == PrintOrientation.Landscape)
                doc.PageSetup.Orientation = 1; // wdOrientLandscape
        });
    }

    // ============ Excel ============
    private static Result<bool> PrintWithExcel(PrintJob job, string? printer, ICollection<int> spawnedOfficePids)
    {
        // Máy PDF ảo → ExportAsFixedFormat (không cần máy in). Máy vật lý → đổi MẶC ĐỊNH Windows tạm sang máy
        // đã chọn TRƯỚC khi mở Excel: Excel đọc máy mặc định lúc khởi tạo → dùng đúng máy. (Set ActivePrinter
        // property bị Excel TỪ CHỐI trên máy WSD/mạng; đã xác minh: đổi default thì PrintOut range chạy được.)
        var (printToFile, outputPath) = PdfOutputArgs(job);
        string? prevDefault = null;
        if (!printToFile && !string.IsNullOrWhiteSpace(printer))
        {
            prevDefault = GetWindowsDefaultPrinter();
            SetWindowsDefaultPrinter(printer);
        }
        var before = SnapshotOfficePids("EXCEL");
        var app = CreateApp("Excel.Application");
        Try(() => { if (NewOfficePid("EXCEL", before) is { } pid) spawnedOfficePids.Add(pid); });
        app.Visible = false;
        app.DisplayAlerts = false;
        dynamic? wb = null;
        List<dynamic> hidden = new(); // sheet trống bị ẩn tạm (không in trang trắng) — trả lại khi xong
        try
        {
            // AddToMru (KHÔNG phải AddToRecentFiles — cái đó là của Word Documents.Open): Excel Workbooks.Open
            // dùng AddToMru. Tên sai → COM binder DISP_E_UNKNOWNNAME (0x80020006) → MỌI file Excel báo
            // "App gốc lỗi khi in" (đã xác minh COM thật: sai lỗi đúng, AddToMru mở OK).
            wb = app.Workbooks.Open(job.FilePath, ReadOnly: true, AddToMru: false, UpdateLinks: 0);
            var copies = Math.Max(job.Config.Copies, 1);

            // Page range: Excel in theo TỪ-TỚI của TỪNG sheet (PrintOut From/To). Range KHÔNG liên tiếp
            // ("1-2,4" bỏ trang 3) → nhóm trang liên tiếp (GroupPages) để in đúng. Không phụ thuộc PageCount
            // (app không đếm trang Excel đáng tin — GET.DOCUMENT trả sai khi đổi sheet).
            bool hasRange = false;
            object from = Missing.Value;
            object to = Missing.Value;
            int[]? pages = null;
            var pr = PrintJob.ParsePageRange(job.Config.PageRange);
            if (pr.IsSuccess && pr.Value!.Length > 0)
            {
                hasRange = true;
                pages = pr.Value;
                from = pages[0];
                to = pages[^1];
            }

            // Áp cấu hình + ẨN sheet không cần in: sheet TRỐNG hoặc KHÔNG phải sheet đã chọn (SheetName —
            // user chọn qua dropdown, áp hàng loạt file). ExportAsFixedFormat xuất CẢ workbook, sheet ẨN bỏ qua.
            // File KHÔNG có sheet đã chọn (tên sheet khác nhau giữa các file trong lô) → fallback in TẤT CẢ.
            var sheetFilter = job.Config.SheetName;
            if (!string.IsNullOrWhiteSpace(sheetFilter))
            {
                var anyMatch = false;
                foreach (var s in wb.Worksheets)
                {
                    try { if (!IsSheetBlank(s) && s.Name.Equals(sheetFilter, StringComparison.OrdinalIgnoreCase)) { anyMatch = true; break; } }
                    catch { }
                }
                if (!anyMatch)
                {
                    OfficeLog($"Excel: sheet '{sheetFilter}' không tồn tại trong file '{job.FileName}' — in tất cả sheet");
                    sheetFilter = null;
                }
            }
            var keptSheets = new List<dynamic>();
            foreach (var sheet in wb.Worksheets)
            {
                var keep = !IsSheetBlank(sheet)
                    && (string.IsNullOrWhiteSpace(sheetFilter)
                        || sheet.Name.Equals(sheetFilter, StringComparison.OrdinalIgnoreCase));
                if (keep)
                {
                    keptSheets.Add(sheet);
                    ApplyExcelSetup(sheet, job.Config.PaperSize, job.Config.Orientation);
                    ApplyExcelPageExtras(sheet, job.Config);
                }
                else
                {
                    try { sheet.Visible = 2 /* xlSheetHidden */; hidden.Add(sheet); } catch { }
                }
            }
            if (keptSheets.Count == 0)
                return Result<bool>.Fail(new PrintError
                {
                    Code = ErrorCodes.FileCorrupted,
                    Category = PrintErrorCategory.App,
                    Message = "File Excel không có sheet nào để in.",
                    Hint = "Kiểm tra tên sheet đã chọn, hoặc file có sheet trống không.",
                });

            if (printToFile)
            {
                // From/To = Missing.Value khi không có range → export CẢ workbook; có range → đúng trang.
                wb.ExportAsFixedFormat(0 /* xlTypePDF */, outputPath, 0 /* xlQualityStandard */, false, false, from, to);
            }
            else
            {
                if (hasRange && pages is not null)
                {
                    // Range "1-3,5" = trang của CẢ TẬP sheet được in (số trang GLOBAL cộng dồn các sheet được
                    // giữ) — KHÔNG áp từng sheet (lỗi cũ: mỗi sheet in 1,2,3,5 → "cả đống"). Map global → đúng trang.
                    var sheetPages = new List<(dynamic Sheet, int Pages, int Start)>();
                    int offset = 0;
                    foreach (var sheet in keptSheets)
                    {
                        var n = SheetPageCount(sheet);
                        sheetPages.Add((sheet, n, offset));
                        offset += n;
                    }
                    var mapped = MapGlobalPages(pages, sheetPages);
                    OfficeLog($"Excel range '{job.Config.PageRange}' → " + string.Join("; ", mapped.Select(m => $"'{m.Item1.Name}' pages {string.Join(",", m.Item2.Select(g => $"{g.From}-{g.To}"))}")));
                    foreach (var (sheet, groups) in mapped)
                        foreach (var (f, t) in groups)
                            sheet.PrintOut(From: f, To: t, Copies: copies, Collate: true);
                }
                else
                {
                    foreach (var sheet in keptSheets)
                        sheet.PrintOut(Copies: copies, Collate: true);
                }
            }
            return Result<bool>.Ok(true);
        }
        finally
        {
            foreach (var s in hidden) Try(() => s.Visible = -1 /* xlSheetVisible */);
            Try(() => wb?.Close(SaveChanges: false));
            Try(() => app.Quit());
            Try(() => Marshal.FinalReleaseComObject(app));
            if (prevDefault is not null) SetWindowsDefaultPrinter(prevDefault); // khôi phục máy in mặc định
        }
    }

    private static void ApplyExcelSetup(dynamic sheet, string paperSize, PrintOrientation orientation)
    {
        Try(() =>
        {
            if (ExcelPaperCode.TryGetValue(paperSize.Trim(), out var xlCode))
                sheet.PageSetup.PaperSize = xlCode;
            // AsDocument/AsPrinter: không ép chiều — giữ chiều trong file hoặc driver quyết
            if (orientation == PrintOrientation.Portrait)
                sheet.PageSetup.Orientation = 1; // xlPortrait
            else if (orientation == PrintOrientation.Landscape)
                sheet.PageSetup.Orientation = 2; // xlLandscape
        });
    }

    /// <summary>Excel: Fit tất cả cột vào 1 trang ngang + tự chọn chiều giấy theo nội dung (option user chọn).</summary>
    private static void ApplyExcelPageExtras(dynamic sheet, PrintConfig cfg)
    {
        Try(() =>
        {
            if (cfg.AutoOrientation)
            {
                // Vùng dữ liệu rộng >= cao → landscape; ngược lại portrait (đo bằng điểm in).
                var used = sheet.UsedRange;
                var wPt = (double)used.Width;
                var hPt = (double)used.Height;
                sheet.PageSetup.Orientation = wPt >= hPt ? 2 /* xlLandscape */ : 1 /* xlPortrait */;
            }
            if (cfg.FitToPageWide)
            {
                // FitToPagesWide=1 + FitToPagesTall=false → gom cột vào 1 trang ngang, cao theo nội dung.
                sheet.PageSetup.Zoom = false;               // bật chế độ FitToPages
                sheet.PageSetup.FitToPagesWide = 1;
                sheet.PageSetup.FitToPagesTall = false;
            }
        });
    }

    /// <summary>Sheet trống = UsedRange không có ô dữ liệu (chỉ A1 rỗng). File xuất từ thiết bị hay
    /// kèm sheet template rỗng — bỏ qua để không in trang trắng. Lỗi đọc → coi là CÓ dữ liệu (an toàn).</summary>
    private static bool IsSheetBlank(dynamic sheet)
    {
        try
        {
            var used = sheet.UsedRange;
            if (used is null) return true;
            int rows = (int)used.Rows.Count;
            int cols = (int)used.Columns.Count;
            if (rows > 1 || cols > 1) return false;
            var v = used.Cells.Item(1, 1).Value2;
            return v is null || (v is string s && s.Length == 0);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Máy in ẢO (PDF/XPS/OneNote/Adobe…) → trả PrintToFile=true + đường dẫn PDF cạnh file gốc
    /// (dùng chung PrinterService.PdfOutputPath), để engine lưu thẳng file xuất — không đẩy vào spooler
    /// PDF printer (mở hộp "Save As" vô hình → "báo xong không ra file"). Máy vật lý → (false, null).</summary>
    private static (bool printToFile, string? outputPath) PdfOutputArgs(PrintJob job)
    {
        var p = PrinterService.PdfOutputPath(job);
        return p is null ? (false, null) : (true, p);
    }

    // ============ PowerPoint ============
    private static Result<bool> PrintWithPowerPoint(PrintJob job, string? printer, ICollection<int> spawnedOfficePids)
    {
        var before = SnapshotOfficePids("POWERPNT");
        var app = CreateApp("PowerPoint.Application");
        Try(() => { if (NewOfficePid("POWERPNT", before) is { } pid) spawnedOfficePids.Add(pid); });
        app.Visible = false; // msoFalse
        dynamic? pres = null;
        try
        {
            var (printToFile, outputPath) = PdfOutputArgs(job);

            // Open(FileName, ReadOnly, Untitled, WithWindow)
            pres = app.Presentations.Open(job.FilePath, ReadOnly: -1 /* msoTrue */, Untitled: 0 /* msoFalse */, WithWindow: 0 /* msoFalse */);
            var copies = Math.Max(job.Config.Copies, 1);

            // Page range: PPT in theo TỪ-TỚI; range KHÔNG liên tiếp → nhóm trang liên tiếp để in đúng.
            bool hasRange = false;
            object from = Missing.Value;
            object to = Missing.Value;
            int[]? pages = null;
            var pr = PrintJob.ParsePageRange(job.Config.PageRange);
            if (pr.IsSuccess && pr.Value!.Length > 0)
            {
                hasRange = true;
                pages = pr.Value;
                from = pages[0];
                to = pages[^1];
            }

            // Máy vật lý: đặt ActivePrinter đúng máy; không đặt được → FALLBACK shell (in file gốc).
            if (!printToFile && !SetPrinter(app, printer))
                return Result<bool>.Fail(new PrintError
                {
                    Code = ErrorCodes.PrinterNotFound,
                    Category = PrintErrorCategory.Printer,
                    Message = $"PowerPoint không đặt được máy in \"{printer}\".",
                    Hint = "Kiểm tra máy in còn hoạt động/đang kết nối, hoặc chọn máy in khác.",
                    Detail = PrinterUnsetMarker,
                });

            if (printToFile)
            {
                pres.ExportAsFixedFormat(Path: outputPath, FixedFormatType: 2 /* ppFixedFormatTypePDF */);
                return Result<bool>.Ok(true);
            }
            if (hasRange && pages is not null)
            {
                foreach (var g in GroupPages(pages))
                    pres.PrintOut(From: g.From, To: g.To, Copies: copies, Collate: true);
            }
            else
                pres.PrintOut(Copies: copies, Collate: true);
            return Result<bool>.Ok(true);
        }
        finally
        {
            Try(() => pres?.Close());
            Try(() => app.Quit());
            Try(() => Marshal.FinalReleaseComObject(app));
        }
    }

    // ============ Chung ============
    private static dynamic CreateApp(string progId)
    {
        var t = Type.GetTypeFromProgID(progId)
            ?? throw new COMException($"Không tìm thấy COM server {progId}.");
        return Activator.CreateInstance(t)!;
    }

    /// <summary>Đặt ActivePrinter = máy in user chọn + XÁC MINH. Trả false khi Office không đặt được
    /// (máy WSD/shared/offline...) → engine phải FAIL RÕ, KHÔNG in lén sang default (thường là PDF →
    /// hộp "Save As" lạ — đã xác minh trên máy thật: Excel không set ActivePrinter sang máy nào).</summary>
    private static bool SetPrinter(dynamic app, string? printer)
    {
        if (string.IsNullOrWhiteSpace(printer))
        {
            OfficeLog("SetPrinter: bỏ qua (printer rỗng/mặc định) — dùng máy default của Office");
            return true; // "mặc định" — user chủ ý dùng default
        }
        try
        {
            var port = ResolvePort(printer);
            app.ActivePrinter = !string.IsNullOrEmpty(port) ? $"{printer} on {port}:" : printer;
            var cur = (string)app.ActivePrinter;
            var ok = cur.StartsWith(printer, StringComparison.OrdinalIgnoreCase);
            OfficeLog(ok ? $"SetPrinter OK: '{cur}'" : $"SetPrinter VERIFY FAIL: yêu cầu '{printer}' nhưng Office là '{cur}'");
            return ok;
        }
        catch (Exception ex)
        {
            OfficeLog($"SetPrinter EX: {ex.Message}");
            return false;
        }
    }

    /// <summary>Đọc máy in mặc định Windows hiện tại (để khôi phục sau khi đổi tạm cho Excel).</summary>
    private static string? GetWindowsDefaultPrinter()
    {
        try
        {
            using var server = new System.Printing.LocalPrintServer();
            return server.DefaultPrintQueue?.Name;
        }
        catch { return null; }
    }

    /// <summary>Đổi máy in mặc định Windows (WScript.Network — không cần admin). Excel đọc máy mặc định lúc
    /// khởi tạo → dùng đúng máy đã chọn (cách duy nhất in được máy WSD/mạng mà Excel từ chối set ActivePrinter).</summary>
    private static void SetWindowsDefaultPrinter(string name)
    {
        try
        {
            dynamic ws = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Network")!)!;
            ws.SetDefaultPrinter(name);
            Marshal.FinalReleaseComObject(ws);
        }
        catch { /* nếu không đổi được → Excel dùng default cũ (best-effort) */ }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string[] Sheets, DateTime Time)> SheetCache = new(System.StringComparer.OrdinalIgnoreCase);
    private static readonly object ProbeThreadLock = new();
    private static System.Windows.Threading.Dispatcher? _probeDispatcher;
    private static dynamic? _probeApp;   // 1 Excel app DÙNG CHUNG cho mọi probe (launch 1 lần, sau ~100ms)

    /// <summary>Liệt kê tên sheet cho UI dropdown chọn sheet in. TỐI ƯU: .xlsx/.xlsm đọc thẳng ZIP workbook.xml
    /// (tức thì, KHÔNG mở Excel); .xls (binary) probe trên 1 thread STA DÙNG CHUNG + 1 Excel app dùng chung
    /// (launch 1 lần ~5s, các probe sau chỉ ~100ms) + cache theo thời gian sửa file.
    /// Mở lỗi / không phải Excel → mảng rỗng (UI ẩn combo).</summary>
    public static System.Threading.Tasks.Task<string[]> ListSheetsAsync(string filePath)
    {
        // .xlsx = ZIP: đọc xl/workbook.xml — nhanh, không cần Excel COM
        if (filePath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
            || filePath.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase))
            return System.Threading.Tasks.Task.FromResult(ListSheetsXlsx(filePath));

        // .xls (binary) — cache theo LastWriteTime (file hiếm đổi) + probe trên thread STA dùng chung
        var mtime = System.IO.File.GetLastWriteTimeUtc(filePath).Ticks;
        if (SheetCache.TryGetValue(filePath, out var hit) && hit.Time.Ticks == mtime)
            return System.Threading.Tasks.Task.FromResult(hit.Sheets);

        var dispatcher = EnsureProbeDispatcher();
        return dispatcher.InvokeAsync(() =>
        {
            var names = ProbeSheetsOnSta(filePath);
            SheetCache[filePath] = (names, System.IO.File.GetLastWriteTimeUtc(filePath));
            return names;
        }).Task;
    }

    /// <summary>Tạo 1 thread STA dùng chung (message pump) — Excel COM bị thread-affine, phải probe đúng 1 thread.</summary>
    private static System.Windows.Threading.Dispatcher EnsureProbeDispatcher()
    {
        lock (ProbeThreadLock)
        {
            if (_probeDispatcher is not null) return _probeDispatcher;
            var tcs = new TaskCompletionSource<System.Windows.Threading.Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
            var t = new Thread(() =>
            {
                tcs.SetResult(System.Windows.Threading.Dispatcher.CurrentDispatcher);
                System.Windows.Threading.Dispatcher.Run();
            })
            { IsBackground = true, Name = "ExcelSheetProbe" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            _probeDispatcher = tcs.Task.Result;
            return _probeDispatcher;
        }
    }

    /// <summary>.xlsx/.xlsm (thực chất là ZIP): đọc xl/workbook.xml → danh sách &lt;sheet name=...&gt;. Không mở Excel.</summary>
    private static string[] ListSheetsXlsx(string filePath)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(filePath);
            var entry = zip.GetEntry("xl/workbook.xml");
            if (entry is null) return [];
            using var reader = new System.IO.StreamReader(entry.Open());
            var xml = reader.ReadToEnd();
            var names = new List<string>();
            // <sheet> (default ns, KHÔNG prefix) hoặc <x:sheet> (namespaced) + thuộc tính name.
            // \w*:?sheet — khớp cả "<sheet name=" lẫn "<x:sheet name=" (regex \w+:?sheet chỉ khớp loại có prefix).
            var matches = System.Text.RegularExpressions.Regex.Matches(xml, @"<\w*:?sheet[^>]*name=""([^""]+)""");
            foreach (System.Text.RegularExpressions.Match m in matches) names.Add(m.Groups[1].Value);
            return names.ToArray();
        }
        catch { return []; }
    }

    /// <summary>.xls (binary): probe trên thread STA dùng chung với 1 Excel app DÙNG CHUNG — launch 1 lần
    /// (~5s), các probe sau chỉ cần Workbooks.Open (~100ms). App hỏng → dựng lại lần sau.</summary>
    private static string[] ProbeSheetsOnSta(string filePath)
    {
        try
        {
            if (_probeApp is null)
            {
                _probeApp = CreateApp("Excel.Application");
                _probeApp.Visible = false;
                _probeApp.DisplayAlerts = false;
            }
            dynamic wb = _probeApp.Workbooks.Open(filePath, ReadOnly: true, AddToMru: false, UpdateLinks: 0);
            var names = new List<string>();
            foreach (var s in wb.Worksheets) names.Add(s.Name);
            wb.Close(false);
            return names.ToArray();
        }
        catch
        {
            try { _probeApp?.Quit(); } catch { }
            _probeApp = null;
            return [];
        }
    }

    /// <summary>Ghi log chẩn đoán in Office (máy nào, đường nào) — đọc để bắt lỗi "in ra PDF".</summary>
    private static void OfficeLog(string msg)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "printonator-office.log"),
                $"{DateTimeOffset.Now:O} {msg}\n");
        }
        catch { }
    }

    /// <summary>Lấy cổng máy in (vd "Ne06:", "USB001") qua System.Printing — không cần WMI.</summary>
    internal static string? ResolvePort(string printerName)
    {
        try
        {
            using var server = new System.Printing.LocalPrintServer();
            var q = server.GetPrintQueue(printerName);
            return q?.QueuePort?.Name;
        }
        catch { return null; }
    }

    /// <summary>wPaper* (Word) theo tên khổ chuẩn; bỏ qua khổ lạ (giữ default file).</summary>
    private static readonly Dictionary<string, int> WordPaperCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A0"] = 69,
        ["A1"] = 68,
        ["A2"] = 66,
        ["A3"] = 8,
        ["A4"] = 7,
        ["A5"] = 11,
        ["A6"] = 70,
        ["B5"] = 13,
        ["Letter"] = 1,
        ["Legal"] = 5,
        ["Ledger"] = 4,
        ["Executive"] = 7,
    };

    /// <summary>xlPaper* (Excel) theo tên khổ chuẩn.</summary>
    private static readonly Dictionary<string, int> ExcelPaperCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A0"] = 68,
        ["A1"] = 67,
        ["A2"] = 66,
        ["A3"] = 8,
        ["A4"] = 9,
        ["A5"] = 11,
        ["A6"] = 70,
        ["B5"] = 13,
        ["Letter"] = 1,
        ["Legal"] = 5,
        ["Ledger"] = 4,
        ["Executive"] = 7,
    };

    private static void Try(Action a)
    {
        try { a(); } catch { /* dọn dẹp COM — lỗi ở đây không được che lỗi gốc */ }
    }

    /// <summary>Số trang in của 1 sheet (GET.DOCUMENT(50) — cần sheet ACTIVE; sai layout → trả 1 an toàn).
    /// Dùng để map range sang số trang GLOBAL cộng dồn các sheet (file nhiều sheet in đúng "1-3,5" = 4 trang).</summary>
    private static int SheetPageCount(dynamic sheet)
    {
        try
        {
            sheet.Activate();
            var n = (int)sheet.Application.ExecuteExcel4Macro("GET.DOCUMENT(50)");
            return n > 0 ? n : 1;
        }
        catch { return 1; }
    }

    /// <summary>Map range (số trang GLOBAL) → (sheet, nhóm trang cục bộ). "1-3,5" với Form=5 trang + CMC=1 trang
    /// → Form in (1,3),(5,5); CMC KHÔNG in (global 6 ngoài range) — đúng ý "trang 1,2,3,5 của cả file".</summary>
    private static List<(dynamic Sheet, List<(int From, int To)> Groups)> MapGlobalPages(
        int[] pages, List<(dynamic Sheet, int Pages, int Start)> sheetPages)
    {
        var result = new List<(dynamic, List<(int, int)>)>();
        foreach (var sp in sheetPages)
        {
            var local = new List<int>();
            foreach (var p in pages)
            {
                var l = p - sp.Start;
                if (l >= 1 && l <= sp.Pages) local.Add(l);
            }
            if (local.Count == 0) continue;
            result.Add((sp.Sheet, GroupPages(local.ToArray())));
        }
        return result;
    }

    /// <summary>Nhóm trang LIÊN TIẾP thành các khoảng — vì Excel/PowerPoint PrintOut(From,To) chỉ in dãy
    /// liên tục. "1-2,4" (bỏ trang 3) → [[1,2],[4]] → in (1,2) rồi (4,4) để đúng range.</summary>
    private static List<(int From, int To)> GroupPages(int[] pages)
    {
        var groups = new List<(int, int)>();
        if (pages.Length == 0) return groups;
        int start = pages[0], prev = pages[0];
        for (var i = 1; i < pages.Length; i++)
        {
            if (pages[i] == prev + 1) { prev = pages[i]; continue; }
            groups.Add((start, prev));
            start = pages[i]; prev = pages[i];
        }
        groups.Add((start, prev));
        return groups;
    }
}