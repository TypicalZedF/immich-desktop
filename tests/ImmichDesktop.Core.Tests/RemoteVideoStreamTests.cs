using ImmichDesktop.Core.Api;

namespace ImmichDesktop.Core.Tests;

/// <summary>Videos stream into the player in pieces, and seeking (e.g. to an MP4's index at the end) works.</summary>
public class RemoteVideoStreamTests
{
    static byte[] Video(int size)
    {
        var data = new byte[size];
        new Random(3).NextBytes(data);
        return data;
    }

    [Fact]
    public void Reads_and_seeks_match_the_file()
    {
        var video = Video(RemoteVideoStream.ChunkSize * 3 + 12_345);
        var requests = new List<long>();
        using var stream = new RemoteVideoStream((offset, count, _) =>
        {
            lock (requests)
                requests.Add(offset);
            var n = (int)Math.Min(count, video.Length - offset);
            return Task.FromResult(new VideoChunk(video.AsSpan((int)offset, n).ToArray(), video.Length, offset));
        });

        Assert.Equal(video.Length, stream.Length);
        var random = new Random(5);
        var buffer = new byte[300_000];
        for (var i = 0; i < 200; i++)
        {
            var at = random.NextInt64(video.Length);
            stream.Seek(at, SeekOrigin.Begin);
            var read = stream.Read(buffer, 0, random.Next(1, buffer.Length));
            Assert.True(read > 0);
            Assert.True(buffer.AsSpan(0, read).SequenceEqual(video.AsSpan((int)at, read)));
        }
        stream.Seek(0, SeekOrigin.End);
        Assert.Equal(0, stream.Read(buffer, 0, 10));
        Assert.All(requests, offset => Assert.Equal(0, offset % RemoteVideoStream.ChunkSize)); // whole pieces only
    }

    [Fact]
    public void Servers_without_range_support_still_play()
    {
        var video = Video(2_500_000);
        var calls = 0;
        using var stream = new RemoteVideoStream((_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new VideoChunk(video, video.Length, 0, Partial: false));
        });

        var copy = new MemoryStream();
        stream.CopyTo(copy);

        Assert.Equal(video, copy.ToArray());
        Assert.Equal(1, calls);
    }

    [Fact]
    public void A_video_the_server_wont_send_gives_up_instead_of_asking_forever()
    {
        // The player keeps reading after errors; the server must not be hammered (it was: 170,000 requests in seconds).
        var calls = 0;
        var failed = 0;
        using var stream = new RemoteVideoStream((_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromException<VideoChunk>(new ImmichApiException(System.Net.HttpStatusCode.NotFound, "Not found"));
        });
        stream.Failed += () => failed++;

        var buffer = new byte[1000];
        for (var i = 0; i < 1000; i++)
            Assert.ThrowsAny<Exception>(() => stream.Read(buffer, 0, buffer.Length));

        Assert.InRange(calls, 1, 3);
        Assert.Equal(1, failed);
    }
}
