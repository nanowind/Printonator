using System;
using System.IO;

namespace Printonator.Core.IO;

/// <summary>
/// IDisposable temporary directory that auto-deletes on dispose.
/// Replaces the 5 copy-pasted create-temp-dir + try/finally-delete patterns.
/// </summary>
public sealed class TempDir : IDisposable
{
    private readonly string _dirPath;
    private bool _disposed;

    private TempDir(string prefix)
    {
        _dirPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dirPath);
    }

    public string FullPath => _dirPath;

    public static TempDir Create(string prefix = "printonator-temp")
    {
        return new TempDir(prefix);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (Directory.Exists(_dirPath))
                Directory.Delete(_dirPath, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; ignore errors
        }
    }

    /// <summary>
    /// Creates a subdirectory inside the temp directory.
    /// </summary>
    public string CreateSubdir(string name)
    {
        var sub = System.IO.Path.Combine(_dirPath, name);
        Directory.CreateDirectory(sub);
        return sub;
    }
}