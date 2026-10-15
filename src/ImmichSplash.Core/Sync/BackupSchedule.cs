using ImmichSplash.Core.Settings;

namespace ImmichSplash.Core.Sync;

/// <summary>
/// "Only back up during certain hours": whether the window is open at a time, and when it opens next.
/// Times are minutes after midnight; a window can cross midnight (22:00–06:00).
/// </summary>
public static class BackupSchedule
{
    public static bool IsOpen(AppSettings settings, DateTime now)
    {
        if (!settings.BackupWindowEnabled)
            return true;
        int start = settings.BackupWindowStartMinutes, end = settings.BackupWindowEndMinutes;
        if (start == end)
            return true; // a full day
        var minute = (int)now.TimeOfDay.TotalMinutes;
        return start < end ? minute >= start && minute < end : minute >= start || minute < end;
    }

    /// <summary>When the window next opens (now, if it's open).</summary>
    public static DateTime NextOpen(AppSettings settings, DateTime now)
    {
        if (IsOpen(settings, now))
            return now;
        var opens = now.Date.AddMinutes(settings.BackupWindowStartMinutes);
        return opens > now ? opens : opens.AddDays(1);
    }

    /// <summary>"01:00" style (the PC's own time format).</summary>
    public static string Format(int minutes) =>
        DateTime.Today.AddMinutes(minutes).ToString("t", System.Globalization.CultureInfo.CurrentCulture);
}
