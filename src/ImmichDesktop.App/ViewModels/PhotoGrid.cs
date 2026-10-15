using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichDesktop.Core;
using ImmichDesktop.Core.Api;

namespace ImmichDesktop.App.ViewModels;

/// <summary>
/// One group of the grid (usually a month). Its assets are fetched on first need and kept: from the
/// timeline for the library, an album or a person, or given up front for search results and the map.
/// </summary>
public sealed class BucketState
{
    readonly Func<Task<List<TimelineAsset>>> _load;
    Task<List<TimelineAsset>>? _assets;

    public BucketState(string label, int count, int position, Func<Task<List<TimelineAsset>>> load, DateTime? month = null, DateTime? day = null)
    {
        Label = label;
        Count = count;
        Position = position;
        _load = load;
        Month = month;
        Day = day;
    }

    public string Label { get; }

    /// <summary>The month this group covers, when it is one (for the date rail).</summary>
    public DateTime? Month { get; }

    /// <summary>The day this group covers, for recent days ("Today", "Yesterday", "Monday"…).</summary>
    public DateTime? Day { get; }
    public int Count { get; }

    /// <summary>Where it is among the grid's groups.</summary>
    public int Position { get; private set; }

    /// <summary>A month of the timeline (optionally just one album or person).</summary>
    public static BucketState FromTimeline(ImmichClient client, TimeBucket bucket, int position, TimelineFilter filter) =>
        new(MonthLabel(bucket.Key), bucket.Count, position, () => client.GetTimeBucketAsync(bucket.Key, filter, CancellationToken.None), ParseMonth(bucket.Key));

    /// <summary>Groups loaded assets by month, or keeps them in one group (e.g. search results, best match first).</summary>
    public static List<BucketState> FromAssets(IReadOnlyList<TimelineAsset> assets, string? singleGroupLabel = null)
    {
        if (singleGroupLabel is not null)
            return assets.Count == 0 ? [] : [new BucketState(singleGroupLabel, assets.Count, 0, () => Task.FromResult(assets.ToList()))];

        var groups = new List<BucketState>();
        foreach (var month in assets.GroupBy(a => MonthKey(a.FileCreatedAt)))
        {
            var items = month.ToList();
            groups.Add(new BucketState(month.Key is { } key ? MonthLabel(key) : Tr.T("Other"), items.Count, groups.Count, () => Task.FromResult(items),
                month.Key is { } k ? ParseMonth(k) : null));
        }
        return groups;
    }

    /// <summary>
    /// Like the Immich apps, recent photos are grouped by day instead of by month: Today, Yesterday,
    /// the weekday for the rest of the past week, then the date for the rest of this month; older
    /// photos stay in months. Only this month's group (and last month's, early in a month) is read
    /// to do it, and the days share what was read.
    /// </summary>
    public static async Task<List<BucketState>> SplitRecentDaysAsync(List<BucketState> buckets, DateTime today, CancellationToken ct)
    {
        today = today.Date;
        var weekStart = today.AddDays(-6);
        bool SameMonth(DateTime a, DateTime b) => a.Year == b.Year && a.Month == b.Month;
        bool IsRecentMonth(DateTime month) => SameMonth(month, today) || SameMonth(month, weekStart);
        bool OwnGroup(DateTime day) => day >= weekStart || SameMonth(day, today);

        if (!buckets.Any(b => b.Month is { } m && b.Day is null && IsRecentMonth(m)))
            return buckets;
        var result = new List<BucketState>(buckets.Count + 8);
        foreach (var bucket in buckets)
        {
            if (bucket.Month is not { } month || bucket.Day is not null || !IsRecentMonth(month))
            {
                result.Add(bucket);
                continue;
            }
            var assets = await bucket.GetAssetsAsync(ct);
            // Keeps the order they came in (newest first in the timeline); the rest of an older month stays one group.
            foreach (var group in assets.GroupBy(a => LocalDay(a.FileCreatedAt) is { } d && OwnGroup(d) ? d : (DateTime?)null))
            {
                var items = group.ToList();
                result.Add(group.Key is { } day
                    ? new BucketState(DayLabel(day, today), items.Count, 0, () => Task.FromResult(items), month, day)
                    : new BucketState(bucket.Label, items.Count, 0, () => Task.FromResult(items), month));
            }
        }
        for (var i = 0; i < result.Count; i++)
            result[i].Position = i;
        return result;
    }

    static DateTime? LocalDay(string? date) =>
        DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.LocalDateTime.Date : null;

    static string DayLabel(DateTime day, DateTime today)
    {
        var ago = (today - day).Days;
        return ago switch
        {
            0 => Tr.T("Today"),
            1 => Tr.T("Yesterday"),
            > 1 and < 7 => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(day.ToString("dddd", CultureInfo.CurrentCulture)),
            _ => day.ToString(day.Year == today.Year ? "ddd, MMM d" : "ddd, MMM d, yyyy", CultureInfo.CurrentCulture),
        };
    }

