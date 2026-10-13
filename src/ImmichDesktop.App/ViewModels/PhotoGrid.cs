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

    public BucketState(string label, int count, int position, Func<Task<List<TimelineAsset>>> load, DateTime? month = null)
    {
        Label = label;
        Count = count;
        Position = position;
        _load = load;
        Month = month;
    }

    public string Label { get; }

    /// <summary>The month this group covers, when it is one (for the date rail).</summary>
    public DateTime? Month { get; }
    public int Count { get; }
    public int Position { get; }

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
            groups.Add(new BucketState(month.Key is { } key ? MonthLabel(key) : "Other", items.Count, groups.Count, () => Task.FromResult(items),
                month.Key is { } k ? ParseMonth(k) : null));
        }
        return groups;
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
    public double TileSize { get; }
    public double RowHeight => TileSize + PhotosViewModel.Gap;
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
    public double TileSize { get; } = tile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVideo), nameof(IsFavorite), nameof(DurationText), nameof(IsMissing), nameof(CanOpen))]
    public partial TimelineAsset? Asset { get; set; }

    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    bool _bound;

    public bool IsMissing => _bound && Asset is null;
    public bool IsVideo => Asset is { IsImage: false };
    public bool IsFavorite => Asset?.IsFavorite == true;
    public string DurationText => Asset?.DurationMs is { } ms and > 0 ? FormatDuration(ms) : "";

    /// <summary>Videos can't be picked as a profile picture.</summary>
    public bool CanOpen => !(owner.IsPicker && IsVideo);

    /// <summary>Selecting (the check in the corner) is for the library, not for picking a profile picture.</summary>
    public bool CanSelect => !owner.IsPicker;

    internal void Bind(TimelineAsset? asset)
    {
        _bound = true;
        Asset = asset;
        IsSelected = asset is not null && owner.IsAssetSelected(asset.Id);
    }

    internal async Task LoadThumbnailAsync(CancellationToken ct)
    {
        if (Asset is null || Thumbnail is not null || owner.Thumbnails is not { } loader)
            return;
        try
        {
            var bitmap = await loader.LoadAsync(Asset.Id, AssetImageSize.Thumbnail, owner.DecodeWidth, ct);
            if (ct.IsCancellationRequested)
                bitmap?.Dispose();
            else
                Thumbnail = bitmap;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Info($"Thumbnail failed for {Asset.Id}: {ex.Message}");
        }
    }

    internal void Release()
    {
        var bitmap = Thumbnail;
        Thumbnail = null;
        bitmap?.Dispose();
    }

    /// <summary>Opens the photo, or (while selecting) adds it to the selection.</summary>
    [RelayCommand]
    void Open()
    {
        if (owner.IsSelecting && Asset is not null)
            owner.ToggleSelection(this);
        else
            owner.OpenViewer(this);
    }

    [RelayCommand]
    void ToggleSelect()
    {
        if (Asset is not null)
            owner.ToggleSelection(this);
    }

    internal static string FormatDuration(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}
