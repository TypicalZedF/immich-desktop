using ImmichDesktop.Core;
using ImmichDesktop.Core.Settings;
using ImmichDesktop.Core.Sync;
using Material.Icons;

namespace ImmichDesktop.App.ViewModels;

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
                return new("Not signed in", "Sign in to start backing up.", null, MaterialIconKind.AccountAlertOutline, StatusTone.Warning);

            case SyncState.Scanning:
                return new("Looking for new photos", s.Detail ?? "Checking your folders…", null, MaterialIconKind.Sync, StatusTone.Busy);

            case SyncState.Uploading:
            {
                var remaining = Math.Max(0, s.RunTotal - s.RunDone);
                var progress = $"{Format.Count(s.RunDone)} of {Format.Count(s.RunTotal)}";
                var trayDetail = s.BytesPerSecond > 1 ? $"{progress} · {Format.Speed(s.BytesPerSecond)}" : progress;
                var detail = s.Detail ?? "New photos and videos are being uploaded to Immich.";
                return new($"Backing up {Format.Plural(remaining, "file")}", detail, trayDetail, MaterialIconKind.CloudUploadOutline, StatusTone.Busy);
            }

            case SyncState.Paused:
            {
                var detail = settings.PausedUntil is { } until
                    ? $"Resumes automatically {(until.Date == DateTimeOffset.Now.Date ? "at" : "on")} {FormatResume(until)}."
                    : "New photos won't be uploaded until you resume.";
                return new("Backup paused", detail, detail, MaterialIconKind.PauseCircleOutline, StatusTone.Paused);
            }

            case SyncState.WaitingForPower:
                return new("Waiting for power", "Backup continues when your PC is plugged in.", "Waiting for the charger", MaterialIconKind.PowerPlugOutline, StatusTone.Paused);

            case SyncState.Offline:
                return new("Can't reach your server", s.Detail ?? "Retrying shortly.", "Offline — will retry", MaterialIconKind.CloudOffOutline, StatusTone.Warning);

            case SyncState.AuthError:
                return new("Sign in again", s.Detail ?? "Your sign-in has expired.", "Signed out — open the app to sign in", MaterialIconKind.AccountAlertOutline, StatusTone.Error);

            case SyncState.Error:
                return new("Something went wrong", s.Detail ?? "Retrying in a few minutes.", "Something went wrong", MaterialIconKind.AlertCircleOutline, StatusTone.Error);
        }

        // Idle
        if (!settings.Folders.Any(f => f.Enabled))
            return new("No folders selected", "Add a folder to start backing up.", "No folders selected", MaterialIconKind.FolderAlertOutline, StatusTone.Warning);

        if (c.Failed > 0)
        {
            return new($"{Format.Plural(c.Failed, "file needs", "files need")} attention",
                "Some files couldn't be uploaded. They'll be retried automatically.",
                $"{Format.Plural(c.Failed, "file")} couldn't be uploaded",
                MaterialIconKind.CloudAlertOutline, StatusTone.Warning);
        }

        if (c.Pending > 0)
            return new("Almost done", $"{Format.Plural(c.Pending, "file")} will upload shortly.", $"{Format.Plural(c.Pending, "file")} waiting", MaterialIconKind.ClockOutline, StatusTone.Ok);

        var checkedText = s.LastSync is { } last ? $" · Checked {Format.Relative(last)}" : "";
        return new("Everything is backed up",
            $"{Format.Plural(c.BackedUp, "photo or video", "photos and videos")} safe in Immich{checkedText}",
            $"{Format.Plural(c.BackedUp, "file")} backed up",
            MaterialIconKind.CloudCheckOutline, StatusTone.Ok);
    }

    static string FormatResume(DateTimeOffset until)
    {
        var local = until.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("t") : local.ToString("ddd t");
    }
}
