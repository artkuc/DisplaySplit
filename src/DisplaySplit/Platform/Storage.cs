using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace DisplaySplit.Platform;

/// <summary>
/// Crash-safe JSON persistence: write tmp → fsync → keep .bak → atomic move.
/// Load falls back to .bak; a corrupt file is renamed and never silently overwritten with defaults.
/// </summary>
public static class Storage
{
    static string? _home;

    /// <summary>Data directory. The user's own folder, or the protected admin-mode folder (see <see cref="UseDirectory"/>).</summary>
    public static string Home => _home ??= ResolveHome();

    /// <summary>
    /// Redirect all storage (and the log) to <paramref name="dir"/>. Admin mode calls this first thing in Main, before
    /// anything is logged, so an elevated process never writes into a folder the non-elevated user can modify.
    /// </summary>
    public static void UseDirectory(string dir)
    {
        _home = dir;
        Log.ResetPath();
    }

    /// <summary>The non-elevated user's data folder (DISPLAYSPLIT_HOME or %APPDATA%\DisplaySplit). Not created here.</summary>
    public static string UserHome()
    {
        string? env = Environment.GetEnvironmentVariable("DISPLAYSPLIT_HOME");
        return !string.IsNullOrWhiteSpace(env)
            ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DisplaySplit");
    }

    static string ResolveHome()
    {
        string dir = UserHome();
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string PathOf(string name) => Path.Combine(Home, name);

    /// <summary>
    /// Read a document from another directory (admin-mode migration). Refuses reparse points (symlinks/junctions)
    /// and oversized files; only typed data comes out, never raw bytes.
    /// </summary>
    public static bool TryLoadFrom<T>(string dir, string name, JsonTypeInfo<T> info, out T? value) where T : class
    {
        value = null;
        string path = Path.Combine(dir, name);
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return false;
            if ((fi.Attributes & FileAttributes.ReparsePoint) != 0 || fi.Length > 64L * 1024 * 1024)
            {
                Log.Warn($"migrate: skipped {path} (reparse point or too large)");
                return false;
            }
        }
        catch (Exception ex) { Log.Warn($"migrate {path}: {ex.Message}"); return false; }
        return TryRead(path, info, out value);
    }

    public static T Load<T>(string name, JsonTypeInfo<T> info, Func<T> defaults, out bool loadedFromDisk) where T : class
    {
        string path = PathOf(name);
        loadedFromDisk = false;
        if (TryRead(path, info, out T? value)) { loadedFromDisk = true; return value!; }
        bool mainExists = File.Exists(path);
        if (TryRead(path + ".bak", info, out value)) { loadedFromDisk = true; Quarantine(path, mainExists); return value!; }
        Quarantine(path, mainExists);
        return defaults();
    }

    static void Quarantine(string path, bool exists)
    {
        if (!exists) return;
        try { File.Move(path, $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true); }
        catch (Exception ex) { Log.Warn($"quarantine {path}: {ex.Message}"); }
    }

    static bool TryRead<T>(string path, JsonTypeInfo<T> info, out T? value) where T : class
    {
        value = null;
        try
        {
            if (!File.Exists(path)) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            value = JsonSerializer.Deserialize(fs, info);
            return value != null;
        }
        catch (Exception ex)
        {
            Log.Warn($"read {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Returns false if the write failed (caller should retry later).</summary>
    public static bool Save<T>(string name, T value, JsonTypeInfo<T> info) => SaveTo(Home, name, value, info);

    /// <summary>Like <see cref="Save{T}"/>, into an explicit directory.</summary>
    public static bool SaveTo<T>(string dir, string name, T value, JsonTypeInfo<T> info)
    {
        string path = Path.Combine(dir, name);
        string tmp = path + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(fs, value, info);
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"save {path}: {ex.Message}");
            try { File.Delete(tmp); } catch { /* ignore */ }
            return false;
        }
    }
}

/// <summary>Debounces saves of one document onto a thread-pool timer; Flush() writes synchronously.</summary>
public sealed class DebouncedSaver<T>(string name, JsonTypeInfo<T> info, Func<T> snapshot, int delayMs = 1000) : IDisposable
{
    readonly object _lock = new();
    Timer? _timer;
    bool _dirty;

    /// <summary>Mark dirty; the actual write happens on the UI thread (which owns the data) after the debounce delay.</summary>
    public void MarkDirty()
    {
        lock (_lock)
        {
            _dirty = true;
            _timer ??= new Timer(_ => App.Post(Flush), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(delayMs, Timeout.Infinite);
        }
    }

    /// <summary>Write now if dirty. Must be called on the UI thread (the owner of the data).</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (!_dirty) return;
            _dirty = false;
        }
        if (!Storage.Save(name, snapshot(), info))
        {
            // e.g. file briefly locked by AV/OneDrive: stay dirty and retry (bounded by the debounce delay).
            if (++_failures <= 20) MarkDirty();
        }
        else _failures = 0;
    }

    int _failures;

    public void Dispose()
    {
        _timer?.Dispose();
        Flush();
    }
}
