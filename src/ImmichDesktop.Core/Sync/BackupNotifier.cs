namespace ImmichDesktop.Core.Sync;

public enum NoticeKind
{
    /// <summary>A sizeable backup run finished.</summary>
    BackedUp,
    /// <summary>Files failed in the run that just ended.</summary>
    Failed,
    /// <summary>The server signed the app out.</summary>
    SignInNeeded,
}

/// <summary>Something worth a desktop notification. <see cref="Kind"/> says where a click should lead.</summary>
public sealed record Notice(NoticeKind Kind, string Title, string Body);

/// <summary>
/// Decides when the backup is worth a desktop notification, from the engine's status updates: a big
/// backup finishing, files failing, or needing to sign in again. Small everyday runs (a screenshot
/// or two) stay quiet.
/// </summary>
public sealed class BackupNotifier
{
    /// <summary>A run counts as big from this many uploaded files, or from <see cref="LongRun"/>.</summary>
    public const int BigRunFiles = 20;
    public static readonly TimeSpan LongRun = TimeSpan.FromMinutes(2);

    SyncState _state = SyncState.NotConfigured;
    StatusCounts _atStart;
    DateTimeOffset _startedAt;
    bool _running;

    /// <summary>Feed every status update in order; returns a notice when one is due.</summary>
    public Notice? OnStatus(SyncStatus status, DateTimeOffset now)
    {
        var previous = _state;
        _state = status.State;

        if (status.State == SyncState.AuthError && previous != SyncState.AuthError)
            return new Notice(NoticeKind.SignInNeeded, Loc.T("Sign in to Immich again"),
                Loc.T("Your sign-in has expired, so backup has stopped. Open Immich Desktop to sign in."));

        if (status.IsBusy && !_running)
        {
            _running = true;
            _atStart = status.Counts;
            _startedAt = now;
            return null;
        }
        if (status.IsBusy || !_running)
            return null;

        // The run just ended.
        _running = false;
        var uploaded = status.Counts.Uploaded - _atStart.Uploaded;
        var failed = status.Counts.Failed - _atStart.Failed;
        if (failed > 0)
            return new Notice(NoticeKind.Failed, Loc.T("{0} couldn't be backed up", Format.Plural(failed, "file")),
                Loc.T("They'll be tried again automatically. Open Activity to see why."));
        if (uploaded > 0 && (uploaded >= BigRunFiles || now - _startedAt >= LongRun))
            return new Notice(NoticeKind.BackedUp, Loc.T("Backup complete"),
                Loc.T("Backed up {0} to Immich.", Format.Plural(uploaded, "new photo or video", "new photos and videos")));
        return null;
    }
}
