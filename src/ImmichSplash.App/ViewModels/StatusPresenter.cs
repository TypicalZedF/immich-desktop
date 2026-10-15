using ImmichSplash.Core;
using ImmichSplash.Core.Settings;
using ImmichSplash.Core.Sync;
using Material.Icons;

namespace ImmichSplash.App.ViewModels;

public enum StatusTone
{
    Ok,
    Busy,
    Paused,
    Warning,
    Error,
}

public sealed record StatusView(string Title, string Detail, string? TrayDetail, MaterialIconKind Icon, StatusTone Tone)
{
    public bool Spin => Icon == MaterialIconKind.Sync;
    public bool IsBusy => Tone == StatusTone.Busy;
    public bool IsPaused => Tone == StatusTone.Paused;
    public bool IsWarning => Tone == StatusTone.Warning;
    public bool IsError => Tone == StatusTone.Error;
}

/// <summary>Turns engine state into the words and icons shown in the window and the tray, so they always agree.</summary>
public static class StatusPresenter
{
    public static StatusView Describe(SyncStatus s, AppSettings settings)
    {
        var c = s.Counts;
        switch (s.State)
        {
            case SyncState.NotConfigured:
                return new(Tr.T("Not signed in"), Tr.T("Sign in to start backing up."), null, MaterialIconKind.AccountAlertOutline, StatusTone.Warning);

            case SyncState.Scanning:
                return new(Tr.T("Looking for new photos"), s.Detail ?? Tr.T("Checking your folders…"), null, MaterialIconKind.Sync, StatusTone.Busy);

            case SyncState.Uploading:
            {
                var remaining = Math.Max(0, s.RunTotal - s.RunDone);
                var progress = Tr.T("{0} of {1}", Format.Count(s.RunDone), Format.Count(s.RunTotal));
                var trayDetail = s.BytesPerSecond > 1 ? $"{progress} · {Format.Speed(s.BytesPerSecond)}" : progress;
                if (s.Remaining is { } left)
                    trayDetail += $"\n{Format.TimeLeft(left)}";
                var detail = s.Detail ?? Tr.T("New photos and videos are being uploaded to Immich.");
                return new(Tr.T("Backing up {0}", Format.Plural(remaining, "file")), detail, trayDetail, MaterialIconKind.CloudUploadOutline, StatusTone.Busy);
            }

            case SyncState.Paused:
            {
                var detail = settings.PausedUntil is { } until
                    ? (until.Date == DateTimeOffset.Now.Date ? Tr.T("Resumes automatically at {0}.", FormatResume(until)) : Tr.T("Resumes automatically on {0}.", FormatResume(until)))
                    : Tr.T("New photos won't be uploaded until you resume.");
                return new(Tr.T("Backup paused"), detail, detail, MaterialIconKind.PauseCircleOutline, StatusTone.Paused);
            }

            case SyncState.WaitingForPower:
                return new(Tr.T("Waiting for power"), Tr.T("Backup continues when your PC is plugged in."), Tr.T("Waiting for the charger"), MaterialIconKind.PowerPlugOutline, StatusTone.Paused);

            case SyncState.WaitingForSchedule:
            {
                var from = Core.Sync.BackupSchedule.Format(settings.BackupWindowStartMinutes);
                var to = Core.Sync.BackupSchedule.Format(settings.BackupWindowEndMinutes);
                return new(Tr.T("Waiting until {0}", from), Tr.T("You chose to back up between {0} and {1}. Change this in Settings → Backup.", from, to),
                    Tr.T("Backs up from {0}", from), MaterialIconKind.ClockOutline, StatusTone.Paused);
            }

            case SyncState.WaitingForNetwork:
                return new(Tr.T("Saving data"), Tr.T("Backup is paused on this metered connection. Change this in Settings → Network."),
                    Tr.T("Paused on a metered connection"), MaterialIconKind.WifiStrengthAlertOutline, StatusTone.Paused);

            case SyncState.Offline:
                return new(Tr.T("Can't reach your server"), s.Detail ?? Tr.T("Retrying shortly."), Tr.T("Offline — will retry"), MaterialIconKind.CloudOffOutline, StatusTone.Warning);

            case SyncState.AuthError:
                return new(Tr.T("Sign in again"), s.Detail ?? Tr.T("Your sign-in has expired."), Tr.T("Signed out — open the app to sign in"), MaterialIconKind.AccountAlertOutline, StatusTone.Error);

            case SyncState.Error:
                return new(Tr.T("Something went wrong"), s.Detail ?? Tr.T("Retrying in a few minutes."), Tr.T("Something went wrong"), MaterialIconKind.AlertCircleOutline, StatusTone.Error);
        }

        // Idle
        if (!settings.Folders.Any(f => f.Enabled))
            return new(Tr.T("No folders selected"), Tr.T("Add a folder to start backing up."), Tr.T("No folders selected"), MaterialIconKind.FolderAlertOutline, StatusTone.Warning);

        if (c.Failed > 0)
        {
            return new(c.Failed == 1 ? Tr.T("1 file needs attention") : Tr.T("{0} files need attention", Format.Count(c.Failed)),
                Tr.T("Some files couldn't be uploaded. They'll be retried automatically."),
                Tr.T("{0} couldn't be uploaded", Format.Plural(c.Failed, "file")),
                MaterialIconKind.CloudAlertOutline, StatusTone.Warning);
        }

        if (c.Pending > 0)
            return new(Tr.T("Almost done"), Tr.T("{0} will upload shortly.", Format.Plural(c.Pending, "file")), Tr.T("{0} waiting", Format.Plural(c.Pending, "file")), MaterialIconKind.ClockOutline, StatusTone.Ok);

        var checkedText = s.LastSync is { } last ? Tr.T(" · Checked {0}", Format.Relative(last)) : "";
        return new(Tr.T("Everything is backed up"),
            Tr.T("{0} safe in Immich{1}", Format.Plural(c.BackedUp, "photo or video", "photos and videos"), checkedText),
            Tr.T("{0} backed up", Format.Plural(c.BackedUp, "file")),
            MaterialIconKind.CloudCheckOutline, StatusTone.Ok);
    }

    static string FormatResume(DateTimeOffset until)
    {
        var local = until.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("t") : local.ToString("ddd t");
    }
}
