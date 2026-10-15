using ImmichSplash.Core.Api;

namespace ImmichSplash.Core.Settings;

public static class MapSettings
{
    /// <summary>The map's photo filter from the saved Map settings.</summary>
    public static MapFilter Filter(AppSettings s, DateTimeOffset now)
    {
        DateTimeOffset? after = s.MapDateRange switch
        {
            MapDateRange.Day => now.AddDays(-1),
            MapDateRange.Week => now.AddDays(-7),
            MapDateRange.Month => now.AddMonths(-1),
            MapDateRange.Year => now.AddYears(-1),
            MapDateRange.ThreeYears => now.AddYears(-3),
            MapDateRange.Custom when s.MapDateFrom is { } from => new DateTimeOffset(from.Date, TimeSpan.Zero),
            _ => null,
        };
        // A custom range includes the whole last day.
        DateTimeOffset? before = s.MapDateRange == MapDateRange.Custom && s.MapDateTo is { } to
            ? new DateTimeOffset(to.Date.AddDays(1).AddTicks(-1), TimeSpan.Zero)
            : null;
        return new MapFilter(s.MapOnlyFavorites, s.MapIncludeArchived, s.MapIncludePartners, s.MapIncludeSharedAlbums, after, before);
    }
}
