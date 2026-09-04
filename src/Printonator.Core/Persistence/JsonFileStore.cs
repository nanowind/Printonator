using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Printonator.Core.Persistence;

/// <summary>
/// Generic JSON file store — consolidates identical Load/Save/corrupt-rename patterns
/// from PresetStore, QueueStore, and HistoryStore.
/// </summary>
public static class JsonFileStore
{
    /// <summary>
    /// Default app data directory: %APPDATA%\Printonator
    /// </summary>
    public static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Printonator");

    /// <summary>
    /// Read JSON file into a list. Returns empty list on error.
    /// File corruption → rename to .corrupt-ts (preserves data for debugging).
    /// </summary>
    public static List<T> Load<T>(string path)
    {
        try
        {
            if (!File.Exists(path)) return new List<T>();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? new List<T>();
        }
        catch
        {
            // File corrupt → rename backup (don't overwrite/lose data), return empty
            try
            {
                if (File.Exists(path))
                    File.Move(path, $"{path}.corrupt-{DateTimeOffset.Now:yyyyMMddHHmmss}");
            }
            catch { }
            return new List<T>();
        }
    }

    /// <summary>
    /// Write list to JSON file. Creates directory if needed.
    /// Deletes file if list is empty.
    /// </summary>
    public static void Save<T>(string path, List<T> items)
    {
        if (items.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(items, JsonOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Delete file if it exists. Best-effort.
    /// </summary>
    public static void Delete(string path)
    {
        if (File.Exists(path))
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}