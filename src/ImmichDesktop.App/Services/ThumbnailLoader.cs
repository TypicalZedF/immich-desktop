using Avalonia.Media.Imaging;
using ImmichDesktop.Core.Api;

namespace ImmichDesktop.App.Services;

/// <summary>
/// Fetches asset images from the server for the Photos page. Keeps a bounded in-memory cache of the
/// compressed thumbnails (cheap to re-decode), limits parallel downloads, and decodes off the UI thread.
/// Lives as long as the Photos page, so closing the window frees everything.
/// </summary>
public sealed class ThumbnailLoader(ImmichClient client) : IDisposable
{
    const long CacheBudgetBytes = 48L * 1024 * 1024;

    readonly SemaphoreSlim _downloads = new(8, 8);
    readonly Lock _gate = new();
    readonly Dictionary<string, LinkedListNode<(string Key, byte[] Data)>> _index = [];
    readonly LinkedList<(string Key, byte[] Data)> _lru = new();
    long _cachedBytes;

    public async Task<Bitmap?> LoadAsync(string assetId, AssetImageSize size, int decodeWidth, CancellationToken ct)
    {
        var key = $"{assetId}:{size}";
        var data = FromCache(key);
        if (data is null)
        {
            await _downloads.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                data = await client.GetAssetImageAsync(assetId, size, ct);
            }
            finally
            {
                _downloads.Release();
            }
            if (size == AssetImageSize.Thumbnail)
                AddToCache(key, data);
        }

        return await Task.Run(() =>
        {
            using var stream = new MemoryStream(data);
            return Bitmap.DecodeToWidth(stream, Math.Max(32, decodeWidth), BitmapInterpolationMode.MediumQuality);
        }, ct);
    }

    byte[]? FromCache(string key)
    {
        lock (_gate)
        {
            if (!_index.TryGetValue(key, out var node))
                return null;
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Data;
        }
    }

    void AddToCache(string key, byte[] data)
    {
        lock (_gate)
        {
            if (_index.ContainsKey(key))
                return;
            _index[key] = _lru.AddFirst((key, data));
            _cachedBytes += data.Length;
            while (_cachedBytes > CacheBudgetBytes && _lru.Last is { } oldest)
            {
                _lru.RemoveLast();
                _index.Remove(oldest.Value.Key);
                _cachedBytes -= oldest.Value.Data.Length;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _index.Clear();
            _lru.Clear();
            _cachedBytes = 0;
        }
    }
}
