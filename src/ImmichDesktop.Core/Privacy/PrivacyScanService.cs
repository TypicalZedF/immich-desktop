using ImmichDesktop.Core.Api;
using ImmichDesktop.Core.Platform;
using ImmichDesktop.Core.Services;
using ImmichDesktop.Core.Settings;
using ImmichDesktop.Core.Storage;

namespace ImmichDesktop.Core.Privacy;

public enum PrivacyScanState { Off, Unavailable, Starting, Scanning, Paused, Done, Error }

/// <summary>Where the privacy check is, for the Utilities page.</summary>
/// <param name="Remaining">Estimated time to finish, from the recent pace (null until there's enough to go on).</param>
/// <param name="OneTime">A one-time check (the setting is off): it stops once the library is done.</param>
public sealed record PrivacyScanStatus(PrivacyScanState State, int Checked, int Total, int Suggestions, bool OnGpu, string? Detail = null,
    TimeSpan? Remaining = null, bool OneTime = false)
{
    public static PrivacyScanStatus Off { get; } = new(PrivacyScanState.Off, 0, 0, 0, false);
}

/// <summary>
/// Looks through your whole Immich library, on this PC, for pictures you may want in the locked
/// folder: nudity, payment cards, IDs, bank papers, passwords, medical records. It downloads each
/// photo's preview from your own server, checks it with the bundled models, and keeps only the
/// result (what kind of thing was found) in the local database. Nothing is sent anywhere and
/// nothing is moved without you. Pauses on battery, on metered connections and (if chosen)
/// while you're using the PC; picks up where it left off.
/// </summary>
public sealed class PrivacyScanService : IDisposable
{
    /// <summary>Bump when the checks change enough that everything should be looked at again.</summary>
    public const int ChecksVersion = 1;

    const float ExplicitThreshold = 0.6f;
    const float SuggestiveThreshold = 0.85f;
    static readonly TimeSpan IdleNeeded = TimeSpan.FromMinutes(2);
    static readonly TimeSpan RecheckLibraryEvery = TimeSpan.FromHours(1);

    readonly SettingsStore _settings;
    readonly StateDatabase _db;
    readonly AccountService _account;
    readonly Func<bool> _isMetered;
    readonly Func<bool> _isPrivateNetwork;
    readonly Func<byte[], BgraImage?> _decode;
    readonly Lock _gate = new();
    CancellationTokenSource? _run;
    Task? _task;
    bool? _runningWithGpu;
    bool _once;        // a one-time check was asked for
    bool _wasEnabled;  // the setting, last time it was applied

    /// <param name="isPrivateNetwork">
    /// True on a home (private) network. The check only runs then: never on public Wi-Fi, even
    /// though it only talks to your own server.
    /// </param>
    public PrivacyScanService(SettingsStore settings, StateDatabase db, AccountService account, Func<bool> isMetered,
        Func<bool> isPrivateNetwork, Func<byte[], BgraImage?> decode)
    {
        _isPrivateNetwork = isPrivateNetwork;
        _settings = settings;
        _db = db;
        _account = account;
        _isMetered = isMetered;
        _decode = decode;
        _settings.Changed += _ => Apply();
        _account.SessionChanged += Apply;
        Apply();
    }

    public PrivacyScanStatus Status { get; private set; } = PrivacyScanStatus.Off;

    /// <summary>Raised from a background thread.</summary>
    public event Action<PrivacyScanStatus>? StatusChanged;

    /// <summary>Flagged pictures waiting for you to decide.</summary>
    public int Suggestions => _db.CountPrivacySuggestions();

    /// <summary>Starts or stops to match the settings and sign-in.</summary>
    void Apply()
    {
        var s = _settings.Current;
        lock (_gate)
        {
            // Turning the setting off also stops a one-time check that was running alongside it.
            if (_wasEnabled && !s.PrivacyScanEnabled)
                _once = false;
            _wasEnabled = s.PrivacyScanEnabled;
            var wanted = (s.PrivacyScanEnabled || _once) && _account.IsSignedIn;
            if (wanted && _task is { IsCompleted: false } && _runningWithGpu == s.PrivacyScanUseGpu)
                return;
            _run?.Cancel();
            _run = null;
            if (!wanted)
            {
                // A finished one-time check keeps saying so (until it's started again or turned on).
                if (Status is not { State: PrivacyScanState.Done, OneTime: true })
                    Report(PrivacyScanStatus.Off with { Suggestions = Suggestions });
                return;
            }
            if (!OnDeviceModels.Available)
            {
                Report(new PrivacyScanStatus(PrivacyScanState.Unavailable, 0, 0, 0, false, Loc.T("The privacy check's files are missing. Reinstall the app.")));
                return;
            }
            var cts = _run = new CancellationTokenSource();
            var previous = _task;
            _runningWithGpu = s.PrivacyScanUseGpu;
            _task = Task.Run(async () =>
            {
                if (previous is not null)
                    await previous.ContinueWith(_ => { }, TaskScheduler.Default); // let the last run let go of the models
                await RunAsync(s.PrivacyScanUseGpu, cts.Token);
            });
        }
    }

