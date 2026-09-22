using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Printonator.Core.Models;
using Printonator.Core.Printing;

namespace Printonator.Spool.Printing;

/// <summary>Đếm số trang TRƯỚC khi in (dùng cho trang bìa) — vì PageCount chỉ được điền trong lúc in.</summary>
public static class PageCountProber
{
    /// <summary>
    /// Probe PageCount cho lô job TRƯỚC khi dựng trang bìa. Theo format:
    /// PDF → WindowsPdfRasterizer (async, ~10-100ms, không mở app). Ảnh → luôn 1 trang.
    /// Office → tuần tự Word rồi Excel rồi PPT, mỗi nhóm MỘT session cho cả lô (không mở song song).
    /// TXT/CSV → GIỮ "?": số trang phụ thuộc font/wrap của browser, không ước lượng bừa
    /// (đo bừa sai còn tệ hơn "?"; CSV IsExcel=false nên không lọt probe Excel — để luôn ở nhánh này).
    /// KHÔNG BAO GIỜ ném ra ngoài: từng nhánh có try/catch riêng; job đếm lỗi → PageCount=0 → bìa "?".
    /// </summary>
    public static async Task ProbeAsync(IEnumerable<PrintJob> jobs, CancellationToken ct)
    {
        var targets = jobs.Where(j => j.PageCount <= 0 && File.Exists(j.FilePath)).ToList();
        if (targets.Count == 0) return; // không có gì để đếm → 0 chi phí, không mở app nào

        // ===== 1) PDF — async, rẻ, chạy trước =====
        foreach (var job in targets.Where(j => j.Format.Equals("PDF", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var n = await WindowsPdfRasterizer.PdfPageCountReliableAsync(job.FilePath, ct).ConfigureAwait(false);
                if (n > 0) job.PageCount = n;
            }
            catch { /* PageCount giữ 0 → bìa "?" */ }
        }

        // ===== 2) Ảnh — luôn 1 trang, không cần mở gì =====
        foreach (var job in targets.Where(j => FileFormatRegistry.ImageFormats.Contains(
                     j.Format, StringComparer.OrdinalIgnoreCase)))
            job.PageCount = 1;

        // ===== 3) Office — tuần tự từng nhóm (1 session/nhóm cho cả lô), không mở 3 app song song =====
        var wordJobs = targets.Where(j => j.PageCount <= 0
            && InstalledApps.AppForFormat(j.Format) == OfficeAppKind.Word).ToList();
        // CSV lọt AppForFormat=Excel nhưng IsExcel=false → ProbeExcelPageCounts không nhận → để ở TXT ("?")
        var excelJobs = targets.Where(j => j.PageCount <= 0 && j.IsExcel).ToList();
        var pptJobs = targets.Where(j => j.PageCount <= 0
            && InstalledApps.AppForFormat(j.Format) == OfficeAppKind.PowerPoint).ToList();

        // ===== 4) TXT/CSV — để "?" (xem docstring) =====

        // Office app treo là chuyện của CẢ BỘ Office trên máy (1 file hỏng → app kẹt), nên khi 1 nhóm
        // hết hạn thì bỏ luôn các nhóm sau — tránh cộng dồn 15s×3 = 45s chờ vô ích cho bìa.
        var officeTimedOut = false;

        if (wordJobs.Count > 0)
            officeTimedOut = !RunOfficeProbe("WINWORD", "WordPageCountProbe", "Word.Application", visibleIsBool: true, body: app =>
            {
                foreach (var job in wordJobs)
                {
                    dynamic? doc = null;
                    try
                    {
                        // Word dùng AddToRecentFiles (KHÔNG phải AddToMru — của Excel Workbooks.Open;
                        // sai tên → COM binder DISP_E_UNKNOWNNAME, bug đã xảy ra trong repo).
                        doc = app.Documents.Open(job.FilePath, ReadOnly: true, AddToRecentFiles: false);
                        var n = (int)doc.ComputeStatistics(2); // wdStatisticPages
                        if (n > 0) job.PageCount = n;
                    }
                    catch { /* PageCount giữ 0 → bìa "?" */ }
                    finally { Try(() => doc?.Close(SaveChanges: 0)); }
                }
            });

        if (excelJobs.Count > 0 && !officeTimedOut)
        {
            try { officeTimedOut = !OfficeComPrintEngine.ProbeExcelPageCounts(excelJobs); }
            catch { /* PageCount giữ 0 → bìa "?" */ }
        }

        if (pptJobs.Count > 0 && !officeTimedOut)
            RunOfficeProbe("POWERPNT", "PptPageCountProbe", "PowerPoint.Application", visibleIsBool: false, body: app =>
            {
                foreach (var job in pptJobs)
                {
                    dynamic? pres = null;
                    try
                    {
                        pres = app.Presentations.Open(job.FilePath,
                            ReadOnly: -1 /* msoTrue */, Untitled: 0 /* msoFalse */, WithWindow: 0 /* msoFalse */);
                        var n = (int)pres.Slides.Count;   // 1 slide = 1 trang khi in
                        if (n > 0) job.PageCount = n;
                    }
                    catch { /* PageCount giữ 0 → bìa "?" */ }
                    finally { Try(() => pres?.Close()); }
                }
            });
    }

    /// <summary>
    /// Chạy 1 phiên Office COM trên thread STA riêng, có HẠN CHỜ. Quá hạn → KILL đúng process
    /// engine tự spawn (không đụng Office user đang mở) rồi trả về.
    ///
    /// VÌ SAO PHẢI KILL: Join hết hạn chỉ bỏ chờ — thread nền VẪN chạy với COM app đang mở, không ai
    /// gọi Quit() → EXCEL.EXE/WINWORD.EXE mồ côi (đã gặp thật: 1 file .xlsx làm Workbooks.Open treo
    /// quá hạn, để lại Excel treo nền + chậm bìa). Kill theo PID để bìa không bị đơ vô ích.
    ///
    /// Trả true = xong trong hạn (15s — file thường probe ~4s); false = hết hạn/treo.
    /// </summary>
    private static bool RunOfficeProbe(string procName, string threadName, string progId, bool visibleIsBool, Action<dynamic> body)
    {
        var spawnedPid = new int[1];
        var worker = new Thread(() =>
        {
            var before = SnapshotPids(procName);
            try
            {
                var app = CreateApp(progId);
                spawnedPid[0] = NewPidOf(procName, before);   // ghi PID trước khi làm việc nặng
                // Word/Excel: Visible là bool. PPT: MsoTriState (int) — gán bool sẽ binding fail, và
                // Presentations.Open(WithWindow:0) đã mở ẩn nên không cần set.
                if (visibleIsBool) Try(() => app.Visible = false);
                if (progId.StartsWith("Word", StringComparison.Ordinal)) Try(() => app.DisplayAlerts = 0);
                try { body(app); }
                finally
                {
                    Try(() => app.Quit());
                    Try(() => Marshal.FinalReleaseComObject(app));
                }
            }
            catch { /* app không có / không mở được → bỏ nhóm (PageCount giữ 0 → bìa "?") */ }
        })
        { IsBackground = true, Name = threadName };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        if (worker.Join(TimeSpan.FromSeconds(15))) return true;
        var pid = Volatile.Read(ref spawnedPid[0]);
        if (pid > 0)
        {
            try
            {
                var p = System.Diagnostics.Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
            catch { }
        }
        return false;
    }

    private static HashSet<int> SnapshotPids(string processName)
    {
        try { return new HashSet<int>(System.Diagnostics.Process.GetProcessesByName(processName).Select(p => p.Id)); }
        catch { return new HashSet<int>(); }
    }

    private static int NewPidOf(string processName, HashSet<int> before)
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(processName))
                if (!before.Contains(p.Id)) return p.Id;
        }
        catch { }
        return 0;
    }

    /// <summary>Tạo COM app theo ProgID — không có → COMException (caller catch, bỏ nhóm).</summary>
    private static dynamic CreateApp(string progId)
    {
        var t = Type.GetTypeFromProgID(progId)
            ?? throw new COMException($"Không tìm thấy COM server {progId}.");
        return Activator.CreateInstance(t)!;
    }

    /// <summary>Dọn COM — lỗi cleanup không được che lỗi gốc (như OfficeComPrintEngine.Try).</summary>
    private static void Try(Action a)
    {
        try { a(); } catch { }
    }
}
