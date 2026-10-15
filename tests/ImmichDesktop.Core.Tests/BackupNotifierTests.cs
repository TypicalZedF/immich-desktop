using ImmichDesktop.Core.Sync;

namespace ImmichDesktop.Core.Tests;

public class BackupNotifierTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    static SyncStatus S(SyncState state, int uploaded = 0, int failed = 0) =>
        new() { State = state, Counts = new StatusCounts(0, uploaded, 0, failed, 0) };

    [Fact]
    public void A_big_run_is_announced_once_it_ends()
    {
        var n = new BackupNotifier();
        Assert.Null(n.OnStatus(S(SyncState.Idle, 100), T0));
        Assert.Null(n.OnStatus(S(SyncState.Scanning, 100), T0));
        Assert.Null(n.OnStatus(S(SyncState.Uploading, 110), T0.AddSeconds(5)));
        var notice = n.OnStatus(S(SyncState.Idle, 130), T0.AddSeconds(30));
        Assert.Equal(NoticeKind.BackedUp, notice?.Kind);
        Assert.Contains("30", notice!.Body);
    }

    [Fact]
    public void A_screenshot_or_two_stays_quiet()
    {
        var n = new BackupNotifier();
        n.OnStatus(S(SyncState.Uploading, 10), T0);
        Assert.Null(n.OnStatus(S(SyncState.Idle, 12), T0.AddSeconds(5)));
    }

    [Fact]
    public void A_long_run_is_announced_even_with_few_files()
    {
        var n = new BackupNotifier();
        n.OnStatus(S(SyncState.Uploading, 0), T0);
        Assert.Equal(NoticeKind.BackedUp, n.OnStatus(S(SyncState.Idle, 3), T0.AddMinutes(3))?.Kind);
    }

    [Fact]
    public void New_failures_win_over_success()
    {
        var n = new BackupNotifier();
        n.OnStatus(S(SyncState.Uploading, 0, failed: 1), T0);
        var notice = n.OnStatus(S(SyncState.Idle, 50, failed: 3), T0.AddMinutes(1));
        Assert.Equal(NoticeKind.Failed, notice?.Kind);
        Assert.Contains("2 files", notice!.Title);
    }

    [Fact]
    public void Sign_in_is_announced_once()
    {
        var n = new BackupNotifier();
        Assert.Equal(NoticeKind.SignInNeeded, n.OnStatus(S(SyncState.AuthError), T0)?.Kind);
        Assert.Null(n.OnStatus(S(SyncState.AuthError), T0.AddMinutes(1)));
    }

    [Fact]
    public void Pausing_mid_run_still_reports_what_was_done()
    {
        var n = new BackupNotifier();
        n.OnStatus(S(SyncState.Uploading, 0), T0);
        Assert.Equal(NoticeKind.BackedUp, n.OnStatus(S(SyncState.Paused, 25), T0.AddSeconds(40))?.Kind);
    }
}
