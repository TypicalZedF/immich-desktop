using System.Buffers;
using System.Net;
using System.Net.Http.Headers;

namespace ImmichDesktop.Core.Api;

/// <summary>Streams a file from disk with progress reporting and bandwidth limiting; never buffers the whole file.</summary>
internal sealed class FileUploadContent : HttpContent
{
    const int ChunkSize = 64 * 1024;

    readonly string _path;
    readonly long _length;
    readonly BandwidthLimiter _limiter;
    readonly Action<long>? _progress;

    public FileUploadContent(string path, long length, BandwidthLimiter limiter, Action<long>? progress)
    {
        _path = path;
        _length = length;
        _limiter = limiter;
        _progress = progress;
        Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
    {
        await using var file = new FileStream(_path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            BufferSize = 0,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });

        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            long sent = 0;
            int read;
            while ((read = await file.ReadAsync(buffer.AsMemory(0, ChunkSize), ct)) > 0)
            {
                await _limiter.WaitAsync(read, ct);
                await stream.WriteAsync(buffer.AsMemory(0, read), ct);
                sent += read;
                _progress?.Invoke(sent);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _length;
        return true;
    }
}
