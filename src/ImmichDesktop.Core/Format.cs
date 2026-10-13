using System.Globalization;

namespace ImmichDesktop.Core;

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
        $"{Count(count)} {(count == 1 ? singular : plural ?? singular + "s")}";

    public static string Duration(TimeSpan span)
    {
        if (span.TotalSeconds < 60) return Plural(Math.Max(1, (int)Math.Round(span.TotalSeconds)), "second");
        if (span.TotalMinutes < 60) return Plural((int)Math.Round(span.TotalMinutes), "minute");
        if (span.TotalHours < 24) return Plural((int)Math.Round(span.TotalHours), "hour");
        return Plural((int)Math.Round(span.TotalDays), "day");
    }

    public static string Relative(DateTimeOffset time, DateTimeOffset? now = null)
    {
        var delta = (now ?? DateTimeOffset.Now) - time;
        if (delta.TotalSeconds < 45) return "just now";
        if (delta.TotalMinutes < 60) return $"{Math.Max(1, (int)delta.TotalMinutes)} min ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours} hr ago";
        if (delta.TotalDays < 2) return "yesterday";
        if (delta.TotalDays < 7) return $"{(int)delta.TotalDays} days ago";
        return time.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
    }
}
