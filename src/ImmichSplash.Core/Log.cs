using System.Diagnostics;

namespace ImmichSplash.Core;

/// <summary>Small, dependency-free daily rolling file logger. Keeps the last 7 days.</summary>
public static class Log
{
    static readonly Lock Gate = new();
    static StreamWriter? _writer;
    static DateOnly _day;

    public static void Info(string message) => Write("INF", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WRN", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERR", message, ex);

    static void Write(string level, string message, Exception? ex)
    {
        var now = DateTime.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        if (ex is not null)
        {
            line += $" | {ex.GetType().Name}: {ex.Message}";
            // Errors are rare: keep where they came from, so they can be fixed.
            if (level == "ERR" && (ex.InnerException ?? ex).StackTrace is { } trace)
                line += Environment.NewLine + trace;
        }

        Debug.WriteLine(line);

        lock (Gate)
        {
            try
            {
                var day = DateOnly.FromDateTime(now);
                if (_writer is null || day != _day)
                {
                    _writer?.Dispose();
                    _day = day;
                    var path = Path.Combine(AppPaths.LogsDir, $"immich-splash-{now:yyyyMMdd}.log");
                    _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                    DeleteOldLogs();
                }

                _writer.WriteLine(line);
                if (ex is not null && level == "ERR")
                    _writer.WriteLine(ex.ToString());
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
    }

    static void DeleteOldLogs()
    {
        foreach (var file in Directory.EnumerateFiles(AppPaths.LogsDir, "*.log").Where(f => Path.GetFileName(f).StartsWith("immich-", StringComparison.Ordinal)))
        {
            if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-7))
            {
                try { File.Delete(file); } catch { /* in use or gone */ }
            }
        }
    }
}
