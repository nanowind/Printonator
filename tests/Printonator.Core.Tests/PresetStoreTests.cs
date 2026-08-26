using Printonator.Core.Models;
using Printonator.Core.Presets;
using Xunit;

namespace Printonator.Core.Tests;

/// <summary>Unit test PresetStore (JSON local): save/load/delete + file hỏng không làm mất dữ liệu.</summary>
public class PresetStoreTests
{
    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), $"presets_{Guid.NewGuid():N}.json");

    [Fact]
    public void Save_Load_Roundtrip()
    {
        var path = TempPath();
        var store = new PresetStore(path);

        var ok = store.Save(new Preset
        {
            Name = "Hợp đồng 2 mặt",
            Copies = 2,
            Duplex = true,
            PaperSize = "A4",
            PrinterName = "HP404",
        });

        Assert.True(ok);
        var all = store.Load();
        Assert.Single(all);
        Assert.Equal("Hợp đồng 2 mặt", all[0].Name);
        Assert.True(all[0].Duplex);
        Assert.Equal(2, all[0].Copies);
        Assert.Equal("HP404", all[0].PrinterName);

        if (File.Exists(path)) File.Delete(path);
    }

    [Fact]
    public void Save_Overwrites_SameName_CaseInsensitive()
    {
        var path = TempPath();
        var store = new PresetStore(path);
        store.Save(new Preset { Name = "PresetA", Copies = 1 });
        store.Save(new Preset { Name = "preseta", Copies = 3, PaperSize = "A3" });

        Assert.Single(store.Load());
        Assert.Equal(3, store.Load()[0].Copies);
        Assert.Equal("A3", store.Load()[0].PaperSize);

        if (File.Exists(path)) File.Delete(path);
    }

    [Fact]
    public void Delete_Removes_ByName()
    {
        var path = TempPath();
        var store = new PresetStore(path);
        store.Save(new Preset { Name = "A" });
        store.Save(new Preset { Name = "B" });

        Assert.True(store.Delete("a")); // case-insensitive
        Assert.Single(store.Load());
        Assert.False(store.Delete("không-có"));
        Assert.Single(store.Load());

        if (File.Exists(path)) File.Delete(path);
    }

    [Fact]
    public void MissingFile_ReturnsEmpty()
    {
        var store = new PresetStore(TempPath());
        Assert.Empty(store.Load());
    }

    [Fact]
    public void CorruptFile_IsBackedUp_NotLost()
    {
        var path = TempPath();
        File.WriteAllText(path, "{ this is not valid json ]]");

        var store = new PresetStore(path);
        var all = store.Load();
        Assert.Empty(all);

        // Sau khi đổi tên dự phòng, file chính có thể ghi lại preset mới
        Assert.True(store.Save(new Preset { Name = "Mới" }));
        Assert.Single(store.Load());

        // Bản hỏng được giữ dự phòng chứ không bị ghi đè mất
        Assert.Contains(Directory.GetFiles(Path.GetDirectoryName(path)!, "presets_*.json.corrupt-*"), f => true);

        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!).Where(f => f.StartsWith(Path.GetFileNameWithoutExtension(path))))
            File.Delete(f);
    }

    [Fact]
    public void ToPrintConfig_Applies_Embedded()
    {
        var preset = new Preset
        {
            Name = "P",
            Copies = 4,
            Duplex = true,
            PaperSize = "A5",
            PageRange = "2-3",
            PrinterName = "Canon",
        };
        var cfg = preset.ToPrintConfig();
        Assert.Equal(4, cfg.Copies);
        Assert.True(cfg.Duplex);
        Assert.Equal("A5", cfg.PaperSize);
        Assert.Equal("2-3", cfg.PageRange);
        Assert.Equal("Canon", cfg.PrinterName);
    }
}