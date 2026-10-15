using Avalonia.Media.Imaging;
using ImmichSplash.Core.Api;

namespace ImmichSplash.App.Services;

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

    // Decoding is CPU- and memory-hungry: a fast scroll must queue up, not decode a hundred pictures at once.
    readonly SemaphoreSlim _decodes = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 4));
    readonly Lock _gate = new();
    readonly Dictionary<string, LinkedListNode<(string Key, byte[] Data)>> _index = [];
    readonly LinkedList<(string Key, byte[] Data)> _lru = new();
    long _cachedBytes;

    /// <summary>A grid thumbnail, decoded at <paramref name="decodeWidth"/> pixels wide.</summary>
    public async Task<Bitmap?> LoadAsync(string assetId, AssetImageSize size, int decodeWidth, CancellationToken ct)
    {
        var data = await FetchAsync($"{assetId}:{size}", t => client.GetAssetImageAsync(assetId, size, t), cache: size == AssetImageSize.Thumbnail, ct);
        await _decodes.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var stream = new MemoryStream(data);
                return Bitmap.DecodeToWidth(stream, Math.Max(32, decodeWidth), BitmapInterpolationMode.MediumQuality);
            }, ct);
        }
        finally
        {
            _decodes.Release();
        }
    }

    // Decoded grid thumbnails, shared by every cell showing that photo. Each cell that shows one holds
    // a lease on it; an evicted picture is freed as soon as the last lease is returned (not left to the
    // garbage collector, which can't see the native memory behind a picture and lets it pile up when
    // scrolling fast).
    const long DecodedBudgetBytes = 160L * 1024 * 1024;

    sealed class Decoded(string key, Bitmap bitmap, long bytes)
    {
        public string Key { get; } = key;
        public Bitmap Bitmap { get; } = bitmap;
        public long Bytes { get; } = bytes;
        public int Leases;
        public bool Evicted;
    }

    readonly Dictionary<string, LinkedListNode<Decoded>> _decoded = [];
    readonly LinkedList<Decoded> _decodedOrder = new();
    readonly Dictionary<Bitmap, Decoded> _leased = new(ReferenceEqualityComparer.Instance);
    long _decodedBytes;

    /// <summary>
    /// A grid thumbnail, decoded once and shared. The caller holds a lease on it until it hands it
    /// back with <see cref="ReturnGridThumbnail"/>; don't dispose it yourself.
    /// </summary>
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
                return Lease(node.Value);
            }
        }
        var bitmap = await LoadAsync(assetId, AssetImageSize.Thumbnail, width, ct);
        if (bitmap is null)
            return null;
        lock (_gate)
        {
            if (_decoded.TryGetValue(key, out var raced))
            {
                bitmap.Dispose(); // someone else decoded the same picture meanwhile
                return Lease(raced.Value);
            }
            var entry = new Decoded(key, bitmap, (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4);
            _decoded[key] = _decodedOrder.AddFirst(entry);
            _decodedBytes += entry.Bytes;
            var result = Lease(entry);
            EvictDecoded(DecodedBudgetBytes);
            return result;
        }
    }

    Bitmap Lease(Decoded entry)
    {
        entry.Leases++;
        _leased[entry.Bitmap] = entry;
        return entry.Bitmap;
    }

    /// <summary>Hands back a picture from <see cref="LoadGridThumbnailAsync"/> once nothing shows it.</summary>
    public void ReturnGridThumbnail(Bitmap bitmap)
    {
        lock (_gate)
        {
            if (!_leased.TryGetValue(bitmap, out var entry))
                return;
            if (--entry.Leases > 0)
                return;
            _leased.Remove(bitmap);
            if (entry.Evicted)
                entry.Bitmap.Dispose();
        }
    }

    /// <summary>Drops cached pictures (oldest first) until at most <paramref name="budget"/> bytes remain. Call with the lock held.</summary>
    void EvictDecoded(long budget)
    {
        while (_decodedBytes > budget && _decodedOrder.Last is { } oldest)
        {
            _decodedOrder.RemoveLast();
            _decoded.Remove(oldest.Value.Key);
            _decodedBytes -= oldest.Value.Bytes;
            oldest.Value.Evicted = true;
            if (oldest.Value.Leases == 0)
                oldest.Value.Bitmap.Dispose();
        }
    }

    /// <summary>A photo was edited: whatever is cached for it is out of date.</summary>
    public void Forget(string assetId)
    {
        lock (_gate)
        {
            foreach (var key in _index.Keys.Where(k => k.StartsWith(assetId + ":", StringComparison.Ordinal)).ToList())
            {
                _cachedBytes -= _index[key].Value.Data.Length;
                _lru.Remove(_index[key]);
                _index.Remove(key);
            }
            foreach (var key in _decoded.Keys.Where(k => k.StartsWith(assetId + ":", StringComparison.Ordinal)).ToList())
            {
                var node = _decoded[key];
                _decodedOrder.Remove(node);
                _decoded.Remove(key);
                _decodedBytes -= node.Value.Bytes;
                node.Value.Evicted = true;
                if (node.Value.Leases == 0)
                    node.Value.Bitmap.Dispose();
            }
        }
    }

    /// <summary>Frees the cached pictures nobody is showing (memory is running high).</summary>
    public void TrimMemory()
    {
        lock (_gate)
        {
            EvictDecoded(0);
            _index.Clear();
            _lru.Clear();
            _cachedBytes = 0;
        }
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
            EvictDecoded(0);
            _index.Clear();
            _lru.Clear();
            _cachedBytes = 0;
        }
    }
}
