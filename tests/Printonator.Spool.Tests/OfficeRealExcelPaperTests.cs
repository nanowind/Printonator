using System.Runtime.InteropServices;
using Printonator.Core.Models;
using Printonator.Spool.Printing;

namespace Printonator.Spool.Tests;

/// <summary>
/// Đầu-cuối đường đặt khổ giấy cho EXCEL: gọi COM thật, đi qua chính
/// <see cref="OfficeComPrintEngine.ApplyExcelSetup"/> của production, rồi đọc lại
/// <see cref="PageSetup.PaperSize"/>.
///
/// GIỚI HẠN THẬT CỦA EXCEL: object model KHÔNG có PaperWidth/PaperHeight/PageWidth/PageHeight
/// (đã probe: IDispatch trả DISP_E_UNKNOWNNAME cho cả bốn tên; `Get-Member` trên PageSetup sống
/// chỉ có PaperSize trong nhóm Paper*). Word thì có PageWidth/PageHeight — Excel thì không.
/// Vì vậy ở đây chỉ kiểm được "Excel CHẤP NHẬN đúng mã khổ"; còn khổ vật lý suy ra từ mã thì
/// paper-size→mm đã được khoá riêng ở PaperDevModeTests (DMPAPER 13 = B5 JIS 182×257).
///
/// Vì sao vẫn cần: bảng số đã có test, nhưng chỉ COM thật mới chứng minh Excel không âm thầm
/// remap mã (Word thì có remap — xem OfficeRealPaperTests).
/// </summary>
[Collection(RealOfficeCom.CollectionName)]
public class OfficeRealExcelPaperTests
{
    private const int ComTimeoutMs = 120_000;

    [OfficeExcelFact(Timeout = ComTimeoutMs)]
    public void Excel_dat_kho_A3_qua_ma_ra_dung_A3()
        => RunOnWorkbook(sheet =>
        {
            OfficeComPrintEngine.ApplyExcelSetup(sheet, "A3",
                PrintOrientation.AsDocument, logMissingCode: false);

            // Excel trả PaperSize theo hệ DMPAPER: A3 = 8. Đây là bằng chứng Excel nhận mã,
            // KHÔNG phải bằng chứng tờ giấy đo được 297×420 (object model không cho đọc).
            Assert.Equal(8, (int)sheet.PageSetup.PaperSize);
        });

    [OfficeExcelFact(Timeout = ComTimeoutMs)]
    public void Excel_dat_kho_B5_qua_ma_ra_dung_B5()
        => RunOnWorkbook(sheet =>
        {
            OfficeComPrintEngine.ApplyExcelSetup(sheet, "B5",
                PrintOrientation.AsDocument, logMissingCode: false);

            Assert.Equal(13, (int)sheet.PageSetup.PaperSize);   // xlPaperB5 = DMPAPER_B5
        });

    /// <summary>AsDocument = "theo tài liệu" → KHÔNG được đụng vào khổ của sheet.
    /// Sheet mới mặc định A4 (9), trùng với kết quả nếu code ghi bừa A4 — nên đặt trước một khổ
    /// KHÁC (A3=8) để phép so sánh có nghĩa: nếu code ghi đè, giá trị sẽ đổi khỏi 8.</summary>
    [OfficeExcelFact(Timeout = ComTimeoutMs)]
    public void Excel_AsDocument_khong_doi_kho()
        => RunOnWorkbook(sheet =>
        {
            sheet.PageSetup.PaperSize = 8;   // A3 — cố ý khác mặc định A4
            Assert.Equal(8, (int)sheet.PageSetup.PaperSize);

            OfficeComPrintEngine.ApplyExcelSetup(sheet, PaperCatalog.AsDocument,
                PrintOrientation.AsDocument, logMissingCode: false);

            Assert.Equal(8, (int)sheet.PageSetup.PaperSize);
        });

    /// <summary>Đặt khổ và chiều giấy cùng lúc — cả hai phải vào. Lưu ý: đây KHÔNG chứng minh
    /// nhánh "driver từ chối khổ" (máy in PDF nhận A3 nên không kích hoạt được nhánh đó);
    /// nhánh đó được phủ bằng stub trong ExcelSetupTests.Kho_bi_tu_choi_van_dat_duoc_chieu_giay.</summary>
    [OfficeExcelFact(Timeout = ComTimeoutMs)]
    public void Excel_dat_kho_va_chieu_giay_cung_luc()
        => RunOnWorkbook(sheet =>
        {
            OfficeComPrintEngine.ApplyExcelSetup(sheet, "A3",
                PrintOrientation.Landscape, logMissingCode: false);

            Assert.Equal(8, (int)sheet.PageSetup.PaperSize);   // xlPaperA3
            Assert.Equal(2, (int)sheet.PageSetup.Orientation); // xlLandscape
        });

    /// <summary>
    /// Tạo Excel ẩn + một workbook thật trên đĩa, chạy body trên sheet đầu, rồi dọn NGƯỢC thứ tự,
    /// mỗi bước một try riêng (Close ném RPC_E_CALL_REJECTED không được nuốt mất Quit).
    /// KHÔNG có early-return: mọi điều kiện thiếu đã do <see cref="OfficeExcelFactAttribute"/> lo
    /// — early-return trong body từng làm 4 test xanh rỗng trước đây.
    /// </summary>
    private static void RunOnWorkbook(Action<dynamic> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "printonator-excel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        object? app = null;
        object? wb = null;
        object? sheet = null;
        try
        {
            var file = Path.Combine(dir, "t.xlsx");
            app = Activator.CreateInstance(Type.GetTypeFromProgID("Excel.Application")!);
            dynamic x = app!;
            x.Visible = false;
            x.DisplayAlerts = false;

            // Mỗi RCW trung gian phải giữ trong BIẾN và nhả tường minh: `x.Workbooks.Add()` tạo RCW
            // cho collection, `Workbooks.Open` tạo RCW cho workbook, `Worksheets[1]` tạo RCW cho sheet.
            // Bỏ qua bước nào thì Excel ở lại sau khi Quit (đã đo: 12 EXCEL.EXE mồ côi sau 4 test).
            object? workbooks = null;
            object? created = null;
            object? sheets = null;
            try
            {
                workbooks = x.Workbooks;
                created = ((dynamic)workbooks).Add();
                ((dynamic)created).SaveAs(file);
                ((dynamic)created).Close(false);
            }
            finally
            {
                RealOfficeCom.Release(created);
                RealOfficeCom.Release(workbooks);
            }

            object? openedBooks = null;
            try
            {
                openedBooks = x.Workbooks;
                wb = ((dynamic)openedBooks).Open(file);
            }
            finally
            {
                RealOfficeCom.Release(openedBooks);
            }

            try
            {
                sheets = ((dynamic)wb).Worksheets;
                sheet = ((dynamic)sheets)[1];
            }
            finally
            {
                RealOfficeCom.Release(sheets);
            }

            body((dynamic)sheet);
        }
        finally
        {
            try { if (wb is not null) ((dynamic)wb).Close(false); } catch { }
            try { if (app is not null) ((dynamic)app).Quit(); } catch { }
            RealOfficeCom.Release(sheet);
            RealOfficeCom.Release(wb);
            RealOfficeCom.Release(app);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