    public Task<IReadOnlyList<TimelineAsset>> GetAssetsAsync(CancellationToken ct)
    {
        // Shared by every row of the group; a failed load is retried next time.
        if (_assets is null || _assets.IsFaulted || _assets.IsCanceled)
            _assets = _load();
        return Cast(_assets.WaitAsync(ct));

        static async Task<IReadOnlyList<TimelineAsset>> Cast(Task<List<TimelineAsset>> task) => await task;
    }

    /// <summary>Updates a loaded asset in place (e.g. after favoriting it).</summary>
    public bool Replace(string assetId, Func<TimelineAsset, TimelineAsset> update)
    {
        if (_assets is not { IsCompletedSuccessfully: true } loaded)
            return false;
        var list = loaded.Result;
        var index = list.FindIndex(a => a.Id == assetId);
        if (index < 0)
            return false;
        list[index] = update(list[index]);
        return true;
    }

    static string? MonthKey(string? date) =>
        DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? $"{d.Year:0000}-{d.Month:00}-01" : null;

    static DateTime? ParseMonth(string key) =>
        DateTime.TryParse(key, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var date) ? date : null;

    static string MonthLabel(string key) =>
        ParseMonth(key) is { } date
            ? date.ToString(date.Year == DateTime.Now.Year ? "MMMM" : "MMMM yyyy", CultureInfo.CurrentCulture)
            : key;
}

public sealed class MonthHeaderViewModel(string title, string count)
{
    public string Title { get; } = title;
    public string Count { get; } = count;
}

/// <summary>A row of square tiles. Realized only while visible.</summary>
public sealed class AssetRowViewModel : ViewModelBase
{
    readonly PhotosViewModel _owner;
    CancellationTokenSource? _cts;

    public AssetRowViewModel(PhotosViewModel owner, BucketState bucket, int start, int count, double tile)
    {
        _owner = owner;
        Bucket = bucket;
        Start = start;
        TileSize = tile;
        for (var i = 0; i < count; i++)
            Cells.Add(new AssetCellViewModel(owner, bucket, start + i, tile));
    }

    public BucketState Bucket { get; }
    public int Start { get; }
    public double TileSize { get; private set; }
    public double RowHeight => TileSize + PhotosViewModel.Gap;

    internal void Resize(double tile)
    {
        TileSize = tile;
        foreach (var cell in Cells)
            cell.TileSize = tile;
        OnPropertyChanged(nameof(TileSize));
        OnPropertyChanged(nameof(RowHeight));
    }
    public ObservableCollection<AssetCellViewModel> Cells { get; } = [];

