using Printonator.Core.Models;
using Printonator.Core.Presets;
using Xunit;

namespace Printonator.Core.Tests;

/// <summary>Unit test PresetExporter (xuất/nhập profile qua file JSON .printonator).</summary>
public class PresetExporterTests
{
    private static string TempJson()
        => Path.Combine(Path.GetTempPath(), $"presets_export_{Guid.NewGuid():N}.json");

    private static string TempFile()
        => Path.Combine(Path.GetTempPath(), $"presets_export_{Guid.NewGuid():N}.printonator");

    [Fact]
    public void Export_Import_Roundtrip()
    {
        var storePath = TempJson();
        var filePath = TempFile();
        var store = new PresetStore(storePath);
        store.Save(new Preset
        {
            Name = "Hợp đồng 2 mặt",
            Copies = 2,
            Duplex = true,
            PaperSize = "A4",
            PrinterName = "HP404",
        });
        store.Save(new Preset
        {
            Name = "Nháp",
            Copies = 1,
            Collation = PrintCollation.ByDocuments,
            PaperSize = "A5",
            Parity = PageParityFilter.Odd,
        });

        PresetExporter.Export(store, filePath);
        Assert.True(File.Exists(filePath));

        var imported = PresetExporter.Import(filePath);
        Assert.Equal(2, imported.Count);

        var a = imported.Single(p => p.Name == "Hợp đồng 2 mặt");
        Assert.Equal(2, a.Copies);
        Assert.True(a.Duplex);
        Assert.Equal("A4", a.PaperSize);
        Assert.Equal("HP404", a.PrinterName);

        var b = imported.Single(p => p.Name == "Nháp");
        Assert.Equal(PrintCollation.ByDocuments, b.Collation);
        Assert.Equal("A5", b.PaperSize);
        Assert.Equal(PageParityFilter.Odd, b.Parity);

        if (File.Exists(storePath)) File.Delete(storePath);
        if (File.Exists(filePath)) File.Delete(filePath);
    }

    [Fact]
    public void Import_CorruptFile_ReturnsEmpty()
    {
        var filePath = TempFile();
        File.WriteAllText(filePath, "{ this is not valid json ]");

        var imported = PresetExporter.Import(filePath);
        Assert.Empty(imported);
        // File hỏng không bị ghi đè — được đổi tên dự phòng .corrupt-*
        var baseName = Path.GetFileNameWithoutExtension(filePath);
        var renamed = Directory.GetFiles(Path.GetDirectoryName(filePath)!)
            .Select(Path.GetFileName)
            .FirstOrDefault(f => f?.StartsWith(baseName, StringComparison.OrdinalIgnoreCase) == true);
        Assert.False(File.Exists(filePath));
        Assert.NotNull(renamed);
        Assert.Contains(".corrupt-", renamed);

        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(filePath)!)
                     .Where(f => Path.GetFileName(f).StartsWith(baseName, StringComparison.OrdinalIgnoreCase)))
            File.Delete(f);
    }

    [Fact]
    public void Import_NotJsonArray_ThrowsNothing_ReturnsEmpty()
    {
        var filePath = TempFile();
        File.WriteAllText(filePath, "{ \"Name\": \"not an array\" }");

        var imported = PresetExporter.Import(filePath);
        Assert.Empty(imported);

        if (File.Exists(filePath)) File.Delete(filePath);
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(filePath)!).Where(f => f.StartsWith(Path.GetFileNameWithoutExtension(filePath))))
            File.Delete(f);
    }

    [Fact]
    public void Import_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(PresetExporter.Import(TempFile()));
    }
}