using System.Runtime.InteropServices;
using Printonator.Core.Models;
using Printonator.Spool.Printing;

namespace Printonator.Spool.Tests;

/// <summary>
/// Kiểm chứng ĐẦU-CUỐI đường đặt khổ giấy cho Word: gọi COM thật, đi qua chính
/// <see cref="OfficeComPrintEngine.ApplyWordPaper"/> của production, rồi ĐỌC LẠI PageWidth/PageHeight
/// bằng point. Đây là lớp duy nhất phát hiện được lỗi "mã khổ sai hệ số": Word NUỐT IM LẶNG mã hợp lệ
/// nhưng khác nghĩa (đặt 8 tưởng là A3, Word hiểu A4, không ném lỗi, job vẫn báo Done).
///
/// Chỉ chạy trên máy có Office — xem <see cref="OfficeFactAttribute"/>.
/// </summary>
[Collection(RealOfficeCollection.Name)]
public class OfficeRealPaperTests
{
    /// <summary>Word COM là out-of-process: chạy song song với test khác dùng chung máy in ảo sẽ
    /// tranh nhau hàng đợi. Serialize cả bộ test dùng Office thật.</summary>
    public static class RealOfficeCollection
    {
        public const string Name = "RealOffice";
    }

    /// <summary>1 mm = 72/25.4 point. Word làm tròn/clamp nên cho dung sai 2 pt (≈0.7 mm) — trừ
    /// trường hợp tự đặt kích thước, nơi dung sai chặt hơn vì ở đó công thức mm→pt mới là thứ đang
    /// được kiểm (xem AssertSizeExact).</summary>
    private static void AssertSize(double actualPt, int expectedMm, string what)
    {
        var expected = expectedMm * 72.0 / 25.4;
        Assert.True(Math.Abs(actualPt - expected) < 2.0,
            $"{what}: Word trả {actualPt:F1} pt, mong đợi {expected:F1} pt ({expectedMm} mm)");
    }

    /// <summary>Dung sai chặt cho nhánh tự đặt kích thước. KHÔNG suy kỳ vọng từ công thức của
    /// production (72/25.4) — nếu công thức đó sai thì test cũng sai theo và vẫn xanh. Dùng số đo
    /// thật đã xác minh trên Word 16.0.</summary>
    private static void MatchesMeasured(double actualPt, double measuredPt, string what)
        => Assert.True(Math.Abs(actualPt - measuredPt) < 0.5,
            $"{what}: Word trả {actualPt:F1} pt, số đo chuẩn {measuredPt:F1} pt");

    /// <summary>Word COM sống ngoài tiến trình: một hộp thoại ẩn (kích hoạt bản quyền, file đang mở)
    /// làm treo cả lượt chạy. Chặn bằng timeout thay vì để testhost bị kill giữa finally (mất dọn dẹp).
    /// </summary>
    private const int ComTimeoutMs = 120_000;

    [OfficeFact(Timeout = ComTimeoutMs)]
    public void Word_dat_kho_A3_ra_dung_A3_khong_phai_A4()
        => RunOnWordDoc("t.docx", d =>
        {
            // ĐÚNG đường production (không chép lại thân hàm — chép thì test xanh dù hàm hỏng).
            OfficeComPrintEngine.ApplyWordPaper(d, "A3", PrintOrientation.AsDocument);

            // Số đo này lấy từ BẢNG KHỔ CỦA WORD (không phải công thức của app) nên là phép đối
            // chiếu thật: 842.0 × 1190.7 pt = 297 × 420 mm.
            AssertSize((double)d.PageSetup.PageWidth, 297, "A3 width");
            AssertSize((double)d.PageSetup.PageHeight, 420, "A3 height");

            // Chốt chặn hồi quy: mã 8 là DMPAPER_A3; nếu Word hiểu 8 thành A3 thì bảng WordPaperCode
            // (và ghi chú "Word dùng hệ số riêng") đã sai và phải xem lại. Đo trên máy này: 8 → A4.
            d.PageSetup.PaperSize = 8;
            AssertSize((double)d.PageSetup.PageWidth, 210, "mã 8 phải là A4 (không phải A3)");
        });

