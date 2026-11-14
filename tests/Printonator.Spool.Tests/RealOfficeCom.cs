using System.Runtime.InteropServices;

namespace Printonator.Spool.Tests;

/// <summary>
/// Hạ tầng dùng chung cho test COM Office thật (Word + Excel). Tách ra vì hai harness từng lệch
/// nhau: bản Excel thiếu release RCW, và cả hai dùng early-`return` khi thiếu điều kiện — kiểu đó
/// báo "Passed" trong khi body chưa chạy câu nào (đã xảy ra thật: 4 test Excel xanh rỗng).
/// </summary>
internal static class RealOfficeCom
{
    /// <summary>Tên collection cho mọi test class dùng Office COM — xunit xếp chúng chạy TUẦN TỰ.
    /// Word/Excel đều là out-of-process và cùng đụng máy in ảo, chạy song song sẽ tranh nhau.</summary>
    public const string CollectionName = "RealOffice";

    /// <summary>Máy in ảo cần có: Word/Excel kiểm khổ theo máy in đang chọn, máy in mặc định của
    /// máy test có thể thiếu A3.</summary>
    public const string PdfPrinter = "Microsoft Print to PDF";

    /// <summary>Nhả RCW rồi ép finalizer — Quit() một mình không đủ, Office ẩn vẫn sống nếu còn ref
    /// (đã quan sát: mỗi lượt chạy để lại một WINWORD/EXCEL mồ côi).</summary>
    public static void Release(object? com)
    {
        try { if (com is not null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com); }
        catch { }
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    public static bool HasPdfPrinter()
    {
        try
        {
            return System.Drawing.Printing.PrinterSettings.InstalledPrinters
                .Cast<string>()
                .Any(n => n.Equals(PdfPrinter, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }
}

/// <summary>
/// Chỉ chạy khi máy có Microsoft Word VÀ có máy in ảo PDF. Không có → SKIP thật (hiện Skipped),
/// KHÔNG phải early-return trong body (kiểu đó báo Passed dù chưa chạy gì).
/// xunit 2.5.3 không có Assert.Skip nên phải qua attribute.
/// Tách riêng Word/Excel vì xunit gán property của attribute SAU khi constructor chạy — một
/// attribute có tham số `App` sẽ luôn đọc giá trị mặc định và gate sai app.
/// </summary>
public sealed class OfficeWordFactAttribute : FactAttribute
{
    public OfficeWordFactAttribute()
    {
        if (!Printonator.Spool.Printing.InstalledApps.HasWord)
            Skip = "Máy không có Microsoft Word — bỏ qua test Word thật.";
        else if (!RealOfficeCom.HasPdfPrinter())
            Skip = $"Không có máy in \"{RealOfficeCom.PdfPrinter}\" — không kiểm được khổ A3.";
    }
}

/// <summary>Như <see cref="OfficeWordFactAttribute"/> nhưng cho Excel.</summary>
public sealed class OfficeExcelFactAttribute : FactAttribute
{
    public OfficeExcelFactAttribute()
    {
        if (!Printonator.Spool.Printing.InstalledApps.HasExcel)
            Skip = "Máy không có Microsoft Excel — bỏ qua test Excel thật.";
    }
}
