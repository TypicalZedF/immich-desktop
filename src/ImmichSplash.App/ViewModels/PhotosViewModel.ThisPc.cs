using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;

namespace ImmichSplash.App.ViewModels;

public sealed partial class PhotosViewModel
{
    // ---- This PC: photos in backup folders, and which Immich photos are also here ----

    Dictionary<string, string> _onDevice = new(StringComparer.Ordinal);

    /// <summary>Tiles show whether each photo is in Immich, on this PC, or both.</summary>
    internal bool ShowSyncStatus => !IsPicker && !InTrash && !InLocked && Drill?.ShowSizes != true;

    /// <summary>Tiles show file sizes (Large files).</summary>
    internal bool ShowFileSizes => Drill?.ShowSizes == true;

    /// <summary>The file on this PC an Immich photo came from, if any.</summary>
    internal string? DevicePathFor(string assetId) => _onDevice.GetValueOrDefault(assetId);

    /// <summary>
    /// The timeline plus photos and videos in your backup folders that aren't in Immich yet, each in
    /// its month (like the mobile app's timeline, which shows what's on the phone too).
    /// </summary>
    static async Task<List<BucketState>> TimelineWithLocalAsync(ImmichClient client, CancellationToken ct)
    {
        var localTask = LocalOnlyAssetsAsync(ct);
        var server = await client.GetTimeBucketsAsync(TimelineFilter.All, ct);
        var local = (await localTask)
            .GroupBy(a => MonthKey(a.FileCreatedAt!))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.FileCreatedAt, StringComparer.Ordinal).ToList());
        if (local.Count == 0)
            return server.Where(b => b.Count > 0).Select((b, i) => BucketState.FromTimeline(client, b, i, TimelineFilter.All)).ToList();

        var serverByMonth = server.Where(b => b.Count > 0).ToDictionary(b => MonthKey(b.Key), b => b);
        var months = serverByMonth.Keys.Union(local.Keys).OrderByDescending(k => k, StringComparer.Ordinal).ToList();
        var buckets = new List<BucketState>(months.Count);
        foreach (var month in months)
        {
            serverByMonth.TryGetValue(month, out var bucket);
            var mine = local.GetValueOrDefault(month) ?? [];
            if (bucket is null)
            {
                var group = BucketState.FromAssets(mine)[0]; // one month, so one group
                buckets.Add(new BucketState(group.Label, mine.Count, buckets.Count, () => Task.FromResult(mine.ToList()), group.Month));
                continue;
            }
            var template = BucketState.FromTimeline(client, bucket, buckets.Count, TimelineFilter.All);
            buckets.Add(new BucketState(template.Label, bucket.Count + mine.Count, buckets.Count, async () =>
            {
                var assets = await client.GetTimeBucketAsync(bucket.Key, TimelineFilter.All, CancellationToken.None);
                return assets.Concat(mine).OrderByDescending(a => SortKey(a.FileCreatedAt)).ToList();
            }, template.Month));
        }
        return buckets;

        static string MonthKey(string date) =>
            DateTimeOffset.TryParse(date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
                ? $"{d.Year:0000}-{d.Month:00}" : date;

        static DateTimeOffset SortKey(string? date) =>
            DateTimeOffset.TryParse(date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
                ? d : DateTimeOffset.MinValue;
    }

    /// <summary>Photos and videos in your backup folders that aren't in Immich yet, newest first.</summary>
    internal static Task<List<TimelineAsset>> LocalOnlyAssetsAsync(CancellationToken ct) => Task.Run(() =>
    {
        var folders = AppServices.Settings.Current.Folders.Where(f => f.Enabled).Select(f => f.Id).ToList();
        var media = Core.Sync.MediaTypes.Default;
        return AppServices.Database.GetLocalOnly(folders)
            .Where(f => File.Exists(f.Path)) // deleted since the last scan
            .OrderByDescending(f => f.ModifiedTicks)
            .Select(f =>
            {
                // When the file was last changed: the closest thing to "taken" without reading the file.
                var when = new DateTimeOffset(new DateTime(f.ModifiedTicks, DateTimeKind.Utc).ToLocalTime());
                return new TimelineAsset($"local:{f.Path}", media.Classify(f.Path) != Core.Sync.MediaKind.Video, false, 1, null,
                    when.ToString("o"), f.Path, f.Failed);
            })
            .ToList();
    }, ct);

    static async Task<List<BucketState>> TimelineAsync(ImmichClient client, TimelineFilter filter, CancellationToken ct)
    {
        var buckets = await client.GetTimeBucketsAsync(filter, ct);
        return buckets.Where(b => b.Count > 0).Select((b, i) => BucketState.FromTimeline(client, b, i, filter)).ToList();
    }

    async Task LoadGridAsync()
    {
        if (Client is not { } client)
        {
            Error = Tr.T("Sign in to see your library.");
            IsLoading = false;
            return;
        }

        _gridLoad?.Cancel();
        var cts = _gridLoad = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var query = CurrentQuery;
        IsLoading = true;
        IsEmpty = false;
        Error = null;
        Summary = "";
        ReleaseRows();
        try
        {
            if (!IsPicker)
                _onDevice = await Task.Run(AppServices.Database.GetAssetsOnDevice, cts.Token);
            var buckets = await query.Load(client, cts.Token);
            buckets = await BucketState.SplitRecentDaysAsync(buckets, Today(), cts.Token);
            await LoadMemoriesAsync(client, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            _buckets = buckets;
            var total = _buckets.Sum(b => b.Count);
            Summary = Format.Plural(total, Tr.T("photo or video"), Tr.T("photos and videos"));
            IsEmpty = total == 0;
            EmptyText = Drill is not null ? Tr.T("Nothing here.") : Page switch
            {
                LibraryPage.Favorites => Tr.T("No favorites yet. Tap the heart on a photo to add it here."),
                LibraryPage.Archive => Tr.T("Nothing archived. Archived photos are hidden from your timeline but kept in Immich."),
                LibraryPage.Locked => Tr.T("Your locked folder is empty. Photos here are hidden everywhere until you unlock it with your PIN."),
                LibraryPage.Trash => Tr.T("The trash is empty."),
                _ => Tr.T("No photos yet. They'll appear here once they're backed up."),
            };
            RebuildRows();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ImmichApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            Error = Tr.T("Your API key doesn't allow viewing the library. Give it the asset.read and asset.view permissions, or sign in with your password or SSO.");
        }
        catch (Exception ex)
        {
            Log.Warn("Could not load the library", ex);
            Error = Tr.T("Couldn't load your library. {0}", NetworkErrors.Describe(ex));
        }
        finally
        {
            if (ReferenceEquals(cts, _gridLoad))
                IsLoading = false;
        }
    }
}
