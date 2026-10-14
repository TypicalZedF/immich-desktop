using System.Diagnostics;
using ImmichDesktop.Core.Settings;

namespace ImmichDesktop.Core.Sync;

public sealed partial class SyncEngine
{
    // ---- Status -----------------------------------------------------------------------------------

    static List<string> EnabledFolderIds(AppSettings settings) =>
        settings.Folders.Where(f => f.Enabled).Select(f => f.Id).ToList();

    void RefreshCounts(AppSettings settings)
    {
        try
        {
            _counts = _db.GetCounts(EnabledFolderIds(settings));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read counts", ex);
        }
    }

    void SetState(SyncState state, string? detail = null)
    {
        _state = state;
        _detail = detail;
        Publish();
    }

    void Publish()
    {
        SyncStatus snapshot;
        lock (_publishGate)
        {
            var now = Stopwatch.GetTimestamp();
            var bytes = Interlocked.Read(ref _bytesSent);
            if (_state == SyncState.Uploading)
            {
                var elapsed = Stopwatch.GetElapsedTime(_speedSampleTimestamp, now).TotalSeconds;
                if (_speedSampleTimestamp == 0)
                {
                    _speedSampleTimestamp = now;
                    _speedSampleBytes = bytes;
                }
                else if (elapsed >= 0.5)
                {
                    var instant = (bytes - _speedSampleBytes) / elapsed;
                    _speed = _speed == 0 ? instant : _speed * 0.7 + instant * 0.3;
                    _speedSampleTimestamp = now;
                    _speedSampleBytes = bytes;
                }
            }
            else
            {
                _speed = 0;
                _speedSampleTimestamp = 0;
            }

            snapshot = new SyncStatus
            {
                State = _state,
                Detail = _detail,
                Counts = _counts,
                RunTotal = _runTotal,
                RunDone = Math.Min(Volatile.Read(ref _runDone), _runTotal),
                Active = _active.Values
                    .Select(t => new ActiveUpload(t.Path, t.Size, t.Sent))
                    .OrderBy(a => a.Path, StringComparer.Ordinal)
                    .ToList(),
                BytesPerSecond = _speed,
                LastSync = _lastSync,
                Remaining = _state == SyncState.Uploading ? EstimateRemaining(bytes) : null,
            };
            _status = snapshot;
        }

        try
        {
            StatusChanged?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            Log.Error("Status listener failed", ex);
        }
    }
}
