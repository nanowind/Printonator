using System.IO;

namespace Printonator.Spool.Printing;

/// <summary>
/// Dò trình duyệt Chromium trên máy user (không bundle gì) — dùng làm ENGINE RENDER cho PDF/ảnh/TXT:
/// headless printToPDF qua DevTools Protocol (CDP) áp được page range + scale + khổ giấy + chiều thật.
/// Thứ tự: env PRINTONATOR_BROWSER → CHROME (đã kiểm chứng headless ổn định) → EDGE (có sẵn Windows 10/11;
/// một số bản Edge 151+ không chạy headless/CDP — khi đó engine rớt mềm về shell print).
/// </summary>
public sealed class BrowserLocator
{
    private static readonly (string Name, string[] Paths)[] Candidates =
    {
        ("Chrome", new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
        }),
        ("Edge", new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
        }),
    };

    private readonly Func<string?>? _overridePath;

    public BrowserLocator(Func<string?>? overridePath = null) => _overridePath = overridePath;

    /// <summary>Tên + path browser tìm được (Edge ưu tiên — cài sẵn trên Windows 10/11), hoặc null.</summary>
    public (string Name, string Path)? ResolveBrowser()
    {
        try
        {
            var ov = _overridePath?.Invoke();
            if (!string.IsNullOrWhiteSpace(ov) && File.Exists(ov.Trim().Trim('"')))
                return ("Override", ov.Trim().Trim('"'));

            var env = Environment.GetEnvironmentVariable("PRINTONATOR_BROWSER");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env.Trim().Trim('"')))
                return ("Env", env.Trim().Trim('"'));

            foreach (var (name, paths) in Candidates)
                foreach (var p in paths)
                    if (File.Exists(p)) return (name, p);
        }
        catch { }
        return null;
    }
}