using System.Diagnostics;

namespace ImmichSplash.Core.Api;

/// <summary>Shared upload rate limit across all concurrent uploads. A rate of 0 means unlimited.</summary>
public sealed class BandwidthLimiter
{
    readonly Lock _gate = new();
    double _bytesPerSecond;
    double _nextFreeSeconds;

    public BandwidthLimiter(long bytesPerSecond = 0) => SetRate(bytesPerSecond);

    public void SetRate(long bytesPerSecond)
    {
        lock (_gate)
            _bytesPerSecond = Math.Max(0, bytesPerSecond);
    }

    public async ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        double delaySeconds;
        lock (_gate)
        {
            if (_bytesPerSecond <= 0)
                return;

            var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            var start = Math.Max(now, _nextFreeSeconds);
            _nextFreeSeconds = start + bytes / _bytesPerSecond;
            delaySeconds = start - now;
        }

        if (delaySeconds > 0.001)
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
    }
}