    [OfficeFact(Timeout = ComTimeoutMs)]
    public void Word_kho_khong_co_ma_dat_bang_kich_thuoc_duoc()
        => RunOnWordDoc("t2.docx", d =>
        {
            // A6 không có thành viên WdPaperSize → ApplyWordPaper phải đi đường PageWidth/PageHeight.
            var plan = OfficeComPrintEngine.PlanWordPaper("A6");
            Assert.Null(plan.Code);
            Assert.True(plan.IsCustom);

            OfficeComPrintEngine.ApplyWordPaper(d, "A6", PrintOrientation.AsDocument);

            // Số chuẩn ĐO ĐƯỢC trên Word 16.0 — không suy từ 72/25.4 của production.
            MatchesMeasured((double)d.PageSetup.PageWidth, 297.6, "A6 width");
            MatchesMeasured((double)d.PageSetup.PageHeight, 419.5, "A6 height");
            // Word tự chuyển sang wdPaperCustom (41) khi đặt PageWidth/PageHeight.
            Assert.Equal(41, (int)d.PageSetup.PaperSize);
        });

    /// <summary>Tạo Word ẩn + một tài liệu thật, chạy body, rồi dọn theo thứ tự NGƯỢC và mỗi bước
    /// một try riêng — Close ném (RPC_E_CALL_REJECTED là chuyện thường) không được phép bỏ qua Quit,
    /// nếu không mỗi lần chạy test lại để lại một WINWORD mồ côi (đã quan sát được thật).</summary>
    private static void RunOnWordDoc(string fileName, Action<dynamic> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "printonator-word-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        object? app = null;
        object? opened = null;
        try
        {
            var file = Path.Combine(dir, fileName);
            app = Activator.CreateInstance(Type.GetTypeFromProgID("Word.Application")!);
            dynamic w = app!;   // non-null: vừa tạo từ ProgID ở trên
            w.Visible = false;
            w.DisplayAlerts = 0;

            // Word kiểm khổ theo máy in ĐANG CHỌN và ném "Requested PaperSize is not available on
            // the currently selected printer". Máy in mặc định của máy test có thể thiếu A3.
            if (!TryUsePdfPrinter(w)) return;

            // Word cần file thật để Open. Tạo bằng chính Word rồi đóng NGAY, nhả RCW của nó.
            var created = w.Documents.Add();
            try
            {
                ((dynamic)created).SaveAs2(file);
                ((dynamic)created).Close(0);
            }
            finally
            {
                if (created is not null && Marshal.IsComObject(created))
                    Marshal.ReleaseComObject(created);
            }

            opened = w.Documents.Open(file, ReadOnly: false, AddToRecentFiles: false);
            body((dynamic)opened);
        }
        finally
        {
            // Mỗi bước một try: Close ném không được nuốt mất Quit.
            try { if (opened is not null) ((dynamic)opened).Close(0); } catch { }
            try { if (app is not null) ((dynamic)app).Quit(0); } catch { }
            ReleaseCom(opened);
            ReleaseCom(app);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>Trỏ Word sang máy in nhiều khổ. False nếu máy không có "Microsoft Print to PDF"
    /// (bản Windows đổi tên/bị xoá) — khi đó test bỏ qua thay vì fail oan.</summary>
    private static bool TryUsePdfPrinter(dynamic word)
    {
        try
        {
            word.ActivePrinter = "Microsoft Print to PDF";
            return true;
        }
        catch { return false; }
    }

    /// <summary>Nhả RCW rồi ép finalizer — Quit(0) một mình không đủ, Word ẩn vẫn sống nếu còn ref.</summary>
    private static void ReleaseCom(object? com)
    {
        try { if (com is not null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com); }
        catch { }
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}

/// <summary>
/// Chỉ chạy khi máy có Office VÀ có máy in ảo "Microsoft Print to PDF" (Word cần máy in nhiều khổ
/// để nhận A3). Không có → SKIP thật (hiện là Skipped, không phải Passed) — nếu để `return` trần
/// thì CI không có Office sẽ báo xanh trong khi test chưa hề chạy.
/// xunit 2.5.3 không có Assert.Skip nên phải qua attribute.
/// </summary>
public sealed class OfficeFactAttribute : FactAttribute
{
    public OfficeFactAttribute()
    {
        if (!Printonator.Spool.Printing.InstalledApps.HasWord)
            Skip = "Máy không có Microsoft Word — bỏ qua test Office thật.";
        else if (!HasPdfPrinter())
            Skip = "Không có máy in \"Microsoft Print to PDF\" — không kiểm được khổ A3.";
    }

    private static bool HasPdfPrinter()
    {
        try
        {
            return System.Drawing.Printing.PrinterSettings.InstalledPrinters
                .Cast<string>()
                .Any(n => n.Equals("Microsoft Print to PDF", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }
}
