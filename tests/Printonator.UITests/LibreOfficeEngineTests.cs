using System.IO;
using Printonator.Spool.Printing;
using Xunit;

namespace Printonator.UITests;

/// <summary>
/// Unit test engine động LibreOffice (KHÔNG bundle — dò trên máy user):
/// locator trả đúng soffice.exe khi tồn tại; CanHandle chỉ nhận format office khi có soffice.
/// Deterministic: luôn inject override path (không phụ thuộc máy có/không có LibreOffice).
/// </summary>
public class LibreOfficeEngineTests
{
    private static string TempDir()
        => Path.Combine(Path.GetTempPath(), $"printonator-lo-{Guid.NewGuid():N}");

    private static string CreateSofficeFile(string dir)
    {
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "soffice.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]); // stub header MZ — chỉ cần tồn tại
        return exe;
    }

    [Fact]
    public void Resolve_WithOverrideFile_ReturnsThatFile()
    {
        var dir = TempDir();
        try
        {
            var exe = CreateSofficeFile(dir);
            var locator = new LibreOfficeLocator(() => exe);
            Assert.Equal(exe, locator.ResolveSofficePath());
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void Resolve_WithOverrideDirectory_AppendsProgramSoffice()
    {
        var baseDir = TempDir();
        try
        {
            var exe = CreateSofficeFile(Path.Combine(baseDir, "program"));
            // override trỏ tới thư mục chương trình cài đặt (chứa \program\soffice.exe)
            var locator = new LibreOfficeLocator(() => baseDir);
            Assert.Equal(exe, locator.ResolveSofficePath());
        }
        finally { try { Directory.Delete(baseDir, recursive: true); } catch { } }
    }

    [Fact]
    public void Resolve_WithNonExistingOverride_DoesNotThrow()
    {
        var locator = new LibreOfficeLocator(() => Path.Combine(TempDir(), "thiếu", "soffice.exe"));
        // Phải chạy được không crash; kết quả có thể null HOẶC là bản thật trên máy — chỉ cần không throw.
        _ = locator.ResolveSofficePath();
    }

    [Fact]
    public void Engine_CanHandle_OnlyOfficeFormats_WhenSofficeFound()
    {
        var dir = TempDir();
        try
        {
            var exe = CreateSofficeFile(dir);
            var engine = new LibreOfficePrintEngine(() => exe);

            Assert.True(engine.CanHandle("DOCX"));
            Assert.True(engine.CanHandle("xlsx"));   // case-insensitive
            Assert.True(engine.CanHandle("PPTX"));
            Assert.False(engine.CanHandle("PDF"));   // PDF không thuộc engine này (chờ engine riêng/shell)
            Assert.False(engine.CanHandle("PNG"));
            Assert.False(engine.CanHandle("TXT"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void Engine_CanHandle_False_WhenNoSoffice()
    {
        var engine = new LibreOfficePrintEngine(() => null);
        Assert.False(engine.CanHandle("DOCX"));
        Assert.False(engine.CanHandle("XLSX"));
    }
}