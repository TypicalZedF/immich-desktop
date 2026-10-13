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

    /// <summary>Bigger local files (huge panoramas, RAW-sized TIFFs) get a placeholder instead.</summary>
    const long MaxLocalDecodeBytes = 80L * 1024 * 1024;

    readonly SemaphoreSlim _downloads = new(8, 8);
    readonly SemaphoreSlim _local = new(3, 3);
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

    // Decoded grid thumbnails, shared by every cell showing that photo. Eviction just drops the
    // reference (a cell may still be drawing it); the memory is freed once nothing uses it.
    const long DecodedBudgetBytes = 192L * 1024 * 1024;
    readonly Dictionary<string, LinkedListNode<(string Key, Bitmap Bitmap, long Bytes)>> _decoded = [];
    readonly LinkedList<(string Key, Bitmap Bitmap, long Bytes)> _decodedOrder = new();
    long _decodedBytes;

    /// <summary>A grid thumbnail, decoded once and reused (don't dispose it: the cache owns it).</summary>
    public async Task<Bitmap?> LoadGridThumbnailAsync(string assetId, int decodeWidth, CancellationToken ct)
    {
        // Round the width so small layout changes reuse what's decoded.
        var width = Math.Max(64, (decodeWidth + 31) / 32 * 32);
        var key = $"{assetId}:{width}";
        lock (_gate)
        {
            if (_decoded.TryGetValue(key, out var node))
            {
                _decodedOrder.Remove(node);
                _decodedOrder.AddFirst(node);
                return node.Value.Bitmap;
            }
        }
        var bitmap = await LoadAsync(assetId, AssetImageSize.Thumbnail, width, ct);
        if (bitmap is null)
            return null;
        lock (_gate)
        {
            if (_decoded.TryGetValue(key, out var raced))
                return raced.Value.Bitmap;
            var bytes = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;
            _decoded[key] = _decodedOrder.AddFirst((key, bitmap, bytes));
            _decodedBytes += bytes;
            while (_decodedBytes > DecodedBudgetBytes && _decodedOrder.Last is { } oldest)
            {
                _decodedOrder.RemoveLast();
                _decoded.Remove(oldest.Value.Key);
                _decodedBytes -= oldest.Value.Bytes;
            }
        }
        return bitmap;
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

    /// <summary>
    /// A thumbnail for a file on this PC that isn't in Immich yet, the right way up. Null for videos and
    /// formats we can't decode here (e.g. HEIC); those show a placeholder.
    /// </summary>
    public async Task<Bitmap?> LoadLocalAsync(string path, bool isImage, int decodeWidth, CancellationToken ct)
    {
        if (!isImage)
            return null;
        await _local.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxLocalDecodeBytes)
                    return null;
                try
                {
                    using var bitmap = Images.DecodeUpright(File.ReadAllBytes(path), Math.Max(64, decodeWidth * 2)); // long side; tiles crop to a square
                    return Images.ToAvalonia(bitmap);
                }
                catch (InvalidDataException)
                {
                    return null;
                }
            }, ct);
        }
        finally
        {
            _local.Release();
        }
    }

    /// <summary>A file on this PC for the full-window viewer.</summary>
    public static Task<Bitmap> LoadLocalForViewerAsync(string path, int maxSize, CancellationToken ct) =>
        Task.Run(() => Images.DecodeForDisplay(File.ReadAllBytes(path), Math.Max(64, maxSize)), ct);

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
            _decoded.Clear();
            _decodedOrder.Clear();
            _decodedBytes = 0;
            _index.Clear();
            _lru.Clear();
            _cachedBytes = 0;
        }
    }
}
