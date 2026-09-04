namespace Printonator.Core.Printing;

/// <summary>
/// Shared file format constants — single source of truth for all format lists across engines.
/// Avoids copy-pasting OfficeFormats[] across OfficeComPrintEngine + LibreOfficePrintEngine.
/// </summary>
public static class FileFormatRegistry
{
    /// <summary>Office formats supported by COM/LibreOffice engines.</summary>
    public static readonly string[] OfficeFormats =
        ["DOCX", "DOC", "RTF", "XLSX", "XLS", "XLSM", "CSV", "PPTX", "PPT", "PPSX", "PPS"];

    /// <summary>Image formats supported by browser render engines.</summary>
    public static readonly string[] ImageFormats =
        ["PNG", "JPG", "JPEG", "BMP", "GIF", "TIF", "TIFF", "WEBP", "ICO", "JFIF"];

    /// <summary>Text formats supported by browser render engines.</summary>
    public static readonly string[] TextFormats = ["TXT", "CSV"];

    /// <summary>All formats supported by browser render (image + text + PDF).</summary>
    public static readonly string[] BrowserFormats =
        ["PDF", .. ImageFormats, .. TextFormats];

    /// <summary>All formats supported by the application.</summary>
    public static readonly string[] AllFormats =
        ["PDF", .. OfficeFormats, .. BrowserFormats];
}