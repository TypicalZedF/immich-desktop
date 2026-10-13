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

    /// <summary>A grid thumbnail, decoded at <paramref name="decodeWidth"/> pixels wide.</summary>
    public async Task<Bitmap?> LoadAsync(string assetId, AssetImageSize size, int decodeWidth, CancellationToken ct)
    {
        var data = await FetchAsync($"{assetId}:{size}", t => client.GetAssetImageAsync(assetId, size, t), cache: size == AssetImageSize.Thumbnail, ct);
        return await Task.Run(() =>
        {
            using var stream = new MemoryStream(data);
            return Bitmap.DecodeToWidth(stream, Math.Max(32, decodeWidth), BitmapInterpolationMode.MediumQuality);
        }, ct);
    }

    /// <summary>
    /// An image for the full-window viewer: the right way up (full-size images can be straight from a camera)
    /// and decoded to fit <paramref name="maxSize"/> pixels with high-quality scaling, so it looks sharp.
    /// </summary>
    public async Task<Bitmap> LoadForViewerAsync(string assetId, AssetImageSize size, int maxSize, CancellationToken ct)
    {
        var data = await FetchAsync($"{assetId}:{size}", t => client.GetAssetImageAsync(assetId, size, t), cache: false, ct);
        return await Task.Run(() => Images.DecodeForDisplay(data, Math.Max(64, maxSize)), ct);
    }

    /// <summary>A person's face thumbnail.</summary>
    public async Task<Bitmap?> LoadPersonAsync(string personId, int decodeWidth, CancellationToken ct)
    {
        var data = await FetchAsync($"person:{personId}", t => client.GetPersonThumbnailAsync(personId, t), cache: true, ct);
        return await Task.Run(() =>
        {
            using var stream = new MemoryStream(data);
            return Bitmap.DecodeToWidth(stream, Math.Max(32, decodeWidth), BitmapInterpolationMode.HighQuality);
        }, ct);
    }

    async Task<byte[]> FetchAsync(string key, Func<CancellationToken, Task<byte[]>> fetch, bool cache, CancellationToken ct)
    {
        if (FromCache(key) is { } cached)
            return cached;
        byte[] data;
        await _downloads.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            data = await fetch(ct);
        }
        finally
        {
            _downloads.Release();
        }
        if (cache)
            AddToCache(key, data);
        return data;
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
