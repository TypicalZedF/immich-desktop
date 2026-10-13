using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichDesktop.App.Services;
using ImmichDesktop.Core;
using ImmichDesktop.Core.Api;
using ImmichDesktop.Core.Platform;

namespace ImmichDesktop.App.ViewModels;

/// <summary>
/// Your Immich library as a scrolling grid, newest first, grouped by month.
/// Only what's on screen is built: months are fetched when their rows scroll into view,
/// thumbnails load as cells appear and are released when they scroll away.
/// </summary>
public sealed partial class PhotosViewModel : ViewModelBase, IDisposable
{
    public const double Gap = 4;
    const double TargetTile = 168;

    readonly CancellationTokenSource _lifetime = new();
    List<BucketState> _buckets = [];
    int _columns;
    double _width;

    readonly Action<AssetCellViewModel>? _pick;

    public PhotosViewModel(ShellViewModel shell)
    {
        _ = shell;
        _ = LoadAsync();
    }

    PhotosViewModel(Action<AssetCellViewModel> pick)
    {
        _pick = pick;
        _ = LoadAsync();
    }

    /// <summary>The same grid for choosing a photo (e.g. a profile picture): clicking a photo picks it instead of opening it.</summary>
    public static PhotosViewModel ForPicking(Action<AssetCellViewModel> pick) => new(pick);

    public bool IsPicker => _pick is not null;

    internal ImmichClient? Client => AppServices.Account.Client;
    internal ThumbnailLoader? Thumbnails { get; private set; }

    /// <summary>Pixels to decode thumbnails at (tile size × display scaling).</summary>
    internal int DecodeWidth { get; private set; } = 256;