    async Task RunAsync(bool useGpu, CancellationToken ct)
    {
        ReportRun(new PrivacyScanStatus(PrivacyScanState.Starting, 0, 0, Suggestions, false), ct);
        NsfwClassifier? classifier = null;
        PhotoTextReader? reader = null;
        try
        {
            classifier = new NsfwClassifier(useGpu);
            reader = new PhotoTextReader(useGpu);
            var onGpu = classifier.OnGpu;
            while (!ct.IsCancellationRequested)
            {
                if (_account.Client is not { } client)
                    return;
                // Not even the photo list is fetched until it's allowed to run (e.g. on a home network).
                await WaitUntilAllowedAsync(Status.Checked, Status.Total, onGpu, ct);
                var library = await ListLibraryAsync(client, ct);
                var done = await Task.Run(() => _db.GetPrivacyScanned(ChecksVersion), ct);
                var todo = library.Where(id => !done.Contains(id)).ToList();
                var checkedCount = library.Count - todo.Count;
                var lastReport = DateTime.MinValue;
                // Pace over the last photos (not since the start: pauses and the first slow model load would skew it).
                var recent = new Queue<DateTime>();

                // Fetch the next preview while the current one is being checked.
                // (Only ever one ahead, and only while it's allowed to run.)
                Task<byte[]?>? next = null;
                for (var i = 0; i < todo.Count; i++)
                {
                    if (await WaitUntilAllowedAsync(checkedCount, library.Count, onGpu, ct))
                        recent.Clear(); // a pause isn't part of the pace
                    var data = await (next ?? FetchAsync(client, todo[i], ct));
                    next = i + 1 < todo.Count && PauseReason() is null ? FetchAsync(client, todo[i + 1], ct) : null;
                    if (data is not null && _decode(data) is { } image)
                    {
                        var (kinds, score) = Check(classifier, reader, image);
                        _db.SavePrivacyScan(todo[i], ChecksVersion, (int)kinds, score);
                    }
                    else
                    {
                        _db.SavePrivacyScan(todo[i], ChecksVersion, 0, 0); // no picture to check (e.g. a format we can't open)
                    }
                    checkedCount++;
                    recent.Enqueue(DateTime.UtcNow);
                    while (recent.Count > 50)
                        recent.Dequeue();
                    ct.ThrowIfCancellationRequested();
                    if (DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(0.5) || i == todo.Count - 1)
                    {
                        lastReport = DateTime.UtcNow;
                        TimeSpan? remaining = recent.Count >= 5
                            ? (recent.Last() - recent.Peek()) / (recent.Count - 1) * (library.Count - checkedCount)
                            : null;
                        ReportRun(new PrivacyScanStatus(PrivacyScanState.Scanning, checkedCount, library.Count, Suggestions, onGpu, Remaining: remaining), ct);
                    }
                }
                ReportRun(new PrivacyScanStatus(PrivacyScanState.Done, library.Count, library.Count, Suggestions, onGpu), ct);
                lock (_gate)
                {
                    if (!_settings.Current.PrivacyScanEnabled)
                    {
                        _once = false; // a one-time check: done, so stop (new photos wait for the next one)
                        return;
                    }
                }
                await Task.Delay(RecheckLibraryEvery, ct); // then look for new photos
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("The privacy check stopped", ex);
            ReportRun(new PrivacyScanStatus(PrivacyScanState.Error, Status.Checked, Status.Total, Suggestions, false, NetworkErrors.Describe(ex)), ct);
            // Try again in a while (e.g. the server was unreachable).
            _ = RetryLaterAsync(ct);
        }
        finally
        {
            classifier?.Dispose();
            reader?.Dispose();
        }
    }

    async Task RetryLaterAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(10), ct);
            await (_task ?? Task.CompletedTask); // this run has finished by now
            Apply();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>What a picture shows, and how sure we are (0–1, for sorting suggestions).</summary>
    public static (SensitiveKind Kinds, double Score) Check(NsfwClassifier classifier, PhotoTextReader reader, BgraImage image)
    {
        var kinds = SensitiveKind.None;
        double score = 0;
        var nsfw = classifier.Classify(image);
        if (nsfw.Explicit >= ExplicitThreshold)
        {
            kinds |= SensitiveKind.Explicit;
            score = nsfw.Explicit;
        }
        else if (nsfw.Sexy >= SuggestiveThreshold)
        {
            kinds |= SensitiveKind.Suggestive;
            score = nsfw.Sexy * 0.7;
        }
        var text = SensitiveText.Find(reader.Read(image).Select(l => l.Text));
        if (text != SensitiveKind.None)
        {
            kinds |= text;
            score = Math.Max(score, text.HasFlag(SensitiveKind.PaymentCard) || text.HasFlag(SensitiveKind.IdDocument) ? 0.95 : 0.8);
        }
        return (kinds, score);
    }

