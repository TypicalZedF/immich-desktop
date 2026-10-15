using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using ImmichSplash.Core.Api;
using ImmichSplash.Core.Platform;
using ImmichSplash.Core.Services;
using ImmichSplash.Core.Settings;
using ImmichSplash.Core.Storage;

namespace ImmichSplash.Core.Sync;

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
public sealed partial class SyncEngine : IAsyncDisposable
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
    int _runFailed;
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

    /// <summary>The time of day, for the backup window. Replaceable for tests.</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.Now;

    bool OutsideWindow(AppSettings settings) => !BackupSchedule.IsOpen(settings, Now());

    /// <summary>Whether the PC is running on battery (or a UPS). Replaceable for tests.</summary>
    public Func<bool> IsOnBattery { get; set; } = PowerStatus.IsOnBattery;

    bool PausedForData(AppSettings settings) => settings.MeteredBackup == MeteredBackup.Pause && IsMetered();
    bool PhotosOnlyForData(AppSettings settings) => settings.MeteredBackup == MeteredBackup.PhotosOnly && IsMetered();

    /// <summary>Latest snapshot. Raised on a background thread; marshal to the UI thread before touching controls.</summary>
    public event Action<SyncStatus>? StatusChanged;

    /// <summary>Raised (on the engine's thread) after each upload run that had something to do.</summary>
    public event Action<RunSummary>? RunFinished;

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
            var hold = (settings.PauseOnBattery && IsOnBattery()) || PausedForData(settings) || OutsideWindow(settings);
            if (hold == _lastHold)
                return;
            _lastHold = hold;
            Log.Info(hold ? "On battery, a metered connection or outside the backup hours: holding backup" : "Resuming backup");
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
            _activity.Add(ActivityKind.Info, Loc.T("Retrying {0}", Format.Plural(count, "file")));
        Wake();
    }

    /// <summary>Forget all local state and re-check every file against the server (nothing is uploaded twice).</summary>
    public void ResetAndRescan()
    {
        _runCts?.Cancel();
        _db.ResetAll();
        _activity.Add(ActivityKind.Info, Loc.T("Re-checking all files against the server"));
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
            _activity.Add(ActivityKind.Info, Loc.T("Backup resumed"));
        }
        if (settings.Paused)
        {
            RefreshCounts(settings);
            SetState(SyncState.Paused);
            return settings.PausedUntil is { } resumeAt ? resumeAt - DateTimeOffset.Now : null;
        }
        if (settings.PauseOnBattery && IsOnBattery())
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
        if (OutsideWindow(settings))
            return WaitForWindow(settings);

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
            await StackRawPairsAsync(client, settings, ct);

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
        catch (WaitForScheduleException)
        {
            return WaitForWindow(settings);
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
            SetState(SyncState.AuthError, Loc.T("Your sign-in has expired or was revoked. Sign in again to keep backing up."));
            _activity.Add(ActivityKind.Error, Loc.T("Signed out by the server. Sign in again to keep backing up."));
            return null;
        }
        catch (Exception ex) when (NetworkErrors.IsConnectivity(ex) && !shutdown.IsCancellationRequested)
        {
            _offlineFailures++;
            var delay = TimeSpan.FromSeconds(Math.Min(15 * Math.Pow(2, _offlineFailures - 1), 600));
            Log.Warn($"Server unreachable (attempt {_offlineFailures}), retrying in {delay}", ex);
            RefreshCounts(settings);
            SetState(SyncState.Offline, NetworkErrors.Describe(ex) + " " + Loc.T("Retrying in {0}.", Format.Duration(delay)));
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

}
