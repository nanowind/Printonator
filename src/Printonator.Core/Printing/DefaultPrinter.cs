using System;

namespace Printonator.Core.Printing;

/// <summary>
/// Centralized default printer resolution — consolidates 3 different implementations:
/// - SpoolPrintEngine: Registry (HKCU\...\Windows\Device)
/// - OfficeComPrintEngine: LocalPrintServer
/// - GdiPrintEngine: delegates to SpoolPrintEngine
///
/// Single source of truth for "mặc định"/"default" printer resolution.
/// </summary>
public static class DefaultPrinter
{
    /// <summary>
    /// Check if the printer name is the "default" sentinel.
    /// Returns true for: null, empty, "mặc định", "default".
    /// </summary>
    public static bool IsDefault(string? printerName)
    {
        return string.IsNullOrWhiteSpace(printerName)
            || printerName.Equals("mặc định", StringComparison.OrdinalIgnoreCase)
            || printerName.Equals("default", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolve the effective printer name — returns null for "default" sentinel (meaning OS default).
    /// This is the standard pattern for LibreOffice/Office COM engines.
    /// </summary>
    public static string? Resolve(string? printerName)
    {
        return IsDefault(printerName) ? null : printerName;
    }

    /// <summary>
    /// Resolve the Windows default printer name via Registry (HKCU\...\Windows\Device).
    /// Returns null if not found or on error.
    /// </summary>
    public static string? GetWindowsDefaultPrinterName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\windows NT\CurrentVersion\Windows");
            return key?.GetValue("Device")?.ToString()?.Split(',')[0];
        }
        catch
        {
            return null;
        }
    }
}