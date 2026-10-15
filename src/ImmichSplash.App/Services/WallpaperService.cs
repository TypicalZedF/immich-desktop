using ImmichSplash.Core;
using ImmichSplash.Core.Api;
using ImmichSplash.Core.Platform;
using ImmichSplash.Core.Settings;

namespace ImmichSplash.App.Services;

/// <summary>
/// A digital picture frame for your desktop: every so often, sets the desktop background to a random
/// photo from your favorites, an album or your whole library. Prefers landscape photos (they fill a
/// screen) and avoids repeating recent ones. Runs from the tray, so the window doesn't need to be open.
/// </summary>
public sealed class WallpaperService : IDisposable
{
    const int MaxSize = 3840;
    const int RememberRecent = 30;

    readonly SemaphoreSlim _gate = new(1, 1);
    readonly Queue<string> _recent = new();
    readonly Random _random = new();
    Timer? _timer;
    int _counter;

    public static bool IsSupported => DesktopWallpaper.IsSupported;

    public DateTimeOffset? LastChanged { get; private set; }
    public string? Error { get; private set; }
    public bool IsChanging { get; private set; }

    /// <summary>Raised (on any thread) when a change starts, finishes or fails.</summary>
    public event Action? Changed;

    public string StatusText =>
        IsChanging ? Tr.T("Changing your background…")
        : Error is { } error ? error
        : LastChanged is { } t ? Tr.T("Changed {0}", Format.Relative(t))
        : "";

    /// <summary>(Re)starts the timer from the current settings.</summary>
    public void Apply(AppSettings settings)
    {
        _timer?.Dispose();
        _timer = null;
        if (!settings.WallpaperEnabled || !IsSupported)
            return;
        var interval = TimeSpan.FromMinutes(Math.Max(5, settings.WallpaperIntervalMinutes));
        // First change soon after starting (or turning it on), then on the interval.
        var first = LastChanged is { } last && DateTimeOffset.Now - last < interval ? interval - (DateTimeOffset.Now - last) : TimeSpan.FromSeconds(5);
        _timer = new Timer(_ => _ = ChangeNowAsync(), null, first, interval);
    }