    [ObservableProperty]
    public partial ObservableCollection<object> Rows { get; set; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial PhotoViewerViewModel? Viewer { get; set; }

    [RelayCommand]
    Task Refresh() => LoadAsync();

    async Task LoadAsync()
    {
        if (Client is not { } client)
        {
            Error = "Sign in to see your library.";
            IsLoading = false;
            return;
        }

        IsLoading = true;
        Error = null;
        try
        {
            Thumbnails?.Dispose();
            Thumbnails = new ThumbnailLoader(client);
            var buckets = await client.GetTimeBucketsAsync(_lifetime.Token);
            _buckets = buckets.Where(b => b.Count > 0).Select((b, i) => new BucketState(b, i)).ToList();
            var total = _buckets.Sum(b => b.Count);
            Summary = Format.Plural(total, "photo or video", "photos and videos");
            IsEmpty = total == 0;
            RebuildRows();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ImmichApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            Error = "Your API key doesn't allow viewing the library. Give it the asset.read and asset.view permissions, or sign in with your password or SSO.";
        }
        catch (Exception ex)
        {
            Log.Warn("Could not load the library", ex);
            Error = $"Couldn't load your library. {NetworkErrors.Describe(ex)}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Called by the view when its width or display scaling changes.</summary>
    public void SetLayout(double width, double scaling)
    {
        if (width <= 0)
            return;
        _width = width;
        var columns = Math.Max(2, (int)((width + Gap) / (TargetTile + Gap)));
        DecodeWidth = (int)Math.Ceiling(TileSize(columns) * Math.Max(1, scaling));
        if (columns != _columns)
        {
            _columns = columns;
            RebuildRows();
        }
    }

    double TileSize(int columns) => Math.Floor((_width - Gap * (columns - 1)) / columns);

    void RebuildRows()
    {
        if (_columns == 0 || _buckets.Count == 0)
        {
            Rows = [];
            return;
        }

        var tile = TileSize(_columns);
        var rows = new List<object>();
        foreach (var bucket in _buckets)
        {
            rows.Add(new MonthHeaderViewModel(bucket.Label, Format.Plural(bucket.Count, "item")));
            for (var start = 0; start < bucket.Count; start += _columns)
                rows.Add(new AssetRowViewModel(this, bucket, start, Math.Min(_columns, bucket.Count - start), tile));
        }
        Rows = new ObservableCollection<object>(rows);
    }

    internal async Task<IReadOnlyList<TimelineAsset>> GetAssetsAsync(BucketState bucket, CancellationToken ct)
    {
        if (Client is not { } client)
            return [];
        return await bucket.GetAssetsAsync(client, ct);
    }

    // ---- Viewer ----

    public void OpenViewer(AssetCellViewModel cell)
    {
        if (cell.Asset is null)
            return;
        if (_pick is not null)
        {
            if (cell.Asset.IsImage)
                _pick(cell);
            return;
        }
        Viewer = new PhotoViewerViewModel(this, cell.Bucket, cell.Index, cell.Asset, cell.Thumbnail);
    }

    internal void CloseViewer() => Viewer = null;

    /// <summary>Finds the asset before/after a position, crossing into neighbouring months.</summary>
    internal async Task<(BucketState Bucket, int Index, TimelineAsset Asset)?> StepAsync(BucketState bucket, int index, int delta, CancellationToken ct)
    {
        var assets = await GetAssetsAsync(bucket, ct);
        var next = index + delta;
        if (next >= 0 && next < assets.Count)
            return (bucket, next, assets[next]);

        var neighbour = bucket.Position + delta;
        while (neighbour >= 0 && neighbour < _buckets.Count)
        {
            var other = _buckets[neighbour];
            var otherAssets = await GetAssetsAsync(other, ct);
            if (otherAssets.Count > 0)
            {
                var i = delta > 0 ? 0 : otherAssets.Count - 1;
                return (other, i, otherAssets[i]);
            }
            neighbour += delta;
        }
        return null;
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        Thumbnails?.Dispose();
        foreach (var row in Rows.OfType<AssetRowViewModel>())
            row.Unrealize();
    }
}

/// <summary>One month of the timeline; its assets are fetched on first need and kept.</summary>
public sealed class BucketState(TimeBucket bucket, int position)
{
    Task<IReadOnlyList<TimelineAsset>>? _assets;

    public string Key { get; } = bucket.Key;
    public int Count { get; } = bucket.Count;
    public int Position { get; } = position;

    public string Label { get; } = DateTime.TryParse(bucket.Key, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var date)
        ? date.ToString(date.Year == DateTime.Now.Year ? "MMMM" : "MMMM yyyy", CultureInfo.CurrentCulture)
        : bucket.Key;

    public Task<IReadOnlyList<TimelineAsset>> GetAssetsAsync(ImmichClient client, CancellationToken ct)
    {
        // Shared by every row of the month; a failed load is retried next time.
        if (_assets is null || _assets.IsFaulted || _assets.IsCanceled)
            _assets = LoadAsync(client);
        return _assets.WaitAsync(ct);
    }

    async Task<IReadOnlyList<TimelineAsset>> LoadAsync(ImmichClient client) =>
        await client.GetTimeBucketAsync(Key, CancellationToken.None);
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
            var assets = await _owner.GetAssetsAsync(Bucket, cts.Token);
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

    bool _bound;

    public bool IsMissing => _bound && Asset is null;
    public bool IsVideo => Asset is { IsImage: false };

    /// <summary>Videos can't be picked as a profile picture.</summary>
    public bool CanOpen => !(owner.IsPicker && IsVideo);
    public bool IsFavorite => Asset?.IsFavorite == true;
    public string DurationText => Asset?.DurationMs is { } ms and > 0 ? FormatDuration(ms) : "";

    internal void Bind(TimelineAsset? asset)
    {
        _bound = true;
        Asset = asset;
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

    [RelayCommand]
    void Open() => owner.OpenViewer(this);

    internal static string FormatDuration(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}

/// <summary>Full-window viewer with previous/next across the whole timeline.</summary>
public sealed partial class PhotoViewerViewModel : ViewModelBase
{
    readonly PhotosViewModel _owner;
    CancellationTokenSource? _cts;
    BucketState _bucket;
    int _index;
    Bitmap? _ownedPreview; // the viewer's own sharp image; placeholders belong to the grid

    public PhotoViewerViewModel(PhotosViewModel owner, BucketState bucket, int index, TimelineAsset asset, Bitmap? placeholder)
    {
        _owner = owner;
        _bucket = bucket;
        _index = index;
        Show(asset, placeholder);
    }

    [ObservableProperty]
    public partial TimelineAsset Asset { get; set; }

    [ObservableProperty]
    public partial Bitmap? Image { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string DateText { get; set; } = "";

    [ObservableProperty]
    public partial string? Error { get; set; }

    public bool IsVideo => !Asset.IsImage;
    public string DurationText => Asset.DurationMs is { } ms and > 0 ? AssetCellViewModel.FormatDuration(ms) : "";

    void Show(TimelineAsset asset, Bitmap? placeholder)
    {
        Asset = asset;
        OnPropertyChanged(nameof(IsVideo));
        OnPropertyChanged(nameof(DurationText));
        DateText = DateTimeOffset.TryParse(asset.FileCreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToLocalTime().ToString("dddd, MMMM d, yyyy · h:mm tt", CultureInfo.CurrentCulture)
            : "";
        Error = null;

        // Show the (already loaded) thumbnail instantly, then swap in the sharper preview.
        Image = placeholder;
        ReleasePreview();
        _ = LoadPreviewAsync(asset);
    }

    async Task LoadPreviewAsync(TimelineAsset asset)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        if (_owner.Thumbnails is not { } loader)
            return;
        IsLoading = true;
        try
        {
            var bitmap = await loader.LoadAsync(asset.Id, AssetImageSize.Preview, 2560, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(asset, Asset))
            {
                bitmap?.Dispose();
                return;
            }
            Image = bitmap;
            ReleasePreview();
            _ownedPreview = bitmap;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Error = $"Couldn't load this photo. {NetworkErrors.Describe(ex)}";
        }
        finally
        {
            if (ReferenceEquals(cts, _cts))
                IsLoading = false;
        }
    }

    [RelayCommand]
    Task Next() => StepAsync(+1);

    [RelayCommand]
    Task Previous() => StepAsync(-1);

    async Task StepAsync(int delta)
    {
        try
        {
            var result = await _owner.StepAsync(_bucket, _index, delta, CancellationToken.None);
            if (result is not { } r)
                return;
            _bucket = r.Bucket;
            _index = r.Index;
            Show(r.Asset, null);
        }
        catch (Exception ex)
        {
            Error = NetworkErrors.Describe(ex);
        }
    }

    void ReleasePreview()
    {
        var old = _ownedPreview;
        _ownedPreview = null;
        if (old is not null && !ReferenceEquals(old, Image))
            old.Dispose();
    }

    [RelayCommand]
    void Close()
    {
        _cts?.Cancel();
        _owner.CloseViewer();
        Image = null;
        ReleasePreview();
    }

    [RelayCommand]
    void OpenInImmich()
    {
        if (AppServices.Settings.Current.WebUrl is { } web)
            ShellLauncher.Open($"{web.TrimEnd('/')}/photos/{Asset.Id}");
    }
}