    /// <summary>Every photo and video in the timeline and the archive (not the locked folder or the trash).</summary>
    static async Task<List<string>> ListLibraryAsync(ImmichClient client, CancellationToken ct)
    {
        var ids = new List<string>();
        foreach (var filter in new[] { TimelineFilter.All, new TimelineFilter(Visibility: "archive") })
        {
            foreach (var bucket in await client.GetTimeBucketsAsync(filter, ct))
            {
                if (bucket.Count == 0)
                    continue;
                foreach (var asset in await client.GetTimeBucketAsync(bucket.Key, filter, ct))
                    ids.Add(asset.Id);
            }
        }
        return ids.Distinct(StringComparer.Ordinal).ToList();
    }

    static async Task<byte[]?> FetchAsync(ImmichClient client, string assetId, CancellationToken ct)
    {
        try
        {
            return await client.GetAssetImageAsync(assetId, AssetImageSize.Preview, ct);
        }
        catch (ImmichApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.BadRequest)
        {
            return null; // deleted meanwhile, or no preview
        }
    }

    /// <summary>Waits while it isn't allowed to run; true if it had to wait.</summary>
    async Task<bool> WaitUntilAllowedAsync(int checkedCount, int total, bool onGpu, CancellationToken ct)
    {
        var waited = false;
        while (true)
        {
            var reason = PauseReason();
            if (reason is null)
                return waited;
            waited = true;
            if (Status.State != PrivacyScanState.Paused || Status.Detail != reason)
                ReportRun(new PrivacyScanStatus(PrivacyScanState.Paused, checkedCount, total, Suggestions, onGpu, reason), ct);
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
    }

    string? PauseReason()
    {
        var s = _settings.Current;
        if (!_isPrivateNetwork())
            return Loc.T("Paused: only runs on a private network, like at home");
        if (PowerStatus.IsOnBattery())
            return Loc.T("Paused while on battery");
        if (_isMetered())
            return Loc.T("Paused on a metered connection");
        if (s.PrivacyScanOnlyWhenIdle && UserIdle.Duration() is { } idle && idle < IdleNeeded)
            return Loc.T("Waiting until the PC isn't being used");
        return null;
    }

    /// <summary>
    /// Checks the library once, with the setting off: it stops when it's done instead of watching
    /// for new photos. The same rules apply (home network only, pauses on battery and so on).
    /// </summary>
    public void ScanOnce()
    {
        lock (_gate)
            _once = true;
        Apply();
    }

    /// <summary>Stops a one-time check.</summary>
    public void StopOnce()
    {
        lock (_gate)
            _once = false;
        Apply();
    }

    /// <summary>Starts over (e.g. after its results were forgotten).</summary>
    public void Restart()
    {
        lock (_gate)
        {
            _run?.Cancel();
            _run = null;
            _runningWithGpu = null;
            if (Status.OneTime)
                Report(PrivacyScanStatus.Off with { Suggestions = Suggestions }); // its results are gone
        }
        Apply();
    }

    /// <summary>The suggestions were acted on (moved or dismissed): refresh the count.</summary>
    public void Refresh() => Report(Status with { Suggestions = Suggestions });

    /// <summary>
    /// A status from a run. Dropped once that run was stopped: a photo finishing after you turned
    /// the check off must not put "Checking…" back on the page.
    /// </summary>
    void ReportRun(PrivacyScanStatus status, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!ct.IsCancellationRequested)
                Report(status with { OneTime = _once && !_settings.Current.PrivacyScanEnabled });
        }
    }

    void Report(PrivacyScanStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _run?.Cancel();
            _run = null;
        }
    }
}