    public async Task ChangeNowAsync()
    {
        if (!IsSupported || AppServices.Account.Client is not { } client || !await _gate.WaitAsync(0))
            return;
        IsChanging = true;
        Error = null;
        Changed?.Invoke();
        try
        {
            var settings = AppServices.Settings.Current;
            var filter = settings.WallpaperSource switch
            {
                WallpaperSource.Album when settings.WallpaperAlbumId is { } album => new TimelineFilter(AlbumId: album),
                WallpaperSource.Library => TimelineFilter.All,
                _ => new TimelineFilter(Favorites: true),
            };
            var asset = settings.WallpaperOrder == WallpaperOrder.Random
                ? await PickAsync(client, filter)
                : await PickNextAsync(client, filter, settings);
            if (asset is null)
            {
                Error = settings.WallpaperSource switch
                {
                    WallpaperSource.Album => Tr.T("That album has no photos to use."),
                    WallpaperSource.Favorites => Tr.T("You have no favorite photos yet. Tap the heart on a photo, or pick an album."),
                    _ => Tr.T("Your library has no photos yet."),
                };
                return;
            }

            var folder = Directory.CreateDirectory(Path.Combine(AppPaths.DataDir, "wallpaper")).FullName;
            // Alternate file names: Windows caches a background by path and may not notice a changed file.
            var path = Path.Combine(folder, $"background-{_counter++ % 2}.jpg");
            var bitmap = await LoadSharpestAsync(client, asset);
            await Task.Run(() =>
            {
                using (bitmap)
                    File.WriteAllBytes(path, Images.ToJpeg(bitmap, 95));
                DesktopWallpaper.Set(path);
            });
            Remember(asset.Id);
            LastChanged = DateTimeOffset.Now;
            Log.Info("Desktop background changed");
        }
        catch (Exception ex)
        {
            Log.Warn("Could not change the desktop background", ex);
            Error = Tr.T("Couldn't change your background. {0}", NetworkErrors.Describe(ex));
        }
        finally
        {
            IsChanging = false;
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// The photo as sharp as the server can give it: the full-size image (only there when the server makes them), else
    /// the original file (if it's a format we can open; not HEIC or RAW), else the ~1440 px preview. On a metered
    /// connection, just the preview (data saving).
    /// </summary>
    static async Task<SkiaSharp.SKBitmap> LoadSharpestAsync(ImmichClient client, TimelineAsset asset)
    {
        var tries = new List<(string Name, Func<Task<byte[]>> Fetch)>();
        if (!AppServices.Network.IsMetered)
        {
            tries.Add(("full size", () => client.GetAssetImageAsync(asset.Id, AssetImageSize.Fullsize, CancellationToken.None)));
            tries.Add(("original", async () =>
            {
                using var stream = new MemoryStream();
                await client.DownloadOriginalAsync(asset.Id, stream, CancellationToken.None);
                return stream.ToArray();
            }));
        }
        tries.Add(("preview", () => client.GetAssetImageAsync(asset.Id, AssetImageSize.Preview, CancellationToken.None)));

        Exception? last = null;
        foreach (var (name, fetch) in tries)
        {
            try
            {
                var data = await fetch();
                var bitmap = await Task.Run(() => Images.DecodeUpright(data, MaxSize));
                // The full-size image or the original may still be small (an old photo): fine, it's the best there is.
                Log.Info($"Desktop background from the {name} image ({bitmap.Width}x{bitmap.Height})");
                return bitmap;
            }
            catch (Exception ex) when (ex is ImmichApiException or InvalidDataException)
            {
                last = ex; // not made by this server, or a format we can't open: try the next
            }
        }
        throw last ?? new InvalidDataException("No picture");
    }

    /// <summary>
    /// In order (oldest or newest first): the photo after the one shown last, and back to the start after the
    /// last one. The place is remembered in the settings, per source.
    /// </summary>
    static async Task<TimelineAsset?> PickNextAsync(ImmichClient client, TimelineFilter filter, AppSettings settings)
    {
        var oldestFirst = settings.WallpaperOrder == WallpaperOrder.OldestFirst;
        var source = $"{settings.WallpaperSource}:{settings.WallpaperAlbumId}:{settings.WallpaperOrder}";
        // The timeline comes newest first.
        var buckets = (await client.GetTimeBucketsAsync(filter, CancellationToken.None)).Where(b => b.Count > 0).ToList();
        if (oldestFirst)
            buckets.Reverse();
        if (buckets.Count == 0)
            return null;

        async Task<List<TimelineAsset>> PhotosIn(TimeBucket bucket)
        {
            var photos = (await client.GetTimeBucketAsync(bucket.Key, filter, CancellationToken.None)).Where(a => a.IsImage).ToList();
            if (oldestFirst)
                photos.Reverse();
            return photos;
        }

        // Where it got to: the month and the photo shown last (if it's still there).
        var parts = settings.WallpaperPosition?.Split('|');
        var start = 0;
        string? lastId = null;
        if (parts is { Length: 3 } && parts[0] == source && buckets.FindIndex(b => b.Key == parts[1]) is >= 0 and var index)
            (start, lastId) = (index, parts[2]);

        // Go round once at most: the rest of that month, the months after it, then from the start again.
        for (var step = 0; step <= buckets.Count; step++)
        {
            var bucket = buckets[(start + step) % buckets.Count];
            var photos = await PhotosIn(bucket);
            var from = 0;
            if (step == 0 && lastId is not null)
                from = photos.FindIndex(a => a.Id == lastId) + 1; // 0 when it's gone: the month from its start
            if (from < photos.Count)
            {
                var next = photos[from];
                AppServices.Settings.Update(s => s.WallpaperPosition = $"{source}|{bucket.Key}|{next.Id}");
                return next;
            }
        }
        return null;
    }

    /// <summary>A random photo, landscape if possible, not one used recently.</summary>
    async Task<TimelineAsset?> PickAsync(ImmichClient client, TimelineFilter filter)
    {
        var buckets = (await client.GetTimeBucketsAsync(filter, CancellationToken.None)).Where(b => b.Count > 0).ToList();
        var total = buckets.Sum(b => b.Count);
        if (total == 0)
            return null;

        TimelineAsset? fallback = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            // Every photo equally likely: pick a position, then find its month.
            var position = _random.Next(total);
            var bucket = buckets.First(b => (position -= b.Count) < 0);
            var photos = (await client.GetTimeBucketAsync(bucket.Key, filter, CancellationToken.None))
                .Where(a => a.IsImage && !_recent.Contains(a.Id)).ToList();
            if (photos.Count == 0)
                continue;
            var landscape = photos.Where(a => a.Ratio >= 1.2).ToList();
            if (landscape.Count > 0)
                return landscape[_random.Next(landscape.Count)];
            fallback ??= photos[_random.Next(photos.Count)];
        }
        return fallback ?? await PickLeastRecentAsync(client, filter, buckets);
    }

    /// <summary>
    /// Every photo picked was shown lately (a small album, or the end of a round): go through the photos and take one
    /// not shown lately, or else the one shown longest ago, so the frame keeps going round instead of stopping.
    /// The one on screen now only comes again if it's the only photo.
    /// </summary>
    async Task<TimelineAsset?> PickLeastRecentAsync(ImmichClient client, TimelineFilter filter, List<TimeBucket> buckets)
    {
        var recent = _recent.ToList(); // oldest first
        var current = recent.Count > 0 ? recent[^1] : null;
        TimelineAsset? best = null, only = null;
        var bestAge = int.MaxValue;
        // A big library always has unshown photos within a few months; a small album is read whole.
        foreach (var bucket in buckets.OrderBy(_ => _random.Next()).Take(24))
        {
            foreach (var asset in (await client.GetTimeBucketAsync(bucket.Key, filter, CancellationToken.None)).Where(a => a.IsImage))
            {
                only ??= asset;
                var age = recent.IndexOf(asset.Id);
                if (age < 0)
                    return asset; // not shown lately after all
                if (asset.Id != current && age < bestAge)
                    (best, bestAge) = (asset, age);
            }
        }
        return best ?? only;
    }

    void Remember(string id)
    {
        _recent.Enqueue(id);
        while (_recent.Count > RememberRecent)
            _recent.Dequeue();
    }

    public void Dispose() => _timer?.Dispose();
}
