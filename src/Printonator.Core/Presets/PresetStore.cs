using System.Text.Json;
using Printonator.Core.Models;
using Printonator.Core.Persistence;

namespace Printonator.Core.Presets;

/// <summary>
/// Lưu/đọc danh sách preset dạng JSON cục bộ.
/// Mặc định đặt tại %APPDATA%\Printonator\presets.json — truyền path khác trong test.
/// Singleton Default instance — 6 places creating new PresetStore() now use one shared instance.
/// </summary>
public sealed class PresetStore
{
    /// <summary>Shared singleton instance — use this instead of creating new PresetStore().</summary>
    public static PresetStore Default { get; } = new();

    private readonly string _path;
    private readonly object _sync = new();

    public PresetStore(string? path = null)
    {
        _path = path ?? Path.Combine(JsonFileStore.AppDataDir, "presets.json");
    }

    public List<Preset> Load()
    {
        lock (_sync)
        {
            return JsonFileStore.Load<Preset>(_path);
        }
    }

    public bool Save(Preset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (string.IsNullOrWhiteSpace(preset.Name)) return false;

        lock (_sync)
        {
            var all = Load();
            all.RemoveAll(p => p.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase));
            all.Add(preset);
            all.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            JsonFileStore.Save(_path, all);
            return true;
        }
    }

    public bool Delete(string name)
    {
        lock (_sync)
        {
            var all = Load();
            var removed = all.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            JsonFileStore.Save(_path, all);
            return true;
        }
    }
}