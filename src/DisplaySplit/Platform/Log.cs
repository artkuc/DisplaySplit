using System.Diagnostics;

namespace DisplaySplit.Platform;

/// <summary>Tiny append-only log (Home\log.txt, rotated at 512 KB). Verbose logging only with DISPLAYSPLIT_DEBUG=1.</summary>
public static class Log
{
    static readonly object Lock = new();
    public static readonly bool Verbose = Environment.GetEnvironmentVariable("DISPLAYSPLIT_DEBUG") == "1";
    static string? _path;

    static string LogPath => _path ??= Storage.PathOf("log.txt");

    public static void Debug(string msg) { if (Verbose) Write("DBG", msg); }
    public static void Info(string msg) => Write("INF", msg);
    public static void Warn(string msg) => Write("WRN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERR", ex is null ? msg : $"{msg}: {ex}");

    static void Write(string level, string msg)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {msg}{Environment.NewLine}";
        System.Diagnostics.Debug.Write(line);
        lock (Lock)
        {
            try
            {
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 512 * 1024) File.Move(LogPath, LogPath + ".old", overwrite: true);
                File.AppendAllText(LogPath, line);
            }
            catch { /* logging must never throw */ }
        }
    }
}