    public async void Realize()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        try
        {
            var assets = await Bucket.GetAssetsAsync(cts.Token);
            foreach (var cell in Cells)
            {
                if (cts.IsCancellationRequested)
                    return;
                cell.Bind(cell.Index < assets.Count ? assets[cell.Index] : null);
                _ = cell.LoadThumbnailAsync(cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not load {Bucket.Label}", ex);
        }
    }

    public void Unrealize()
    {
        _cts?.Cancel();
        _cts = null;
        foreach (var cell in Cells)
            cell.Release();
    }
}

public sealed partial class AssetCellViewModel(PhotosViewModel owner, BucketState bucket, int index, double tile) : ViewModelBase
{
    public BucketState Bucket { get; } = bucket;
    public int Index { get; } = index;
    [ObservableProperty]
    public partial double TileSize { get; set; } = tile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVideo), nameof(IsFavorite), nameof(DurationText), nameof(IsMissing), nameof(CanOpen),
        nameof(CanSelect), nameof(SyncIcon), nameof(SyncText), nameof(HasSyncIcon), nameof(SyncFailed), nameof(IsLocalOnly), nameof(SizeText), nameof(HasSizeText), nameof(ShowLocalPlaceholder),
        nameof(IsLivePhoto), nameof(IsStack), nameof(StackText), nameof(AccessibleName))]
    public partial TimelineAsset? Asset { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLocalPlaceholder))]
    public partial Bitmap? Thumbnail { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    bool _bound;

    public bool IsMissing => _bound && Asset is null;
    public bool IsVideo => Asset is { IsImage: false };
    public bool IsFavorite => Asset?.IsFavorite == true;
    public string DurationText => Asset?.DurationMs is { } ms and > 0 ? FormatDuration(ms) : "";
    public bool IsLivePhoto => Asset is { IsLivePhoto: true, IsImage: true };

    /// <summary>What a screen reader says for this tile: the kind of item and when it was taken.</summary>
    public string AccessibleName
    {
        get
        {
            if (Asset is not { } a)
                return "";
            var kind = a switch
            {
                { IsImage: false } => Tr.T("Video"),
                { IsStack: true } => Tr.T("Stack of {0}", a.StackCount),
                { IsLivePhoto: true } => Tr.T("Live photo"),
                _ => Tr.T("Photo"),
            };
            var parts = new List<string> { kind };
            if (DateTimeOffset.TryParse(a.FileCreatedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
                parts.Add(date.ToLocalTime().ToString("D", System.Globalization.CultureInfo.CurrentCulture));
            if (DurationText.Length > 0)
                parts.Add(DurationText);
            if (a.IsFavorite)
                parts.Add(Tr.T("Favorite"));
            if (SyncText.Length > 0)
                parts.Add(SyncText);
            return string.Join(", ", parts);
        }
    }
    public bool IsStack => Asset is { IsStack: true };
    public string StackText => Asset is { IsStack: true } a ? a.StackCount.ToString(System.Globalization.CultureInfo.CurrentCulture) : "";

    /// <summary>Videos can't be picked as a profile picture.</summary>
    public bool CanOpen => !(owner.IsPicker && IsVideo);

    /// <summary>
    /// Selecting (the check in the corner) is for the library, not for picking a profile picture, and
    /// not for files that are only on this PC (there's nothing in Immich to act on yet).
    /// </summary>
    public bool CanSelect => !owner.IsPicker && Asset is not { IsLocalOnly: true };

    public bool IsLocalOnly => Asset is { IsLocalOnly: true };

    public string SizeText => owner.ShowFileSizes && Asset?.FileSize is long size and > 0 ? Format.Bytes(size) : "";
    public bool HasSizeText => SizeText.Length > 0;

    /// <summary>A file on this PC we can't draw a preview of (a video, or a format like HEIC).</summary>
    public bool ShowLocalPlaceholder => IsLocalOnly && Thumbnail is null && _thumbnailTried;

    bool _thumbnailTried;

    // ---- Where it is: in Immich, on this PC, or both (like the mobile app's timeline badges) ----

    enum Sync { None, CloudOnly, Both, Waiting, Failed }

    Sync SyncState => Asset switch
    {
        null => Sync.None,
        _ when !owner.ShowSyncStatus => Sync.None,
        { IsLocalOnly: true, LocalFailed: true } => Sync.Failed,
        { IsLocalOnly: true } => Sync.Waiting,
        var a when owner.DevicePathFor(a.Id) is not null => Sync.Both,
        _ => Sync.CloudOnly,
    };

    public bool HasSyncIcon => SyncState != Sync.None;
    public bool SyncFailed => SyncState == Sync.Failed;

    public Material.Icons.MaterialIconKind SyncIcon => SyncState switch
    {
        Sync.Both => Material.Icons.MaterialIconKind.CloudCheckOutline,
        Sync.Waiting => Material.Icons.MaterialIconKind.CloudUploadOutline,
        Sync.Failed => Material.Icons.MaterialIconKind.CloudAlertOutline,
        _ => Material.Icons.MaterialIconKind.CloudOutline,
    };

    public string SyncText => SyncState switch
    {
        Sync.Both => Tr.T("Backed up · also on this PC"),
        Sync.Waiting => Tr.T("On this PC · waiting to back up"),
        Sync.Failed => Tr.T("On this PC · couldn't back up yet (will retry)"),
        Sync.CloudOnly => Tr.T("In Immich only"),
        _ => "",
    };

    internal void Bind(TimelineAsset? asset)
    {
        _bound = true;
        Asset = asset;
        IsSelected = asset is not null && owner.IsAssetSelected(asset.Id);
    }

    internal async Task LoadThumbnailAsync(CancellationToken ct)
    {
        if (Asset is not { } asset || Thumbnail is not null || owner.Thumbnails is not { } loader)
            return;
        try
        {
            if (asset.LocalPath is { } path)
            {
                var local = await loader.LoadLocalAsync(path, asset.IsImage, owner.DecodeWidth, ct);
                if (ct.IsCancellationRequested)
                    local?.Dispose();
                else
                {
                    Thumbnail = local;
                    _ownsThumbnail = true;
                }
            }
            else
            {
                // Shared, decoded thumbnails: scrolling back shows them instantly (the cache owns them).
                var bitmap = await loader.LoadGridThumbnailAsync(asset.Id, owner.DecodeWidth, ct);
                if (!ct.IsCancellationRequested)
                {
                    Thumbnail = bitmap;
                    _ownsThumbnail = false;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Info($"Thumbnail failed for {asset.Id}: {ex.Message}");
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                _thumbnailTried = true;
                OnPropertyChanged(nameof(ShowLocalPlaceholder));
            }
        }
    }

    bool _ownsThumbnail;

    internal void Release()
    {
        var bitmap = Thumbnail;
        _thumbnailTried = false;
        Thumbnail = null;
        if (_ownsThumbnail)
            bitmap?.Dispose();
    }

    /// <summary>Opens the photo, or (while selecting) adds it to the selection.</summary>
    [RelayCommand]
    void Open()
    {
        if (owner.IsSelecting && CanSelect && Asset is not null)
            owner.ToggleSelection(this);
        else
            owner.OpenViewer(this);
    }

    [RelayCommand]
    void ToggleSelect()
    {
        if (CanSelect && Asset is not null)
            owner.ToggleSelection(this);
    }

    internal static string FormatDuration(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}
