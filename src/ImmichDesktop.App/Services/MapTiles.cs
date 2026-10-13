using Avalonia.Media.Imaging;
using ImmichDesktop.Core;

namespace ImmichDesktop.App.Services;

/// <summary>
/// Map background tiles (OpenStreetMap data, CARTO's light and dark styles to match the app), fetched
/// a few at a time and kept in a small in-memory cache.
/// </summary>
public sealed class MapTiles : IDisposable
{
    const int MaxCachedTiles = 400;

    /// <summary>Lets the screenshot tool serve tiles without the internet.</summary>
    public static HttpMessageHandler? Handler { get; set; }

    public const string Attribution = "© OpenStreetMap contributors © CARTO";

    readonly HttpClient _http;
    readonly SemaphoreSlim _downloads = new(6, 6);
    readonly Dictionary<string, LinkedListNode<(string Key, Bitmap Bitmap)>> _index = [];
    readonly LinkedList<(string Key, Bitmap Bitmap)> _lru = new();
    readonly HashSet<string> _pending = [];
    readonly HashSet<string> _failed = [];

    public MapTiles()
    {
        _http = Handler is null ? new HttpClient() : new HttpClient(Handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
    }

    /// <summary>Raised on the UI thread when a requested tile arrives.</summary>
    public event Action? TileLoaded;

    /// <summary>The tile if it's ready; otherwise starts fetching it and returns null.</summary>
    public Bitmap? Get(int z, int x, int y, bool dark)
    {
        var key = $"{(dark ? 'd' : 'l')}/{z}/{x}/{y}";
        if (_index.TryGetValue(key, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Bitmap;
        }
        if (_pending.Add(key) && !_failed.Contains(key))
            _ = FetchAsync(key, z, x, y, dark);
        return null;
    }

    async Task FetchAsync(string key, int z, int x, int y, bool dark)
    {
        try
        {
            await _downloads.WaitAsync();
            Bitmap bitmap;
            try
            {
                var subdomain = "abcd"[(x + y) % 4];
                var style = dark ? "dark_all" : "light_all";
                var data = await _http.GetByteArrayAsync($"https://{subdomain}.basemaps.cartocdn.com/{style}/{z}/{x}/{y}@2x.png");
                bitmap = await Task.Run(() => new Bitmap(new MemoryStream(data)));
            }
            finally
            {
                _downloads.Release();
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _pending.Remove(key);
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
            });
            Log.Info($"Map tile {key} failed: {ex.Message}");
        }
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
