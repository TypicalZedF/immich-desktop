using ImmichDesktop.Core.Settings;
using ImmichDesktop.Core.Sync;

namespace ImmichDesktop.Core.Tests;

public class BackupNotifierTests
{
    static readonly AppSettings Defaults = new();
    static RunSummary Run(int uploaded, int already = 0, int failed = 0, double seconds = 10) =>
        new(uploaded, already, failed, TimeSpan.FromSeconds(seconds));

    [Fact]
    public void Big_backups_are_announced_by_default()
    {
        var notice = BackupNotifier.OnRunFinished(Run(30), Defaults);
        Assert.Equal(NoticeKind.BackedUp, notice?.Kind);
        Assert.Contains("30", notice!.Body);
    }

    [Fact]
    public void A_photo_or_two_stays_quiet_by_default_but_not_when_asked_for_every_backup()
    {
        Assert.Null(BackupNotifier.OnRunFinished(Run(2), Defaults));
        Assert.Equal(NoticeKind.BackedUp, BackupNotifier.OnRunFinished(Run(2), new AppSettings { NotifyBackups = BackupNotice.Every })?.Kind);
    }

    [Fact]
    public void Nothing_new_is_never_announced()
    {
        // Everything was already on the server (this used to say "Backup complete" after starting the app).
        foreach (var choice in Enum.GetValues<BackupNotice>())
            Assert.Null(BackupNotifier.OnRunFinished(Run(0, already: 2500, seconds: 600), new AppSettings { NotifyBackups = choice }));
    }

    [Fact]
    public void A_long_run_counts_as_big()
    {
        Assert.Equal(NoticeKind.BackedUp, BackupNotifier.OnRunFinished(Run(3, seconds: 180), Defaults)?.Kind);
    }

    [Fact]
    public void Failures_win_and_can_be_turned_off()
    {
        var notice = BackupNotifier.OnRunFinished(Run(50, failed: 2), Defaults);
        Assert.Equal(NoticeKind.Failed, notice?.Kind);
        Assert.Contains("2 files", notice!.Title);
        Assert.Equal(NoticeKind.BackedUp, BackupNotifier.OnRunFinished(Run(50, failed: 2), new AppSettings { NotifyFailures = false })?.Kind);
    }

    [Fact]
    public void Turning_notifications_off_silences_everything()
    {
        var off = new AppSettings { Notifications = false, NotifyBackups = BackupNotice.Every };
        Assert.Null(BackupNotifier.OnRunFinished(Run(100, failed: 5), off));
        Assert.Null(new BackupNotifier().OnStatus(new SyncStatus { State = SyncState.AuthError }, off));
    }

    [Fact]
    public void Sign_in_is_announced_once()
    {
        var n = new BackupNotifier();
        Assert.Equal(NoticeKind.SignInNeeded, n.OnStatus(new SyncStatus { State = SyncState.AuthError }, Defaults)?.Kind);
        Assert.Null(n.OnStatus(new SyncStatus { State = SyncState.AuthError }, Defaults));
        Assert.Null(new BackupNotifier().OnStatus(new SyncStatus { State = SyncState.AuthError }, new AppSettings { NotifySignIn = false }));
    }
}
