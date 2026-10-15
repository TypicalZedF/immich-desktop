using ImmichSplash.Core.Settings;

namespace ImmichSplash.Core.Sync;

public sealed partial class SyncEngine
{
    // ---- Scanning ---------------------------------------------------------------------------------

    async Task ScanAsync(AppSettings settings, CancellationToken ct)
    {
        var folders = settings.Folders.Where(f => f.Enabled).ToList();
        var interval = TimeSpan.FromMinutes(settings.RescanIntervalMinutes);
        var full = _fullScanRequested || DateTime.UtcNow - _lastFullScanUtc >= interval;

        if (full)
        {
            _fullScanRequested = false;
            _dirtyPaths.Clear();
            _foldersToRescan.Clear();
            _pruneFolders = false;
            SetState(SyncState.Scanning, Loc.T("Looking for new photos and videos…"));
            try
            {
                await Task.Run(() =>
                {
                    _db.DeleteFoldersExcept(settings.Folders.Select(f => f.Id).ToList());
                    foreach (var folder in folders)
                        ScanTree(folder, folder.Path, ct);
                }, ct);
            }
            catch
            {
                _fullScanRequested = true;
                throw;
            }
            _lastFullScanUtc = DateTime.UtcNow;
        }
        else
        {
            await RescanChangedFoldersAsync(settings, ct);
        }

        if (!full && !_dirtyPaths.IsEmpty)
        {
            var dirty = new Dictionary<string, string>();
            foreach (var path in _dirtyPaths.Keys.ToList())
            {
                if (_dirtyPaths.TryRemove(path, out var folderId))
                    dirty[path] = folderId;
            }
            await Task.Run(() => ProcessDirtyPaths(folders, dirty, ct), ct);
        }

        RefreshCounts(settings);
    }

    /// <summary>Rescans only folders whose settings changed (and forgets removed ones).</summary>
    async Task RescanChangedFoldersAsync(AppSettings settings, CancellationToken ct)
    {
        var ids = _foldersToRescan.Keys.ToList();
        foreach (var id in ids)
            _foldersToRescan.TryRemove(id, out _);
        var prune = _pruneFolders;
        _pruneFolders = false;

        var targets = settings.Folders.Where(f => f.Enabled && ids.Contains(f.Id)).ToList();
        if (targets.Count == 0 && !prune)
            return;

        if (targets.Count > 0)
            SetState(SyncState.Scanning, targets.Count == 1 ? Loc.T("Applying your changes to “{0}”…", targets[0].DisplayName) : Loc.T("Applying your folder changes…"));
        try
        {
            await Task.Run(() =>
            {
                if (prune)
                    _db.DeleteFoldersExcept(settings.Folders.Select(f => f.Id).ToList());
                foreach (var folder in targets)
                    ScanTree(folder, folder.Path, ct);
            }, ct);
        }
        catch
        {
            foreach (var id in ids)
                _foldersToRescan[id] = 0;
            _pruneFolders |= prune;
            throw;
        }
    }

    /// <summary>Indexes everything under <paramref name="root"/> (the folder itself or one of its subdirectories).</summary>
    void ScanTree(SyncFolder folder, string root, CancellationToken ct)
    {
        var isFolderRoot = string.Equals(FolderRules.Normalize(root), FolderRules.Normalize(folder.Path), FolderRules.PathComparison);
        if (!Directory.Exists(root))
        {
            if (isFolderRoot)
                Log.Warn($"Folder not found: {root} (drive disconnected?) - keeping its state");
            else
                _db.DeleteUnder(root);
            return;
        }
        if (!isFolderRoot && !folder.IncludeSubfolders)
            return;

        var filter = new FileFilter(folder, _media);
        var known = _db.GetIndex(folder.Id, isFolderRoot ? null : root, FolderRules.PathComparer);
        var batch = new List<ScannedFile>(512);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = folder.IncludeSubfolders,
            IgnoreInaccessible = true,
            AttributesToSkip = SkipAttributes,
            ReturnSpecialDirectories = false,
            MaxRecursionDepth = 64,
        };

        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (file.Length == 0 || !filter.IsMatch(file.FullName))
                continue;

            var modified = file.LastWriteTimeUtc.Ticks;
            if (known.Remove(file.FullName, out var existing) && existing.Size == file.Length && existing.ModifiedTicks == modified)
                continue;

            batch.Add(new ScannedFile(file.FullName, folder.Id, file.Length, modified));
            if (batch.Count >= 500)
            {
                _db.Upsert(batch);
                batch.Clear();
            }
        }

        _db.Upsert(batch);
        if (known.Count > 0)
            _db.DeletePaths(known.Keys); // deleted locally or no longer matching filters (never deleted from the server)
    }

    void ProcessDirtyPaths(List<SyncFolder> folders, Dictionary<string, string> dirty, CancellationToken ct)
    {
        var byId = folders.ToDictionary(f => f.Id);
        var filters = new Dictionary<string, FileFilter>();
        var changed = new List<ScannedFile>();

        foreach (var (path, folderId) in dirty)
        {
            ct.ThrowIfCancellationRequested();
            if (!byId.TryGetValue(folderId, out var folder))
                continue;

            if (File.Exists(path))
            {
                var file = new FileInfo(path);
                if (!filters.TryGetValue(folderId, out var filter))
                    filters[folderId] = filter = new FileFilter(folder, _media);

                if (file.Length == 0 || (file.Attributes & SkipAttributes) != 0 || !IsIncluded(folder, file) || !filter.IsMatch(path))
                {
                    _db.DeletePaths([path]);
                    continue;
                }

                var known = _db.GetStat(path);
                var modified = file.LastWriteTimeUtc.Ticks;
                if (known is { } k && k.Size == file.Length && k.ModifiedTicks == modified)
                    continue;
                changed.Add(new ScannedFile(file.FullName, folder.Id, file.Length, modified));
            }
            else if (Directory.Exists(path))
            {
                if (IsIncluded(folder, new DirectoryInfo(path)))
                    ScanTree(folder, path, ct);
            }
            else
            {
                _db.DeleteUnder(path);
            }
        }

        _db.Upsert(changed);
    }

    /// <summary>Applies the same rules as the full scan: subfolder setting and hidden/system directories.</summary>
    static bool IsIncluded(SyncFolder folder, FileSystemInfo item)
    {
        var root = FolderRules.Normalize(folder.Path);
        var parent = item is FileInfo f ? f.Directory : ((DirectoryInfo)item).Parent;
        if (item is DirectoryInfo dir && (dir.Attributes & SkipAttributes) != 0)
            return false;
        if (!folder.IncludeSubfolders)
            return item is FileInfo && parent is not null && string.Equals(FolderRules.Normalize(parent.FullName), root, FolderRules.PathComparison);

        for (var d = parent; d is not null; d = d.Parent)
        {
            if (string.Equals(FolderRules.Normalize(d.FullName), root, FolderRules.PathComparison))
                return true;
            if ((d.Attributes & SkipAttributes) != 0)
                return false;
        }
        return false;
    }
}
