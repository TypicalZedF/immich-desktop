using ImmichDesktop.Core.Settings;

namespace ImmichDesktop.Core.Sync;

public sealed partial class SyncEngine
{
    // ---- Watchers ---------------------------------------------------------------------------------

    void ConfigureWatchers(AppSettings settings)
    {
        lock (_watcherGate)
        {
            DisposeWatchersLocked();
            foreach (var folder in settings.Folders.Where(f => f.Enabled))
            {
                if (!Directory.Exists(folder.Path))
                    continue;
                try
                {
                    var watcher = new FileSystemWatcher(folder.Path)
                    {
                        IncludeSubdirectories = folder.IncludeSubfolders,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        InternalBufferSize = 64 * 1024,
                    };
                    var id = folder.Id;
                    watcher.Created += (_, e) => MarkDirty(id, e.FullPath);
                    watcher.Changed += (_, e) => MarkDirty(id, e.FullPath);
                    watcher.Deleted += (_, e) => MarkDirty(id, e.FullPath);
                    watcher.Renamed += (_, e) =>
                    {
                        MarkDirty(id, e.OldFullPath);
                        MarkDirty(id, e.FullPath);
                    };
                    watcher.Error += (_, e) =>
                    {
                        // Buffer overflow or the folder went away: fall back to a full scan.
                        Log.Warn($"File watcher error for {folder.Path}", e.GetException());
                        _fullScanRequested = true;
                        ScheduleWake();
                    };
                    watcher.EnableRaisingEvents = true;
                    _watchers[folder.Id] = watcher;
                }
                catch (Exception ex)
                {
                    // e.g. inotify watch limit on Linux; periodic rescans still cover the folder.
                    Log.Warn($"Could not watch {folder.Path}; relying on periodic scans", ex);
                }
            }
        }
    }

    void MarkDirty(string folderId, string path)
    {
        _dirtyPaths[path] = folderId;
        ScheduleWake();
    }

    /// <summary>Debounces bursts of file events (3 s), but never delays more than 30 s while files keep changing.</summary>
    void ScheduleWake()
    {
        var now = Environment.TickCount64;
        Interlocked.CompareExchange(ref _firstDirtyTick, now, 0);
        var waited = now - Interlocked.Read(ref _firstDirtyTick);
        _debounceTimer.Change(waited > 30_000 ? 0 : 3_000, Timeout.Infinite);
    }

    void DisposeWatchers()
    {
        lock (_watcherGate)
            DisposeWatchersLocked();
    }

    void DisposeWatchersLocked()
    {
        foreach (var watcher in _watchers.Values)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
    }
}
