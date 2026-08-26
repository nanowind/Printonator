namespace Printonator.Spool.Printing;

/// <summary>App văn phòng cài trên máy user — dùng để in file qua chính app gốc (như Print Conductor).</summary>
public enum OfficeAppKind
{
    None,
    Word,
    Excel,
    PowerPoint,
}

/// <summary>
/// Phát hiện app gốc có sẵn trên máy user (MS Office) qua ProgID COM —
/// chính xác hơn đoán đường dẫn exe (Office có thể ở vị trí khác nhau / Click-to-Run).
/// </summary>
public static class InstalledApps
{
    private static readonly bool? WordCom;
    private static readonly bool? ExcelCom;
    private static readonly bool? PptCom;

    static InstalledApps()
    {
        WordCom = Type.GetTypeFromProgID("Word.Application") is not null;
        ExcelCom = Type.GetTypeFromProgID("Excel.Application") is not null;
        PptCom = Type.GetTypeFromProgID("PowerPoint.Application") is not null;
    }

    public static bool HasWord => WordCom == true;
    public static bool HasExcel => ExcelCom == true;
    public static bool HasPowerPoint => PptCom == true;

    /// <summary>Ứng với format file → app nào nên in (None = không có app gốc → shell fallback).</summary>
    public static OfficeAppKind AppForFormat(string format)
    {
        var f = format.ToUpperInvariant();
        return f switch
        {
            "DOCX" or "DOC" or "RTF" => HasWord ? OfficeAppKind.Word : OfficeAppKind.None,
            "XLSX" or "XLS" or "XLSM" or "CSV" => HasExcel ? OfficeAppKind.Excel : OfficeAppKind.None,
            "PPTX" or "PPT" or "PPSX" or "PPS" => HasPowerPoint ? OfficeAppKind.PowerPoint : OfficeAppKind.None,
            _ => OfficeAppKind.None,
        };
    }
}