using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using ImmichDesktop.Core.Api;
using ImmichDesktop.Core.Platform;
using ImmichDesktop.Core.Services;
using ImmichDesktop.Core.Settings;
using ImmichDesktop.Core.Storage;

namespace ImmichDesktop.Core.Sync;

/// <summary>
/// Background backup engine.
/// <list type="number">
/// <item>Scans folders (full scan on start / periodically, incremental via file system watchers).</item>
/// <item>Hashes new or changed files and asks the server which ones it already has.</item>
/// <item>Uploads the rest with limited concurrency and bandwidth, retrying failures with backoff.</item>
/// <item>Optionally adds uploaded assets to a per-folder album.</item>
/// </list>
/// Everything runs on one background loop; the UI only observes <see cref="StatusChanged"/>.
/// </summary>
public sealed class SyncEngine : IAsyncDisposable
{
    const int BatchSize = 50;
    const int AlbumBatchSize = 500;
    const FileAttributes SkipAttributes =
        FileAttributes.Hidden | FileAttributes.System | FileAttributes.Offline | FileAttributes.Temporary |
        (FileAttributes)0x00040000 /* RECALL_ON_OPEN: cloud placeholder */ |
        (FileAttributes)0x00400000 /* RECALL_ON_DATA_ACCESS: cloud placeholder */;

    readonly SettingsStore _settings;
    readonly StateDatabase _db;
    readonly AccountService _account;
    readonly ActivityLog _activity;
    readonly BandwidthLimiter _limiter = new();
    readonly CancellationTokenSource _shutdown = new();
    readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    readonly ConcurrentDictionary<string, string> _dirtyPaths = new();
    readonly ConcurrentDictionary<string, UploadTracker> _active = new();
    readonly Dictionary<string, FileSystemWatcher> _watchers = [];
    readonly Lock _watcherGate = new();
    readonly Dictionary<string, string> _albumIds = new(StringComparer.Ordinal);
    readonly HashSet<string> _albumWarnings = new(StringComparer.Ordinal);
    readonly Timer _debounceTimer;
    readonly Timer _progressTimer;
    readonly Lock _publishGate = new();

    Task? _loop;
    CancellationTokenSource? _runCts;
    volatile bool _fullScanRequested = true;
    DateTime _lastFullScanUtc = DateTime.MinValue;
    long _firstDirtyTick;
    MediaTypes _media = MediaTypes.Default;
    ImmichClient? _preparedClient;
    int _offlineFailures;
    string _foldersKey = "";
    volatile List<SyncFolder> _folderSnapshot = [];
    volatile IReadOnlyDictionary<string, (SyncFolder Folder, FileFilter Filter)> _rules =
        new Dictionary<string, (SyncFolder, FileFilter)>();
    readonly ConcurrentDictionary<string, byte> _foldersToRescan = new();
    volatile bool _pruneFolders;

    // Status
    SyncState _state = SyncState.Idle;
    string? _detail;
    StatusCounts _counts;
    int _runTotal;
    int _runDone;
    long _bytesSent;
    long _speedSampleBytes;
    long _speedSampleTimestamp;
    double _speed;
    DateTimeOffset? _lastSync;
    SyncStatus _status = new() { State = SyncState.Idle };

    public SyncEngine(SettingsStore settings, StateDatabase db, AccountService account, ActivityLog activity)
    {
        _settings = settings;
        _db = db;
        _account = account;
        _activity = activity;
        _debounceTimer = new Timer(_ => { Interlocked.Exchange(ref _firstDirtyTick, 0); Wake(); });
        _progressTimer = new Timer(_ => Publish());
    }

