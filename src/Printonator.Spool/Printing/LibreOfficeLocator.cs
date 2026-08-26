using System.IO;
using Microsoft.Win32;

namespace Printonator.Spool.Printing;

/// <summary>
/// Dò TÌM LibreOffice trên máy user (không bundle, không cài kèm — app giữ nhẹ).
/// Thứ tự: env PRINTONATOR_LIBREOFFICE (portable/custom) → registry HKLM/HKCU
/// (InstallPath của LibreOffice) → đường dẫn cài mặc định. Chỉ trả path khi file
/// soffice (soffice.com ưu tiên — headless console chờ được exit code, ngược lại soffice.exe) CÒN tồn tại.
/// </summary>
public sealed class LibreOfficeLocator
{
    private static readonly string[] SofficeNames = ["soffice.com", "soffice.exe"];

    private readonly Func<string?>? _overridePath;

    public LibreOfficeLocator(Func<string?>? overridePath = null) => _overridePath = overridePath;

    /// <summary>Đường dẫn soffice tìm được (soffice.com nếu có), hoặc null nếu máy user không có LibreOffice.</summary>
    public string? ResolveSofficePath()
    {
        // Override (test / user tự trỏ bản portable)
        if (TryAccept(_overridePath?.Invoke(), out var exe)) return exe;

        // Env: PRINTONATOR_LIBREOFFICE = path tới soffice HOẶC thư mục program
        try
        {
            if (TryAccept(Environment.GetEnvironmentVariable("PRINTONATOR_LIBREOFFICE"), out exe)) return exe;
        }
        catch { }

        // Registry: HKLM\SOFTWARE\LibreOffice\LibreOffice → InstallPath; fallback HKCU (per-user install)
        if (TryRegistry(RegistryView.Registry64, out exe)) return exe;
        if (TryRegistry(RegistryView.Registry32, out exe)) return exe;

        // Đường dẫn cài mặc định (x64, x86)
        foreach (var basePath in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (TryAccept(Path.Combine(basePath, "LibreOffice", "program"), out exe)) return exe;
        }

        return null;
    }

    private static bool TryRegistry(RegistryView view, out string? exe)
    {
        exe = null;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = baseKey?.OpenSubKey(@"Software\LibreOffice\LibreOffice");
            var installPath = key?.GetValue("InstallPath")?.ToString();
            if (string.IsNullOrWhiteSpace(installPath)) return false;
            return TryAccept(Path.Combine(installPath.TrimEnd('\\'), "program"), out exe);
        }
        catch { return false; }
    }

    /// <summary>
    /// Chấp nhận candidate nếu tìm thấy soffice:
    /// - chính là file soffice.com/soffice.exe tồn tại;
    /// - là thư mục (cài đặt hoặc program) chứa soffice.com/soffice.exe.
    /// </summary>
    internal static bool TryAccept(string? candidate, out string? resolved)
    {
        resolved = null;
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        var p = candidate.Trim().Trim('"');
        try
        {
            if (File.Exists(p) && IsSofficeName(Path.GetFileName(p)))
            {
                resolved = p;
                return true;
            }
            if (Directory.Exists(p))
            {
                foreach (var name in SofficeNames)
                {
                    var joined = Path.Combine(p, name);
                    if (File.Exists(joined)) { resolved = joined; return true; }
                    // thư mục cài đặt: <base>\program\soffice.*
                    var program = Path.Combine(p, "program", name);
                    if (File.Exists(program)) { resolved = program; return true; }
                }
            }
        }
        catch { }
        return false;
    }

    internal static bool IsSofficeName(string fileName)
        => SofficeNames.Contains(fileName, StringComparer.OrdinalIgnoreCase);
}