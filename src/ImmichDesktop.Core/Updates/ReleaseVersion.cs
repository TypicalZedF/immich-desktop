using System.Globalization;

namespace ImmichDesktop.Core.Updates;

/// <summary>
/// A release version: "1.3.0", or a beta such as "1.3.1-beta.12", which comes before "1.3.1"
/// (semantic versioning order; numeric parts compare as numbers, so beta.10 is after beta.9).
/// </summary>
public sealed record ReleaseVersion(Version Core, string? Prerelease = null) : IComparable<ReleaseVersion>
{
    public bool IsPrerelease => Prerelease is not null;

    public static ReleaseVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var clean = text.Trim().TrimStart('v', 'V').Split('+')[0];
        var dash = clean.IndexOf('-');
        var core = dash < 0 ? clean : clean[..dash];
        var pre = dash < 0 ? null : clean[(dash + 1)..];
        if (!System.Version.TryParse(core, out var v))
            return null;
        return new ReleaseVersion(new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build)), string.IsNullOrEmpty(pre) ? null : pre);
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
            return 1;
        var byCore = Core.CompareTo(other.Core);
        if (byCore != 0)
            return byCore;
        if (Prerelease is null || other.Prerelease is null)
            return (Prerelease is null ? 1 : 0) - (other.Prerelease is null ? 1 : 0);

        var mine = Prerelease.Split('.');
        var theirs = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(mine.Length, theirs.Length); i++)
        {
            var aNum = long.TryParse(mine[i], NumberStyles.None, CultureInfo.InvariantCulture, out var a);
            var bNum = long.TryParse(theirs[i], NumberStyles.None, CultureInfo.InvariantCulture, out var b);
            var part = (aNum, bNum) switch
            {
                (true, true) => a.CompareTo(b),
                (true, false) => -1, // numbers sort before words
                (false, true) => 1,
                _ => string.CompareOrdinal(mine[i], theirs[i]),
            };
            if (part != 0)
                return part;
        }
        return mine.Length.CompareTo(theirs.Length);
    }

    public static bool operator <(ReleaseVersion? a, ReleaseVersion? b) => Compare(a, b) < 0;
    public static bool operator >(ReleaseVersion? a, ReleaseVersion? b) => Compare(a, b) > 0;
    public static bool operator <=(ReleaseVersion? a, ReleaseVersion? b) => Compare(a, b) <= 0;
    public static bool operator >=(ReleaseVersion? a, ReleaseVersion? b) => Compare(a, b) >= 0;

    static int Compare(ReleaseVersion? a, ReleaseVersion? b) => a is null ? (b is null ? 0 : -1) : a.CompareTo(b);

    public override string ToString() => Prerelease is null ? Core.ToString(3) : $"{Core.ToString(3)}-{Prerelease}";
}