    /// <summary>Files modified more recently than this are assumed to still be copying and are retried later.</summary>
    public TimeSpan FileSettleTime { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Whether the current connection is metered (for data saving). Set by the app.</summary>
    public Func<bool> IsMetered { get; set; } = () => false;

    bool PausedForData(AppSettings settings) => settings.MeteredBackup == MeteredBackup.Pause && IsMetered();
    bool PhotosOnlyForData(AppSettings settings) => settings.MeteredBackup == MeteredBackup.PhotosOnly && IsMetered();

    /// <summary>Latest snapshot. Raised on a background thread; marshal to the UI thread before touching controls.</summary>
    public event Action<SyncStatus>? StatusChanged;

    public SyncStatus Status => _status;

    // ---- Lifecycle & commands ---------------------------------------------------------------------

    public void Start()
    {
        if (_loop is not null)
            return;

        var settings = _settings.Current;
        _foldersKey = FoldersKey(settings);
        _folderSnapshot = settings.Folders;
        RebuildRules();
        ApplyRuntimeSettings(settings);
        ConfigureWatchers(settings);

        _settings.Changed += OnSettingsChanged;
        _account.SessionChanged += OnSessionChanged;
        _loop = Task.Run(LoopAsync);
        _holdWatch = new Timer(_ => CheckHolds(), null, HoldCheckInterval, HoldCheckInterval);
        Log.Info("Sync engine started");
    }

    // ---- Time estimate ----
    // Remaining bytes ÷ the run's average throughput. The average covers the whole run (hashing,
    // checks and small-file overhead included), so it's steadier and more honest than the live speed.

    long _remainingAtCount, _bytesAtCount, _bytesAtRunStart;
    long _runStartTimestamp;

    void StartEstimate(IReadOnlyCollection<string> folderIds)
    {
        _bytesAtRunStart = Interlocked.Read(ref _bytesSent);
        _runStartTimestamp = Stopwatch.GetTimestamp();
        RecountRemaining(folderIds);
    }

    void RecountRemaining(IReadOnlyCollection<string> folderIds)
    {
        // Files in progress are still counted in full, minus what has been sent of them.
        var inFlight = _active.Values.Sum(a => a.Sent);
        _remainingAtCount = Math.Max(0, _db.SumCandidateBytes(folderIds) - inFlight);
        _bytesAtCount = Interlocked.Read(ref _bytesSent);
    }

    TimeSpan? EstimateRemaining(long bytesNow)
    {
        if (_runStartTimestamp == 0)
            return null;
        var elapsed = Stopwatch.GetElapsedTime(_runStartTimestamp).TotalSeconds;
        var sent = bytesNow - _bytesAtRunStart;
        if (elapsed < 20 || sent < 2 * 1024 * 1024)
            return null; // not enough to go on yet
        var remaining = Math.Max(0, _remainingAtCount - (bytesNow - _bytesAtCount));
        var rate = sent / elapsed;
        return rate <= 0 ? null : TimeSpan.FromSeconds(Math.Min(remaining / rate, TimeSpan.FromDays(30).TotalSeconds));
    }

    /// <summary>How often power and data saving are re-checked between runs.</summary>
    static readonly TimeSpan HoldCheckInterval = TimeSpan.FromSeconds(10);

    Timer? _holdWatch;
    bool _lastHold;

    /// <summary>
    /// Pause on battery and data saving take effect right away: unplugging (a laptop or a UPS) stops
    /// an upload in progress, and plugging back in resumes, instead of waiting for the next run.
    /// </summary>
    internal void CheckHolds()
    {
        try
        {
            var settings = _settings.Current;
            var hold = (settings.PauseOnBattery && PowerStatus.IsOnBattery()) || PausedForData(settings);
            if (hold == _lastHold)
                return;
            _lastHold = hold;
            Log.Info(hold ? "On battery or a metered connection: holding backup" : "Power or network is back: resuming backup");
            if (hold)
                _runCts?.Cancel();
            Wake();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not check power and network", ex);
        }
    }

    /// <summary>Re-check right away (full scan, retry failed files).</summary>
    public void SyncNow()
    {
        _db.RetryFailed();
        _fullScanRequested = true;
        Wake();
    }

    /// <summary>Pauses backup, optionally only for a while (resumes automatically afterwards).</summary>
    public void Pause(TimeSpan? duration = null) => _settings.Update(s =>
    {
        s.Paused = true;
        s.PausedUntil = duration is { } d ? DateTimeOffset.Now + d : null;
    });

    public void Resume() => _settings.Update(s =>
    {
        s.Paused = false;
        s.PausedUntil = null;
    });

    public void RetryFailed()
    {
        var count = _db.RetryFailed();
        if (count > 0)
            _activity.Add(ActivityKind.Info, $"Retrying {Format.Plural(count, "file")}");
        Wake();
    }

    /// <summary>Forget all local state and re-check every file against the server (nothing is uploaded twice).</summary>
    public void ResetAndRescan()
    {
        _runCts?.Cancel();
        _db.ResetAll();
        _activity.Add(ActivityKind.Info, "Re-checking all files against the server");
        _fullScanRequested = true;
        Wake();
    }

    public void Wake() => _wake.Writer.TryWrite(true);

    public async ValueTask DisposeAsync()
    {
        _settings.Changed -= OnSettingsChanged;
        _account.SessionChanged -= OnSessionChanged;
        await _shutdown.CancelAsync();
        if (_holdWatch is not null)
            await _holdWatch.DisposeAsync();
        DisposeWatchers();
        await _debounceTimer.DisposeAsync();
        await _progressTimer.DisposeAsync();
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // Shutting down anyway.
            }
        }
        _shutdown.Dispose();
    }

    internal void OnSettingsChanged(AppSettings settings)
    {
        ApplyRuntimeSettings(settings);

        var foldersChanged = false;
        var key = FoldersKey(settings);
        if (key != _foldersKey)
        {
            foldersChanged = true;
            var previous = _folderSnapshot.ToDictionary(f => f.Id);
            foreach (var folder in settings.Folders)
            {
                if (!previous.TryGetValue(folder.Id, out var old))
                {
                    if (folder.Enabled)
                        _foldersToRescan[folder.Id] = 0; // newly added
                    continue;
                }
                // Album name changed -> re-add that folder's assets to the new album.
                if (old.AlbumName != folder.AlbumName)
                    _db.ResetAlbumFlags(folder.Id);
                // Only folders whose file rules changed need rescanning.
                if (folder.Enabled && AffectsWhichFiles(old, folder))
                    _foldersToRescan[folder.Id] = 0;
            }
            if (previous.Keys.Any(id => settings.Folders.All(f => f.Id != id)))
                _pruneFolders = true;

            _foldersKey = key;
            _folderSnapshot = settings.Folders;
            RebuildRules();
            ConfigureWatchers(settings);

            // Changes take effect right away: anything queued or uploading that no longer matches is dropped.
            // Uploads that still match carry on undisturbed.
            CancelUnwantedUploads();
        }

        if (settings.Paused)
            _runCts?.Cancel();

        // Folder edits often come in bursts (typing a pattern, ticking boxes): wait for a quiet moment.
        if (foldersChanged)
            ScheduleWake();
        else
            Wake();
    }

    static bool AffectsWhichFiles(SyncFolder a, SyncFolder b) =>
        a.Enabled != b.Enabled ||
        a.IncludeSubfolders != b.IncludeSubfolders ||
        a.IncludePhotos != b.IncludePhotos ||
        a.IncludeVideos != b.IncludeVideos ||
        !string.Equals(a.Path, b.Path, StringComparison.Ordinal) ||
        !a.ExcludePatterns.SequenceEqual(b.ExcludePatterns);

    void RebuildRules() =>
        _rules = _folderSnapshot.Where(f => f.Enabled).ToDictionary(f => f.Id, f => (f, new FileFilter(f, _media)));

    enum Wanted
    {
        Yes,
        Excluded,   // no longer matches its folder's rules: forget it
        FolderOff,  // folder turned off or removed: leave it for later
    }

    /// <summary>Checks a queued file against the folder rules as they are right now.</summary>
    Wanted IsWanted(string path, string folderId)
    {
        if (!_rules.TryGetValue(folderId, out var rule))
            return Wanted.FolderOff;
        if (!rule.Filter.IsMatch(path))
            return Wanted.Excluded;
        if (!rule.Folder.IncludeSubfolders &&
            !string.Equals(FolderRules.Normalize(Path.GetDirectoryName(path) ?? ""), FolderRules.Normalize(rule.Folder.Path), FolderRules.PathComparison))
            return Wanted.Excluded;
        return Wanted.Yes;
    }

    void CancelUnwantedUploads()
    {
        foreach (var tracker in _active.Values)
        {
            if (IsWanted(tracker.Path, tracker.FolderId) != Wanted.Yes)
                tracker.Cancel();
        }
    }

    void OnSessionChanged()
    {
        _runCts?.Cancel();
        _preparedClient = null;
        _fullScanRequested = true;
        Wake();
    }

    void ApplyRuntimeSettings(AppSettings settings) => _limiter.SetRate(settings.UploadLimitKBps * 1024L);

    static string FoldersKey(AppSettings settings) => JsonSerializer.Serialize(settings.Folders);

    // ---- Main loop --------------------------------------------------------------------------------

    async Task LoopAsync()
    {
        var shutdown = _shutdown.Token;
        TrySetLowPriority();

        while (!shutdown.IsCancellationRequested)
        {
            TimeSpan? retryIn;
            try
            {
                retryIn = await RunCycleAsync(shutdown);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("Sync cycle failed", ex);
                SetState(SyncState.Error, ex.Message);
                retryIn = TimeSpan.FromMinutes(5);
            }

            var interval = TimeSpan.FromMinutes(_settings.Current.RescanIntervalMinutes);
            var delay = retryIn is { } r && r < interval ? r : interval;
            await WaitForWakeAsync(delay, shutdown);
        }
    }

    async Task WaitForWakeAsync(TimeSpan delay, CancellationToken shutdown)
    {
        if (delay <= TimeSpan.Zero)
            return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        timeout.CancelAfter(delay);
        try
        {
            await _wake.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Timed out (or shutting down) - either way, run the next cycle / exit.
        }
    }

    /// <summary>One pass: connect, scan, upload, albums. Returns when to try again (null = normal interval).</summary>
    internal async Task<TimeSpan?> RunCycleAsync(CancellationToken shutdown)
    {
        var settings = _settings.Current;
        var client = _account.Client;

        // Always work from the current folder rules, even if a change notification was missed.
        var key = FoldersKey(settings);
        if (key != _foldersKey)
        {
            _foldersKey = key;
            _folderSnapshot = settings.Folders;
            RebuildRules();
        }

        if (client is null || !settings.IsConfigured)
        {
            SetState(SyncState.NotConfigured);
            return null;
        }
        if (settings.Paused && settings.PausedUntil is { } until && until <= DateTimeOffset.Now)
        {
            Resume();
            settings = _settings.Current;
            _activity.Add(ActivityKind.Info, "Backup resumed");
        }
        if (settings.Paused)
        {
            RefreshCounts(settings);
            SetState(SyncState.Paused);
            return settings.PausedUntil is { } resumeAt ? resumeAt - DateTimeOffset.Now : null;
        }
        if (settings.PauseOnBattery && PowerStatus.IsOnBattery())
        {
            RefreshCounts(settings);
            SetState(SyncState.WaitingForPower);
            return TimeSpan.FromMinutes(1);
        }
        if (PausedForData(settings))
        {
            RefreshCounts(settings);
            SetState(SyncState.WaitingForNetwork);
            return TimeSpan.FromMinutes(1);
        }

        using var run = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        _runCts = run;
        var ct = run.Token;
        try
        {
            await PrepareClientAsync(client, ct);
            _offlineFailures = 0;

            await ScanAsync(settings, ct);
            await UploadAsync(client, settings, ct);
            await SyncAlbumsAsync(client, ct);

            _lastSync = DateTimeOffset.Now;
            RefreshCounts(settings);
            SetState(SyncState.Idle);
            return NextRetryDelay(settings);
        }
        catch (OperationCanceledException) when (!shutdown.IsCancellationRequested)
        {
            // Paused, signed out or folders changed: re-evaluate immediately.
            return TimeSpan.Zero;
        }
        catch (WaitForPowerException)
        {
            RefreshCounts(settings);
            SetState(SyncState.WaitingForPower);
            return TimeSpan.FromMinutes(1);
        }
        catch (WaitForNetworkException)
        {
            RefreshCounts(settings);
            SetState(SyncState.WaitingForNetwork);
            return TimeSpan.FromMinutes(1);
        }
        catch (ImmichApiException ex) when (ex.IsUnauthorized)
        {
            Log.Warn("Server rejected our credentials", ex);
            SetState(SyncState.AuthError, "Your sign-in has expired or was revoked. Sign in again to keep backing up.");
            _activity.Add(ActivityKind.Error, "Signed out by the server. Sign in again to keep backing up.");
            return null;
        }
        catch (Exception ex) when (NetworkErrors.IsConnectivity(ex) && !shutdown.IsCancellationRequested)
        {
            _offlineFailures++;
            var delay = TimeSpan.FromSeconds(Math.Min(15 * Math.Pow(2, _offlineFailures - 1), 600));
            Log.Warn($"Server unreachable (attempt {_offlineFailures}), retrying in {delay}", ex);
            RefreshCounts(settings);
            SetState(SyncState.Offline, $"{NetworkErrors.Describe(ex)} Retrying in {Format.Duration(delay)}.");
            return delay;
        }
        finally
        {
            _runCts = null;
            _active.Clear();
            _progressTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    async Task PrepareClientAsync(ImmichClient client, CancellationToken ct)
    {
        await client.PingAsync(ct);
        if (ReferenceEquals(_preparedClient, client))
            return;

        client.ServerVersion ??= await client.GetVersionAsync(ct);
        try
        {
            _media = MediaTypes.FromServer(await client.GetMediaTypesAsync(ct));
        }
        catch (ImmichApiException)
        {
            _media = MediaTypes.Default;
        }
        _albumIds.Clear();
        _albumWarnings.Clear();
        _preparedClient = client;
        RebuildRules();
    }

    TimeSpan? NextRetryDelay(AppSettings settings)
    {
        var next = _db.GetNextAttempt(EnabledFolderIds(settings));
        if (next is null)
            return null;
        var delay = next.Value - DateTime.UtcNow;
        return delay < TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(15) : delay;
    }

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
            SetState(SyncState.Scanning, "Looking for new photos and videos…");
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
            SetState(SyncState.Scanning, targets.Count == 1 ? $"Applying your changes to \u201C{targets[0].DisplayName}\u201D\u2026" : "Applying your folder changes\u2026");
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

    // ---- Uploading --------------------------------------------------------------------------------

    async Task UploadAsync(ImmichClient client, AppSettings settings, CancellationToken ct)
    {
        var folderIds = EnabledFolderIds(settings);
        _runTotal = _db.CountCandidates(folderIds);
        _runDone = 0;
        if (_runTotal == 0)
            return;
        StartEstimate(folderIds);

        var uploaded = 0;
        var alreadyThere = 0;
        SetState(SyncState.Uploading, "Checking files…");
        _progressTimer.Change(400, 400);

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (settings.PauseOnBattery && PowerStatus.IsOnBattery())
                    throw new WaitForPowerException();
                if (PausedForData(settings))
                    throw new WaitForNetworkException();

                folderIds = _rules.Keys.ToList(); // folders turned off mid-run stop after this batch
                RecountRemaining(folderIds);
                var batch = _db.GetCandidates(folderIds, BatchSize);
                if (batch.Count == 0)
                    break;

                // Data saving: on a metered connection videos wait for a better one.
                if (PhotosOnlyForData(settings))
                {
                    var videos = batch.Where(r => _media.Classify(r.Path) == MediaKind.Video).ToList();
                    if (videos.Count > 0)
                    {
                        foreach (var video in videos)
                            _db.Defer(video.Path, DateTime.UtcNow.AddMinutes(15));
                        if (!_deferredVideosNoted)
                        {
                            _deferredVideosNoted = true;
                            _activity.Add(ActivityKind.Info, "On a metered connection: videos will upload when you're on an unmetered one");
                        }
                        batch = batch.Except(videos).ToList();
                        if (batch.Count == 0)
                            continue;
                    }
                }
                else
                {
                    _deferredVideosNoted = false;
                }

                var ready = await PrepareBatchAsync(batch, ct);
                if (ready.Count == 0)
                    continue;

                var results = await client.BulkUploadCheckAsync(ready.Select(r => new ChecksumItem(r.Path, r.Checksum!)).ToList(), ct);
                var byPath = results.ToDictionary(r => r.Id, StringComparer.Ordinal);
                var toUpload = new List<FileRecord>(ready.Count);

                foreach (var record in ready)
                {
                    if (!byPath.TryGetValue(record.Path, out var result))
                    {
                        _db.Defer(record.Path, DateTime.UtcNow.AddMinutes(5));
                        continue;
                    }

                    if (result.IsAccepted)
                    {
                        toUpload.Add(record);
                    }
                    else if (result.IsDuplicate)
                    {
                        _db.MarkDone(record.Path, FileStatus.Duplicate, result.AssetId);
                        alreadyThere++;
                        Interlocked.Increment(ref _runDone);
                    }
                    else
                    {
                        _db.MarkUnsupported(record.Path, "This file type isn't supported by your Immich server.");
                        _activity.Add(ActivityKind.Warning, $"Skipped {Path.GetFileName(record.Path)} — file type not supported by the server", record.Path);
                        Interlocked.Increment(ref _runDone);
                    }
                }

                _detail = null;
                Publish();

                await Parallel.ForEachAsync(
                    toUpload,
                    new ParallelOptions { MaxDegreeOfParallelism = settings.MaxConcurrentUploads, CancellationToken = ct },
                    async (record, token) =>
                    {
                        if (await UploadOneAsync(client, record, token))
                            Interlocked.Increment(ref uploaded);
                    });

                await SyncAlbumsDuringUploadAsync(client, ct);
                RefreshCounts(settings);
            }
        }
        finally
        {
            if (uploaded > 0 || alreadyThere > 0)
            {
                var message = uploaded > 0 ? $"Backed up {Format.Plural(uploaded, "new file")}" : "No new files to upload";
                if (alreadyThere > 0)
                    message += $" · {Format.Count(alreadyThere)} already on the server";
                _activity.Add(ActivityKind.Success, message);
            }
        }
    }

    /// <summary>Hashes a batch, skipping files that vanished, are still being written, or can't be read.</summary>
    async Task<List<FileRecord>> PrepareBatchAsync(List<FileRecord> batch, CancellationToken ct)
    {
        var ready = new List<FileRecord>(batch.Count);
        foreach (var record in batch)
        {
            ct.ThrowIfCancellationRequested();
            if (Skip(record))
                continue;

            var file = new FileInfo(record.Path);
            if (!file.Exists)
            {
                _db.DeletePaths([record.Path]);
                Interlocked.Increment(ref _runDone);
                continue;
            }

            if (DateTime.UtcNow - file.LastWriteTimeUtc < FileSettleTime)
            {
                _db.Defer(record.Path, DateTime.UtcNow + FileSettleTime + TimeSpan.FromSeconds(5));
                continue;
            }

            var current = record;
            if (file.Length != record.Size || file.LastWriteTimeUtc.Ticks != record.ModifiedTicks)
            {
                current = record with { Size = file.Length, ModifiedTicks = file.LastWriteTimeUtc.Ticks, Checksum = null };
                _db.UpdateStat(record.Path, current.Size, current.ModifiedTicks);
            }

            if (current.Checksum is null)
            {
                try
                {
                    var checksum = await FileHasher.Sha1HexAsync(record.Path, ct);
                    _db.SetChecksum(record.Path, checksum);
                    current = current with { Checksum = checksum };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _db.MarkFailed(record.Path, $"Couldn't read the file: {ex.Message}", NextAttempt(record.Attempts + 1));
                    Interlocked.Increment(ref _runDone);
                    continue;
                }
            }

            ready.Add(current);
        }
        return ready;
    }

    /// <summary>Drops a queued file that the current folder rules no longer want.</summary>
    bool Skip(FileRecord record)
    {
        var wanted = IsWanted(record.Path, record.FolderId);
        if (wanted == Wanted.Yes)
            return false;
        if (wanted == Wanted.Excluded)
            _db.DeletePaths([record.Path]);
        Interlocked.Increment(ref _runDone);
        return true;
    }

    async Task<bool> UploadOneAsync(ImmichClient client, FileRecord record, CancellationToken ct)
    {
        if (Skip(record))
            return false;

        using var tracker = new UploadTracker(record.Path, record.FolderId, record.Size, ct);
        _active[record.Path] = tracker;
        ct = tracker.Token;
        try
        {
            var file = new FileInfo(record.Path);
            if (!file.Exists)
            {
                _db.DeletePaths([record.Path]);
                Interlocked.Increment(ref _runDone);
                return false;
            }
            if (file.Length != record.Size || file.LastWriteTimeUtc.Ticks != record.ModifiedTicks)
            {
                // Changed since it was hashed; it'll be picked up again with a fresh checksum.
                _db.UpdateStat(record.Path, file.Length, file.LastWriteTimeUtc.Ticks);
                return false;
            }

            var created = file.CreationTimeUtc < file.LastWriteTimeUtc ? file.CreationTimeUtc : file.LastWriteTimeUtc;
            var request = new UploadRequest(record.Path, file.Name, record.Size, record.Checksum!, created, file.LastWriteTimeUtc);
            var result = await client.UploadAsync(request, _limiter, sent =>
            {
                var delta = sent - tracker.Sent;
                tracker.Sent = sent;
                Interlocked.Add(ref _bytesSent, delta);
            }, ct);

            _db.MarkDone(record.Path, result.IsDuplicate ? FileStatus.Duplicate : FileStatus.Uploaded, result.Id);
            Interlocked.Increment(ref _runDone);
            if (!result.IsDuplicate)
                _activity.Add(ActivityKind.Uploaded, file.Name, record.Path);
            return !result.IsDuplicate;
        }
        catch (OperationCanceledException) when (tracker.WasCancelledIndividually)
        {
            // Excluded or its folder turned off while uploading: stop just this file.
            Log.Info($"Stopped uploading {record.Path}: no longer included");
            Skip(record);
            return false;
        }
        catch (ImmichApiException ex) when (!ex.IsUnauthorized && !ex.IsTransient)
        {
            Fail(record, ex.Message);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && !NetworkErrors.IsConnectivity(ex))
        {
            Fail(record, $"Couldn't read the file: {ex.Message}");
            return false;
        }
        catch (Exception ex) when (NetworkErrors.IsConnectivity(ex))
        {
            // Server or network went away: put the file back and let the cycle handle going offline.
            _db.Defer(record.Path, DateTime.UtcNow.AddSeconds(30));
            throw;
        }
        finally
        {
            _active.TryRemove(record.Path, out _);
        }
    }

    void Fail(FileRecord record, string reason)
    {
        Log.Warn($"Upload failed for {record.Path}: {reason}");
        _db.MarkFailed(record.Path, reason, NextAttempt(record.Attempts + 1));
        Interlocked.Increment(ref _runDone);
        _activity.Add(ActivityKind.Error, $"Couldn't upload {Path.GetFileName(record.Path)}: {reason}", record.Path);
    }

    static DateTime NextAttempt(int attempts) =>
        DateTime.UtcNow + TimeSpan.FromMinutes(Math.Min(Math.Pow(2, attempts), 12 * 60));

    // ---- Albums -----------------------------------------------------------------------------------

    /// <summary>
    /// Adds uploaded (or already-on-server) files to their folder's album. Runs after every upload batch,
    /// not just at the end, so albums fill up while a big backup is still running, and always uses the
    /// folder settings as they are right now (an album set mid-upload applies immediately).
    /// </summary>
    async Task SyncAlbumsAsync(ImmichClient client, CancellationToken ct)
    {
        foreach (var folder in _folderSnapshot.Where(f => f.Enabled && !string.IsNullOrWhiteSpace(f.AlbumName)))
        {
            var name = folder.AlbumName!.Trim();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var work = _db.GetAlbumWork(folder.Id, AlbumBatchSize);
                if (work.Count == 0)
                    break;

                try
                {
                    var albumId = await GetOrCreateAlbumAsync(client, name, ct);
                    await client.AddAssetsToAlbumAsync(albumId, work.Select(w => w.AssetId).Distinct().ToList(), ct);
                    _db.MarkAlbumDone(work.Select(w => w.Path));
                }
                catch (ImmichApiException ex) when (ex.StatusCode is HttpStatusCode.Forbidden)
                {
                    if (_albumWarnings.Add(name))
                        _activity.Add(ActivityKind.Warning, $"Couldn't add photos to the album “{name}”. If you signed in with an API key, give it album permissions.");
                    break;
                }
                catch (ImmichApiException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
                {
                    // The album was probably deleted on the server; look it up again next cycle.
                    Log.Warn($"Album \"{name}\" update failed", ex);
                    _albumIds.Remove(name);
                    break;
                }
            }
        }
    }

    /// <summary>Album step between upload batches: a hiccup here must never stop the backup itself.</summary>
    async Task SyncAlbumsDuringUploadAsync(ImmichClient client, CancellationToken ct)
    {
        try
        {
            await SyncAlbumsAsync(client, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !NetworkErrors.IsConnectivity(ex) && ex is not ImmichApiException { IsUnauthorized: true })
        {
            Log.Warn("Album update during upload failed; will retry after the batch", ex);
        }
    }

    async Task<string> GetOrCreateAlbumAsync(ImmichClient client, string name, CancellationToken ct)
    {
        if (_albumIds.TryGetValue(name, out var cached))
            return cached;

        var albums = await client.GetAlbumsAsync(ct);
        var album = albums.FirstOrDefault(a => a.AlbumName == name)
                    ?? await client.CreateAlbumAsync(name, null, ct);
        if (!albums.Contains(album))
            _activity.Add(ActivityKind.Info, $"Created album “{name}”");
        _albumIds[name] = album.Id;
        return album.Id;
    }

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

    // ---- Status -----------------------------------------------------------------------------------

    static List<string> EnabledFolderIds(AppSettings settings) =>
        settings.Folders.Where(f => f.Enabled).Select(f => f.Id).ToList();

    void RefreshCounts(AppSettings settings)
    {
        try
        {
            _counts = _db.GetCounts(EnabledFolderIds(settings));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read counts", ex);
        }
    }

    void SetState(SyncState state, string? detail = null)
    {
        _state = state;
        _detail = detail;
        Publish();
    }

    void Publish()
    {
        SyncStatus snapshot;
        lock (_publishGate)
        {
            var now = Stopwatch.GetTimestamp();
            var bytes = Interlocked.Read(ref _bytesSent);
            if (_state == SyncState.Uploading)
            {
                var elapsed = Stopwatch.GetElapsedTime(_speedSampleTimestamp, now).TotalSeconds;
                if (_speedSampleTimestamp == 0)
                {
                    _speedSampleTimestamp = now;
                    _speedSampleBytes = bytes;
                }
                else if (elapsed >= 0.5)
                {
                    var instant = (bytes - _speedSampleBytes) / elapsed;
                    _speed = _speed == 0 ? instant : _speed * 0.7 + instant * 0.3;
                    _speedSampleTimestamp = now;
                    _speedSampleBytes = bytes;
                }
            }
            else
            {
                _speed = 0;
                _speedSampleTimestamp = 0;
            }

            snapshot = new SyncStatus
            {
                State = _state,
                Detail = _detail,
                Counts = _counts,
                RunTotal = _runTotal,
                RunDone = Math.Min(Volatile.Read(ref _runDone), _runTotal),
                Active = _active.Values
                    .Select(t => new ActiveUpload(t.Path, t.Size, t.Sent))
                    .OrderBy(a => a.Path, StringComparer.Ordinal)
                    .ToList(),
                BytesPerSecond = _speed,
                LastSync = _lastSync,
                Remaining = _state == SyncState.Uploading ? EstimateRemaining(bytes) : null,
            };
            _status = snapshot;
        }

        try
        {
            StatusChanged?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            Log.Error("Status listener failed", ex);
        }
    }

    static void TrySetLowPriority()
    {
        // Backups should never make the PC feel slow. (On Linux this maps to a higher nice value.)
        try
        {
            using var process = Process.GetCurrentProcess();
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not lower process priority", ex);
        }
    }

    sealed class UploadTracker(string path, string folderId, long size, CancellationToken runToken) : IDisposable
    {
        readonly CancellationTokenSource _cts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
        volatile bool _cancelled;
        long _sent;
        public string Path { get; } = path;
        public string FolderId { get; } = folderId;
        public long Size { get; } = size;
        public CancellationToken Token => _cts.Token;
        public bool WasCancelledIndividually => _cancelled && !runToken.IsCancellationRequested;

        public void Cancel()
        {
            _cancelled = true;
            try { _cts.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
        }

        public void Dispose() => _cts.Dispose();

        public long Sent
        {
            get => Interlocked.Read(ref _sent);
            set => Interlocked.Exchange(ref _sent, value);
        }
    }

    sealed class WaitForPowerException : Exception;

    sealed class WaitForNetworkException : Exception;

    bool _deferredVideosNoted;
}
