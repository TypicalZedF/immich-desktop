using Avalonia.Media.Imaging;
using ImmichDesktop.Core;

namespace ImmichDesktop.App.Services;

/// <summary>An online street map service: where its tiles come from and how it must be credited.</summary>
public sealed record TileProvider(string Name, string LightUrl, string DarkUrl, int TilePixels, string Attribution);

/// <summary>
/// Optional street detail for the map (drawn over the built-in offline map), fetched a few at a time
/// and kept in a small in-memory cache. No account or key is needed. If a service keeps failing,
/// the next one is used.
/// </summary>
public sealed class MapTiles : IDisposable
{
    const int MaxCachedTiles = 400;
    const int FailuresBeforeSwitching = 6;

    /// <summary>Lets the screenshot tool serve tiles without the internet.</summary>
    public static HttpMessageHandler? Handler { get; set; }

    public static IReadOnlyList<TileProvider> Providers { get; } =
    [
        new("CARTO",
            "https://{s}.basemaps.cartocdn.com/light_all/{z}/{x}/{y}@2x.png",
            "https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}@2x.png",
            512, "© OpenStreetMap contributors © CARTO"),
        new("Esri",
            "https://server.arcgisonline.com/ArcGIS/rest/services/Canvas/World_Light_Gray_Base/MapServer/tile/{z}/{y}/{x}",
            "https://server.arcgisonline.com/ArcGIS/rest/services/Canvas/World_Dark_Gray_Base/MapServer/tile/{z}/{y}/{x}",
            256, "© Esri, HERE, Garmin, © OpenStreetMap contributors"),
    ];

    readonly HttpClient _http;
    readonly SemaphoreSlim _downloads = new(6, 6);
    readonly Dictionary<string, LinkedListNode<(string Key, Bitmap Bitmap)>> _index = [];
    readonly LinkedList<(string Key, Bitmap Bitmap)> _lru = new();
    readonly HashSet<string> _pending = [];
    readonly HashSet<string> _failed = [];
    int _provider;
    int _failures;

    public MapTiles()
    {
        _http = Handler is null ? new HttpClient() : new HttpClient(Handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
    }

    /// <summary>The service in use (it changes if one keeps failing).</summary>
    public TileProvider Provider => Providers[_provider];

    /// <summary>True once every service has failed repeatedly (then only the offline map shows).</summary>
    public bool AllFailed { get; private set; }

    /// <summary>Raised on the UI thread when a requested tile arrives.</summary>
    public event Action? TileLoaded;

    /// <summary>The tile if it's ready; otherwise starts fetching it and returns null.</summary>
    public Bitmap? Get(int z, int x, int y, bool dark)
    {
        if (AllFailed)
            return null;
        var key = $"{_provider}/{(dark ? 'd' : 'l')}/{z}/{x}/{y}";
        if (_index.TryGetValue(key, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Bitmap;
        }
        if (_pending.Add(key) && !_failed.Contains(key))
            _ = FetchAsync(key, _provider, z, x, y, dark);
        return null;
    }

    async Task FetchAsync(string key, int provider, int z, int x, int y, bool dark)
    {
        try
        {
            await _downloads.WaitAsync();
            Bitmap bitmap;
            try
            {
                var p = Providers[provider];
                var url = (dark ? p.DarkUrl : p.LightUrl)
                    .Replace("{s}", "abcd"[(x + y) % 4].ToString())
                    .Replace("{z}", z.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Replace("{x}", x.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Replace("{y}", y.ToString(System.Globalization.CultureInfo.InvariantCulture));
                using var response = await _http.GetAsync(url);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentType?.MediaType is { } type && !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Got {type} instead of a map image");
                var data = await response.Content.ReadAsByteArrayAsync();
                bitmap = await Task.Run(() => new Bitmap(new MemoryStream(data)));
            }
            finally
            {
                _downloads.Release();
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _pending.Remove(key);
                _failures = 0;
                _index[key] = _lru.AddFirst((key, bitmap));
                while (_lru.Count > MaxCachedTiles && _lru.Last is { } oldest)
                {
                    _lru.RemoveLast();
                    _index.Remove(oldest.Value.Key);
                    oldest.Value.Bitmap.Dispose();
                }
                TileLoaded?.Invoke();
            });
        }
        catch (Exception ex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _pending.Remove(key);
                _failed.Add(key);
                if (provider == _provider && ++_failures >= FailuresBeforeSwitching)
                    SwitchProvider();
            });
            Log.Info($"Map tile {key} failed: {ex.Message}");
        }
    }

    void SwitchProvider()
    {
        _failures = 0;
        _failed.Clear();
        if (_provider + 1 < Providers.Count)
        {
            _provider++;
            Log.Info($"Map tiles: switching to {Provider.Name}");
        }
        else
        {
            AllFailed = true;
            Log.Warn("Map tiles: no street map service is reachable; showing the built-in map only");
        }
        TileLoaded?.Invoke();
    }

    public void Dispose()
    {
        foreach (var (_, bitmap) in _lru)
            bitmap.Dispose();
        _lru.Clear();
        _index.Clear();
        _http.Dispose();
    }
}
