using System.Globalization;

namespace ImmichSplash.Core;

/// <summary>Human-friendly formatting shared by the UI and the tray.</summary>
public static class Format
{
    static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(double bytes)
    {
        var unit = 0;
        while (bytes >= 1024 && unit < Units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes:0} {Units[unit]}"
            : bytes.ToString(bytes < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + Units[unit];
    }

    public static string Speed(double bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

    public static string Count(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    public static string Plural(long count, string singular, string? plural = null) =>
        $"{Count(count)} {Loc.T(count == 1 ? singular : plural ?? singular + "s")}";

    public static string Duration(TimeSpan span)
    {
        if (span.TotalSeconds < 60) return Plural(Math.Max(1, (int)Math.Round(span.TotalSeconds)), "second");
        if (span.TotalMinutes < 60) return Plural((int)Math.Round(span.TotalMinutes), "minute");
        if (span.TotalHours < 24) return Plural((int)Math.Round(span.TotalHours), "hour");
        return Plural((int)Math.Round(span.TotalDays), "day");
    }

    /// <summary>"About 2 hours 15 minutes left", rounded so it doesn't flicker.</summary>
    public static string TimeLeft(TimeSpan t)
    {
        if (t.TotalMinutes < 1) return Loc.T("Less than a minute left");
        if (t.TotalHours < 1) return Loc.T("About {0} left", Plural((int)Math.Ceiling(t.TotalMinutes), "minute"));
        if (t.TotalHours < 10)
        {
            var hours = (int)t.TotalHours;
            var minutes = (int)Math.Round((t.TotalMinutes - hours * 60) / 5) * 5;
            if (minutes == 60) { hours++; minutes = 0; }
            return minutes == 0
                ? Loc.T("About {0} left", Plural(hours, "hour"))
                : Loc.T("About {0} {1} left", Plural(hours, "hour"), Plural(minutes, "minute"));
        }
        if (t.TotalHours < 48) return Loc.T("About {0} left", Plural((int)Math.Round(t.TotalHours), "hour"));
        return Loc.T("About {0} left", Plural((int)Math.Round(t.TotalDays), "day"));
    }

    public static string Relative(DateTimeOffset time, DateTimeOffset? now = null)
    {
        var delta = (now ?? DateTimeOffset.Now) - time;
        if (delta.TotalSeconds < 45) return Loc.T("just now");
        if (delta.TotalMinutes < 60) return Loc.T("{0} min ago", Math.Max(1, (int)delta.TotalMinutes));
        if (delta.TotalHours < 24) return Loc.T("{0} hr ago", (int)delta.TotalHours);
        if (delta.TotalDays < 2) return Loc.T("yesterday");
        if (delta.TotalDays < 7) return Loc.T("{0} days ago", (int)delta.TotalDays);
        return time.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
    }
}
