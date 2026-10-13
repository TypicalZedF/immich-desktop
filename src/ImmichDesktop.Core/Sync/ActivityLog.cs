using ImmichDesktop.Core.Storage;

namespace ImmichDesktop.Core.Sync;

public enum ActivityKind
{
    Info = 0,
    Uploaded = 1,
    Warning = 2,
    Error = 3,
    Success = 4,
}

public sealed record ActivityEntry(long Id, DateTimeOffset Time, ActivityKind Kind, string Message, string? Path);

/// <summary>User-facing history of what the app did. Persisted so it survives restarts.</summary>
public sealed class ActivityLog(StateDatabase db)
{
    public event Action<ActivityEntry>? Added;
    public event Action? Cleared;

    public IReadOnlyList<ActivityEntry> Recent(int limit = 300) => db.GetActivity(limit);

    public void Add(ActivityKind kind, string message, string? path = null)
    {
        try
        {
            var time = DateTimeOffset.Now;
            var id = db.AddActivity(time, kind, message, path);
            Added?.Invoke(new ActivityEntry(id, time, kind, message, path));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not record activity", ex);
        }
    }

    public void Clear()
    {
        db.ClearActivity();
        Cleared?.Invoke();
    }
}
