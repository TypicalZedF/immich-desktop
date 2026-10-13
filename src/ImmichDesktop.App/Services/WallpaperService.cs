using ImmichDesktop.Core;
using ImmichDesktop.Core.Api;
using ImmichDesktop.Core.Platform;
using ImmichDesktop.Core.Settings;

namespace ImmichDesktop.App.Services;

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
        IsChanging ? "Changing your background…"
        : Error is { } error ? error
        : LastChanged is { } t ? $"Changed {Format.Relative(t)}"
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
            var asset = await PickAsync(client, filter);
            if (asset is null)
            {
                Error = settings.WallpaperSource switch
                {
                    WallpaperSource.Album => "That album has no photos to use.",
                    WallpaperSource.Favorites => "You have no favorite photos yet. Tap the heart on a photo, or pick an album.",
                    _ => "Your library has no photos yet.",
                };
                return;
            }

            byte[] data;
            try
            {
                // Data saving: the smaller preview on a metered connection.
                var size = AppServices.Network.IsMetered ? AssetImageSize.Preview : AssetImageSize.Fullsize;
                data = await client.GetAssetImageAsync(asset.Id, size, CancellationToken.None);
            }
            catch (ImmichApiException)
            {
                data = await client.GetAssetImageAsync(asset.Id, AssetImageSize.Preview, CancellationToken.None);
            }

            var folder = Directory.CreateDirectory(Path.Combine(AppPaths.DataDir, "wallpaper")).FullName;
            // Alternate file names: Windows caches a background by path and may not notice a changed file.
            var path = Path.Combine(folder, $"background-{_counter++ % 2}.jpg");
            await Task.Run(() =>
            {
                using var bitmap = Images.DecodeUpright(data, MaxSize);
                File.WriteAllBytes(path, Images.ToJpeg(bitmap, 92));
                DesktopWallpaper.Set(path);
            });
            Remember(asset.Id);
            LastChanged = DateTimeOffset.Now;
            Log.Info("Desktop background changed");
        }
        catch (Exception ex)
        {
            Log.Warn("Could not change the desktop background", ex);
            Error = $"Couldn't change your background. {NetworkErrors.Describe(ex)}";
        }
        finally
        {
            IsChanging = false;
            _gate.Release();
            Changed?.Invoke();
        }
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
        return fallback;
    }

    void Remember(string id)
    {
        _recent.Enqueue(id);
        while (_recent.Count > RememberRecent)
            _recent.Dequeue();
    }

    public void Dispose() => _timer?.Dispose();
}
