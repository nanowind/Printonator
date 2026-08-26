using System.Text.Json;
using Printonator.Core.Models;

namespace Printonator.Core.Presets;

/// <summary>
/// Lưu/đọc danh sách preset dạng JSON cục bộ.
/// Mặc định đặt tại %APPDATA%\Printonator\presets.json — truyền path khác trong test.
/// </summary>
public sealed class PresetStore
{
    private readonly string _path;
    private readonly object _sync = new();

    public PresetStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Printonator", "presets.json");
    }

    public List<Preset> Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_path)) return new List<Preset>();
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<List<Preset>>(json) ?? new List<Preset>();
            }
            catch
            {
                // File hỏng → đổi tên dự phòng (không ghi đè mất dữ liệu), trả danh sách rỗng
                try
                {
                    if (File.Exists(_path))
                        File.Move(_path, $"{_path}.corrupt-{DateTimeOffset.Now:yyyyMMddHHmmss}");
                }
                catch { }
                return new List<Preset>();
            }
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
            WriteAll(all);
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
            WriteAll(all);
            return true;
        }
    }

    private void WriteAll(List<Preset> presets)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(presets, new JsonSerializerOptions { WriteIndented = true }));
    }
}