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
                tcs.TrySetResult(PrintOnSta(job, spawnedOfficePids));
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
        SetPrinter(app, printer);
        app.Visible = false;
        app.DisplayAlerts = 0; // wdAlertsNone
        dynamic? doc = null;
        try
        {
            // KHÔNG mở Visible:false — Word.PrintOut cần "document window active" (0x800A11FD)
            doc = app.Documents.Open(job.FilePath, ReadOnly: true, AddToRecentFiles: false);
            doc.Activate();

            // Số trang THẬT từ Word (wdStatisticPages=2) — bắt buộc trước khi resolve page range
            Try(() => { var n = (int)doc.ComputeStatistics(2); if (n > 0) job.PageCount = n; });

            // Page setup: khổ giấy + chiều giấy theo cấu hình
            ApplyWordPaper(doc, job.Config.PaperSize, job.Config.Orientation);

            var (all, pages) = WordPrintArgs(job);
            var copies = Math.Max(job.Config.Copies, 1);
            // Ghi nhận trung thực: Word COM không phân biệt HƯỚNG lật cạnh (chỉ có ManualDuplexPrint bool) —
            // ShortEdge qua shim Duplex (LongEdge|ShortEdge → true) cũng in 2 mặt theo driver-default.
            // Không sửa logic; muốn chính xác hướng lật phải qua render/fallback có cờ riêng.
            // Máy in ảo (PDF/XPS) → export PDF TRỰC TIẾP bằng Word (không qua driver PDF — hết lỗi
            // "báo xong không ra file" vì ActivePrinter của PDF printer bị Word/Excel từ chối set).
            var (printToFile, outputPath) = PdfOutputArgs(job);
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
        var before = SnapshotOfficePids("EXCEL");
        var app = CreateApp("Excel.Application");
        Try(() => { if (NewOfficePid("EXCEL", before) is { } pid) spawnedOfficePids.Add(pid); });
        SetPrinter(app, printer);
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

            // Page range (từ-tới) nếu user chỉ định — áp cho từng sheet (máy vật lý)
            bool hasRange = false;
            object from = Missing.Value;
            object to = Missing.Value;
            var r = job.ResolvePhysicalPages();
            if (r.IsSuccess && r.Value!.Length > 0)
            {
                hasRange = true;
                from = r.Value[0];
                to = r.Value[^1];
            }

            // Máy in ảo (Microsoft Print to PDF/XPS...) → export PDF TRỰC TIẾP bằng Excel. In qua driver
            // PDF bị lỗi "báo xong không ra file": ActivePrinter của Microsoft Print to PDF (port PORTPROMPT)
            // bị Excel TỪ CHỐI set → in lộn sang default/ẩn hộp Save As. ExportAsFixedFormat không cần máy in.
            var (printToFile, outputPath) = PdfOutputArgs(job);

            // Áp cấu hình cho MỌI sheet + ẨN sheet trống (không in trang trắng). wb.PrintOut() mặc định
            // chỉ in ACTIVE sheet → trước đây chỉ ra sheet đầu. ExportAsFixedFormat xuất CẢ workbook,
            // sheet ẨN (trống) bỏ qua → 1 file đủ các sheet có dữ liệu (đã xác minh COM thật: 2 trang).
            foreach (var sheet in wb.Worksheets)
            {
                ApplyExcelSetup(sheet, job.Config.PaperSize, job.Config.Orientation);
                if (IsSheetBlank(sheet))
                {
                    try { sheet.Visible = 2 /* xlSheetHidden */; hidden.Add(sheet); } catch { }
                }
            }
            var hasAnyData = false;
            foreach (var sheet in wb.Worksheets)
                if (!IsSheetBlank(sheet)) { hasAnyData = true; break; }
            if (!hasAnyData)
                return Result<bool>.Fail(new PrintError
                {
                    Code = ErrorCodes.FileCorrupted,
                    Category = PrintErrorCategory.App,
                    Message = "File Excel không có sheet nào có dữ liệu để in.",
                    Hint = "Kiểm tra lại file — mọi sheet đều trống.",
                });

            if (printToFile)
            {
                wb.ExportAsFixedFormat(0 /* xlTypePDF */, outputPath);
            }
            else
            {
                // Máy VẬT LÝ → in TỪNG sheet (sheet trống đã ẩn — bỏ qua). wb.PrintOut() chỉ in ACTIVE
                // sheet → trước đây chỉ ra sheet đầu x N bản. Mỗi sheet 1 lần PrintOut → ra đủ các sheet.
                foreach (var sheet in wb.Worksheets)
                {
                    if (IsSheetBlank(sheet)) continue;
                    if (hasRange)
                        sheet.PrintOut(From: from, To: to, Copies: copies, Collate: true);
                    else
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
        SetPrinter(app, printer);
        app.Visible = false; // msoFalse
        dynamic? pres = null;
        try
        {
            // Open(FileName, ReadOnly, Untitled, WithWindow)
            pres = app.Presentations.Open(job.FilePath, ReadOnly: -1 /* msoTrue */, Untitled: 0 /* msoFalse */, WithWindow: 0 /* msoFalse */);
            var all = true;
            object from = Missing.Value;
            object to = Missing.Value;
            var r = job.ResolvePhysicalPages();
            if (r.IsSuccess && r.Value!.Length > 0)
            {
                all = false;
                from = r.Value[0];
                to = r.Value[^1];
            }
            // Máy in ảo (PDF) → export PDF TRỰC TIẾP bằng PowerPoint (không qua driver PDF).
            var (printToFile, outputPath) = PdfOutputArgs(job);
            if (printToFile)
            {
                pres.ExportAsFixedFormat(Path: outputPath, FixedFormatType: 2 /* ppFixedFormatTypePDF */);
                return Result<bool>.Ok(true);
            }
            if (all)
                pres.PrintOut(Copies: Math.Max(job.Config.Copies, 1), Collate: true);
            else
                pres.PrintOut(From: from, To: to, Copies: Math.Max(job.Config.Copies, 1), Collate: true);
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

    /// <summary>ActivePrinter = "Tên máy on Cổng:" — cần port (PrintQueue.QueuePort). Máy "mặc định" → không set.</summary>
    private static void SetPrinter(dynamic app, string? printer)
    {
        if (string.IsNullOrWhiteSpace(printer)) return;
        Try(() =>
        {
            var port = ResolvePort(printer);
            if (!string.IsNullOrEmpty(port))
                app.ActivePrinter = $"{printer} on {port}:";
        });
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
}