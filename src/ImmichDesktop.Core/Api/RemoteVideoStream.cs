namespace ImmichDesktop.Core.Api;

/// <summary>
/// A video on the server as a seekable stream, so the player can start right away and jump around:
/// it's fetched in pieces as the player asks for them (with the account's sign-in, which a player
/// can't add to a web address itself), keeping recent pieces and fetching the next one ahead.
/// </summary>
public sealed class RemoteVideoStream : Stream
{
    public const int ChunkSize = 1024 * 1024;
    const int CachedChunks = 24;

    readonly Func<long, int, CancellationToken, Task<VideoChunk>> _fetch;
    readonly CancellationTokenSource _cts = new();
    readonly Lock _gate = new();
    readonly Dictionary<long, Task<byte[]>> _chunks = [];
    readonly LinkedList<long> _recent = new();
    byte[]? _whole; // the server sent everything at once (no range support)
    long _length = -1;
    long _position;

    public RemoteVideoStream(Func<long, int, CancellationToken, Task<VideoChunk>> fetch) => _fetch = fetch;

    public RemoteVideoStream(ImmichClient client, string assetId)
        : this((offset, count, ct) => client.GetVideoChunkAsync(assetId, offset, count, ct))
    {
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            if (_length < 0)
                Chunk(0); // the first piece tells the size
            return _length;
        }
    }

    public override long Position
    {
        get => _position;
        set => _position = Math.Max(0, value);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count == 0)
            return 0;
        var length = Length;
        if (_position >= length)
            return 0;
        var index = _position / ChunkSize;
        var chunk = _whole ?? Chunk(index);
        var start = _whole is null ? (int)(_position - index * ChunkSize) : (int)_position;
        var n = (int)Math.Min(Math.Min(count, chunk.Length - start), length - _position);
        if (n <= 0)
            return 0;
        Buffer.BlockCopy(chunk, start, buffer, offset, n);
        _position += n;
        if (_whole is null && (index + 1) * ChunkSize < length)
            Prefetch(index + 1);
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => Length + offset,
        };
        return _position;
    }

    byte[] Chunk(long index)
    {
        Task<byte[]> task;
        lock (_gate)
            task = Start(index);
        try
        {
            return task.GetAwaiter().GetResult(); // called on the player's own reading thread
        }
        catch
        {
            lock (_gate)
                _chunks.Remove(index); // let a later read try again
            throw;
        }
    }

    void Prefetch(long index)
    {
        lock (_gate)
            _ = Start(index);
    }

    Task<byte[]> Start(long index)
    {
        if (_chunks.TryGetValue(index, out var existing))
        {
            _recent.Remove(index);
            _recent.AddFirst(index);
            return existing;
        }
        var task = FetchAsync(index);
        _chunks[index] = task;
        _recent.AddFirst(index);
        while (_recent.Count > CachedChunks && _recent.Last is { } oldest)
        {
            _recent.RemoveLast();
            _chunks.Remove(oldest.Value);
        }
        return task;
    }

    async Task<byte[]> FetchAsync(long index)
    {
        var chunk = await _fetch(index * ChunkSize, ChunkSize, _cts.Token).ConfigureAwait(false);
        if (!chunk.Partial)
        {
            // No range support: the server sent the whole video.
            _length = chunk.Data.Length;
            return _whole = chunk.Data;
        }
        if (chunk.Total is { } size)
            _length = size;
        else if (_length < 0)
            _length = chunk.Data.Length < ChunkSize ? index * ChunkSize + chunk.Data.Length : long.MaxValue;
        if (chunk.Offset != index * ChunkSize)
            throw new IOException("The server sent a different part of the video than asked.");
        return chunk.Data;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            lock (_gate)
            {
                _chunks.Clear();
                _recent.Clear();
            }
        }
        base.Dispose(disposing);
    }
}
