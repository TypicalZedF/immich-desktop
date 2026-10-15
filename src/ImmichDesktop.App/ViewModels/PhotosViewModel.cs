using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichDesktop.App.Controls;
using ImmichDesktop.App.Services;
using ImmichDesktop.Core;
using ImmichDesktop.Core.Api;

namespace ImmichDesktop.App.ViewModels;

/// <summary>The library sections in the sidebar (like the Immich web app's, with the mobile app's Library group).</summary>
public enum LibraryPage
{
    Timeline,
    Explore,
    Map,
    Sharing,
    Albums,
    Favorites,
    Archive,
    Locked,
    Trash,
    Utilities,
}

/// <summary>What the grid is showing: a title and how to load its groups.</summary>
/// <param name="ShowSizes">Tiles show each file's size (Large files).</param>
/// <param name="OffersArchiveAll">An "Archive all" button in the header (screenshots utility).</param>
public sealed record GridQuery(string Title, Func<ImmichClient, CancellationToken, Task<List<BucketState>>> Load, AlbumInfo? Album = null,
    bool ShowSizes = false, bool OffersArchiveAll = false);

/// <summary>
/// One section of your Immich library (see <see cref="LibraryPage"/>): the timeline, Explore (people
/// and places), the map, sharing, albums, favorites, the archive, the locked folder or the trash, plus
/// search results and anything opened from them. Grids only build what's on screen: months load as
/// their rows scroll into view and thumbnails are released when they scroll away. Select photos to act
/// on them (what's offered depends on the section).
/// </summary>
public sealed partial class PhotosViewModel : ViewModelBase, IDisposable
{
    public const double Gap = 4;

    /// <summary>Height of a month heading in the grid (fixed, so the date rail knows where months are).</summary>
    public const double HeaderHeight = 54;
    public const double MinTile = 80, MaxTile = 360;
    double _targetTile = Math.Clamp(AppServices.Settings.Current.ThumbnailSize, MinTile, MaxTile);
    double _scaling = 1;
    const int CardDecodeWidth = 360;

    readonly CancellationTokenSource _lifetime = new();
    readonly MainWindowViewModel? _main;
    readonly Action<AssetCellViewModel>? _pick;
    readonly HashSet<string> _selected = [];
    CancellationTokenSource? _gridLoad;
    CancellationTokenSource? _noticeTimer;
    List<BucketState> _buckets = [];
    int _columns;
    double _width;

    public PhotosViewModel(ShellViewModel shell, LibraryPage page) : this(shell.Main, null, page)
    {
    }

    PhotosViewModel(MainWindowViewModel? main, Action<AssetCellViewModel>? pick, LibraryPage page)
    {
        _main = main;
        _pick = pick;
        Page = page;
        if (Client is { } client)
            Thumbnails = new ThumbnailLoader(client);
        _ = LoadPageAsync();
    }

    /// <summary>The same library for choosing a photo (e.g. a profile picture): clicking a photo picks it instead of opening it.</summary>
    public static PhotosViewModel ForPicking(Action<AssetCellViewModel> pick) => new(null, pick, LibraryPage.Timeline);

    public bool IsPicker => _pick is not null;
    public bool IsNotPicker => !IsPicker;

    internal ImmichClient? Client => AppServices.Account.Client;
    internal ThumbnailLoader? Thumbnails { get; private set; }

    /// <summary>Pixels to decode thumbnails at (tile size × display scaling).</summary>
    internal int DecodeWidth { get; private set; } = 256;

    /// <summary>Long side, in pixels, the viewer needs to look sharp (set by the view from its size and scaling).</summary>
    internal int ViewerPixels { get; set; } = 2048;

    /// <summary>False once the server turns out not to offer full-size images (older versions).</summary>
    internal bool FullsizeSupported { get; set; } = true;

    // ---- Page ----

    public LibraryPage Page { get; }

    public bool IsTrash => Page == LibraryPage.Trash && Drill is null;
    public bool IsArchive => Page == LibraryPage.Archive && Drill is null;
    public bool IsLockedFolder => Page == LibraryPage.Locked;

    Task LoadPageAsync() => Page switch
    {
        LibraryPage.Explore => Task.WhenAll(LoadPeopleAsync(), LoadPlacesAsync()),
        LibraryPage.Map => LoadMarkersAsync(),
        LibraryPage.Sharing => LoadSharingAsync(),
        LibraryPage.Albums => LoadAlbumsAsync(),
        LibraryPage.Locked => CheckLockedAsync(),
        LibraryPage.Utilities => LoadUtilitiesAsync(),
        _ => LoadGridAsync(),
    };

    /// <summary>An album, person, place, search or map area opened from the page; Back returns to the page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrid), nameof(ShowAlbums), nameof(ShowExplore), nameof(ShowMap), nameof(ShowSharing),
        nameof(ShowLockedPrompt), nameof(IsDrilledIn), nameof(Heading), nameof(CanShareAlbum), nameof(CanDeleteAlbum), nameof(ShowHeading),
        nameof(IsTrash), nameof(IsArchive), nameof(ShowTrashActions), nameof(ShowLockAction), nameof(ShowUtilities), nameof(ShowArchiveAll))]
    public partial GridQuery? Drill { get; set; }

    public bool IsDrilledIn => Drill is not null || Duplicates is not null || Privacy is not null;

    /// <summary>The profile picture dialog has its own title, so only show ours once you've opened something.</summary>
    public bool ShowHeading => !IsPicker || IsDrilledIn;
    public bool ShowGrid => Drill is not null
        || Page is LibraryPage.Timeline or LibraryPage.Favorites or LibraryPage.Archive or LibraryPage.Trash
        || (Page == LibraryPage.Locked && IsUnlocked);
    public bool ShowAlbums => Drill is null && Page == LibraryPage.Albums;
    public bool ShowExplore => Drill is null && Page == LibraryPage.Explore;
    public bool ShowMap => Drill is null && Page == LibraryPage.Map;
    public bool ShowSharing => Drill is null && Page == LibraryPage.Sharing;
    public bool ShowLockedPrompt => Drill is null && Page == LibraryPage.Locked && !IsUnlocked;
    public bool ShowTrashActions => IsTrash && !IsEmpty && !IsLoading;
    public bool ShowArchiveAll => Drill?.OffersArchiveAll == true && !IsEmpty && !IsLoading;
    public bool ShowLockAction => Page == LibraryPage.Locked && IsUnlocked && Drill is null;
    public bool CanShareAlbum => Drill?.Album is not null && !IsPicker;
    public bool CanDeleteAlbum => Drill?.Album is { } album && !IsPicker && album.IsOwnedBy(AppServices.Settings.Current.UserId);

    public string Heading => Drill?.Title ?? (Duplicates is not null ? Tr.T("Review duplicates") : null)
        ?? (Privacy is not null ? Tr.T("Suggest for the locked folder") : null) ?? Page switch
    {
        LibraryPage.Utilities => Tr.T("Utilities"),
        LibraryPage.Explore => Tr.T("Explore"),
        LibraryPage.Map => Tr.T("Map"),
        LibraryPage.Sharing => Tr.T("Sharing"),
        LibraryPage.Albums => Tr.T("Albums"),
        LibraryPage.Favorites => Tr.T("Favorites"),
        LibraryPage.Archive => Tr.T("Archive"),
        LibraryPage.Locked => Tr.T("Locked folder"),
        LibraryPage.Trash => Tr.T("Trash"),
        _ => Tr.T("Photos"),
    };

    /// <summary>Back on this page after visiting others: refresh lists that other pages can change.</summary>
    internal void OnShownAgain()
    {
        if (Drill is not null || Duplicates is not null || Privacy is not null)
            return;
        _ = Page switch
        {
            LibraryPage.Sharing => LoadSharingAsync(),
            LibraryPage.Albums => LoadAlbumsAsync(),
            LibraryPage.Utilities => LoadUtilitiesAsync(),
            _ => Task.CompletedTask,
        };
    }

    /// <summary>Out of whatever is open (search results, an album, a review…) to the page itself.</summary>
    internal void ReturnToStart()
    {
        if (Viewer is not null)
            return; // the photo viewer covers the page; it has its own close
        for (var i = 0; i < 4 && (Drill is not null || Duplicates is not null || Privacy is not null); i++)
            Back();
    }

    [RelayCommand]
    void Back()
    {
        if (Duplicates is { } duplicates)
        {
            Duplicates = null;
            duplicates.Dispose();
            _ = LoadUtilitiesAsync();
            return;
        }
        if (Privacy is { } privacy)
        {
            Privacy = null;
            privacy.Dispose();
            _ = LoadUtilitiesAsync();
            return;
        }
        if (Drill is null)
            return;
        Drill = null;
        ClearSelection();
        ReleaseRows();
        if (ShowGrid)
            _ = LoadGridAsync();
    }

    void OpenDrill(GridQuery query)
    {
        ClearSelection();
        Drill = query;
        _ = LoadGridAsync();
    }

    // ---- Grid ----

    [ObservableProperty]
    public partial ObservableCollection<object> Rows { get; set; } = [];

    /// <summary>Where each month starts in the grid, for the date rail.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSections))]
    public partial IReadOnlyList<TimelineSection> Sections { get; set; } = [];

    public bool HasSections => Sections.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTrashActions), nameof(ShowArchiveAll))]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTrashActions), nameof(ShowArchiveAll))]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string EmptyText { get; set; } = "";

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial PhotoViewerViewModel? Viewer { get; set; }

    [RelayCommand]
    Task Refresh() => Duplicates is { } duplicates ? duplicates.RefreshCommand.ExecuteAsync(null)
        : ShowGrid ? LoadGridAsync() : LoadPageAsync();

    GridQuery CurrentQuery => Drill ?? Page switch
    {
        LibraryPage.Favorites => new GridQuery(Tr.T("Favorites"), (c, ct) => TimelineAsync(c, new TimelineFilter(Favorites: true), ct)),
        LibraryPage.Archive => new GridQuery(Tr.T("Archive"), (c, ct) => TimelineAsync(c, new TimelineFilter(Visibility: "archive"), ct)),
        LibraryPage.Locked => new GridQuery(Tr.T("Locked folder"), (c, ct) => TimelineAsync(c, new TimelineFilter(Visibility: "locked"), ct)),
        LibraryPage.Trash => new GridQuery(Tr.T("Trash"), (c, ct) => TimelineAsync(c, new TimelineFilter(Trashed: true), ct)),
        _ => new GridQuery(Tr.T("Photos"), (c, ct) => IsPicker ? TimelineAsync(c, TimelineFilter.All, ct) : TimelineWithLocalAsync(c, ct)),
    };

    // ---- Thumbnail size (the slider, or Ctrl + scroll) ----

    /// <summary>The photo the grid's top row starts with (asked of the view, to keep your place when resizing).</summary>
    internal Func<(BucketState Bucket, int Index)?>? GetTopAnchor { get; set; }

    /// <summary>Asks the view to scroll so this row is at the top.</summary>
    public event Action<int>? ScrollToRowRequested;

    CancellationTokenSource? _saveTileSize;

    /// <summary>How big thumbnails are, in pixels (remembered).</summary>
    public double ThumbnailSize
    {
        get => _targetTile;
        set
        {
            value = Math.Clamp(value, MinTile, MaxTile);
            if (Math.Abs(value - _targetTile) < 0.5)
                return;
            var anchor = GetTopAnchor?.Invoke();
            _targetTile = value;
            OnPropertyChanged();
            var columnsBefore = _columns;
            if (_width > 0)
                SetLayout(_width, _scaling);
            if (anchor is { } a && _columns != columnsBefore)
            {
                var index = Rows.ToList().FindIndex(r => r is AssetRowViewModel row && row.Bucket == a.Bucket && a.Index >= row.Start && a.Index < row.Start + row.Cells.Count);
                if (index >= 0)
                    ScrollToRowRequested?.Invoke(index);
            }
            // Save once the slider settles, not on every step.
            _saveTileSize?.Cancel();
            var cts = _saveTileSize = new CancellationTokenSource();
            var size = (int)value;
            _ = Task.Delay(500, cts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled)
                    AppServices.Settings.Update(s => s.ThumbnailSize = size);
            }, TaskScheduler.Default);
        }
    }

    /// <summary>Called by the view when its width or display scaling changes.</summary>
    public void SetLayout(double width, double scaling)
    {
        if (width <= 0)
            return;
        _width = width;
        _scaling = scaling;
        var columns = Math.Max(2, (int)((width + Gap) / (_targetTile + Gap)));
        var tile = TileSize(columns);
        DecodeWidth = (int)Math.Ceiling(tile * Math.Max(1, scaling));
        if (columns != _columns)
        {
            _columns = columns;
            _builtTile = tile;
            RebuildRows();
        }
        else if (tile != _builtTile)
        {
            // Same columns, different width (the window was resized, or the date rail appeared):
            // resize the tiles in place so the scroll position and loaded thumbnails stay.
            _builtTile = tile;
            foreach (var row in Rows.OfType<AssetRowViewModel>())
                row.Resize(tile);
            UpdateOffsets(Rows);
        }
    }

    double _builtTile;

    double TileSize(int columns) => Math.Floor((_width - Gap * (columns - 1)) / columns);

    void RebuildRows()
    {
        if (_columns == 0 || _buckets.Count == 0)
        {
            _rowOffsets = [0];
            Rows = [];
            Sections = [];
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
        if (ShowsMemories && _memoryCards.Count > 0)
            rows.Insert(0, new MemoriesRowViewModel(_memoryCards));
        UpdateOffsets(rows);
        Rows = new ObservableCollection<object>(rows);
    }

    /// <summary>Works out where every row and month starts (for the date rail).</summary>
    void UpdateOffsets(IReadOnlyList<object> rows)
    {
        var tile = TileSize(_columns);
        var sections = new List<TimelineSection>();
        double offset = rows.Count > 0 && rows[0] is MemoriesRowViewModel ? MemoriesHeight : 0;
        foreach (var bucket in _buckets)
        {
            var rowCount = (bucket.Count + _columns - 1) / _columns;
            var height = HeaderHeight + rowCount * (tile + Gap);
            var shortLabel = bucket.Day is null && bucket.Month is { } month ? month.ToString("MMM yyyy", System.Globalization.CultureInfo.CurrentCulture) : bucket.Label;
            sections.Add(new TimelineSection(shortLabel, bucket.Month?.Year, offset, height));
            offset += height;
        }
        _rowOffsets = new double[rows.Count + 1];
        for (var i = 0; i < rows.Count; i++)
            _rowOffsets[i + 1] = _rowOffsets[i] + rows[i] switch
            {
                AssetRowViewModel row => row.RowHeight,
                MemoriesRowViewModel => MemoriesHeight,
                _ => HeaderHeight,
            };
        // The rail is for the timeline; one big group (e.g. search results) has no dates to show.
        Sections = sections.Count > 1 && sections.Any(s => s.Year is not null) ? sections : [];
    }

    // Exact positions of the grid's rows. The list only estimates the height of rows it hasn't drawn,
    // so the date rail works from these instead of the scrollbar.
    double[] _rowOffsets = [0];

    /// <summary>Total height of the grid's rows.</summary>
    public double ContentHeight => _rowOffsets[^1];

    /// <summary>Where row <paramref name="index"/> starts.</summary>
    public double RowOffset(int index) => _rowOffsets[Math.Clamp(index, 0, _rowOffsets.Length - 1)];

    /// <summary>The row at a position (and how far into it the position is).</summary>
    public (int Index, double Within) RowAt(double offset)
    {
        if (_rowOffsets.Length < 2)
            return (0, 0);
        var i = Array.BinarySearch(_rowOffsets, offset);
        if (i < 0)
            i = ~i - 1;
        i = Math.Clamp(i, 0, _rowOffsets.Length - 2);
        return (i, offset - _rowOffsets[i]);
    }

    void ReleaseRows()
    {
        foreach (var row in Rows.OfType<AssetRowViewModel>())
            row.Unrealize();
        _rowOffsets = [0];
        Rows = [];
        Sections = [];
        _buckets = [];
    }

    IEnumerable<AssetCellViewModel> VisibleCells => Rows.OfType<AssetRowViewModel>().SelectMany(r => r.Cells);

    // ---- Search ----

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    /// <summary>Searches from outside (the top bar).</summary>
    public void SearchFor(string text)
    {
        SearchText = text;
        Search();
    }

    [RelayCommand]
    void Search()
    {
        var text = SearchText.Trim();
        if (text.Length == 0)
            return;
        OpenDrill(new GridQuery($"“{text}”", async (client, ct) =>
        {
            try
            {
                // Smart search finds "dog on the beach" (needs machine learning on the server)…
                var results = await client.SmartSearchAsync(text, 250, ct);
                return BucketState.FromAssets(results, Tr.T("Best matches"));
            }
            catch (ImmichApiException ex) when (ex.StatusCode is not System.Net.HttpStatusCode.Unauthorized)
            {
                // …and without it, fall back to file names.
                Log.Info($"Smart search unavailable ({ex.Message}); searching file names");
                return BucketState.FromAssets(await client.SearchAsync(text, null, 1000, ct));
            }
        }));
    }

    /// <summary>Photos whose file name contains the text (e.g. "IMG_4410").</summary>
    public void SearchFileName(string text) =>
        OpenDrill(new GridQuery(Tr.T("File names with “{0}”", text), async (client, ct) => BucketState.FromAssets(await client.SearchAsync(text, null, 2000, ct))));

    // ---- Map ----

    [ObservableProperty]
    public partial IReadOnlyList<MapMarker> Markers { get; set; } = [];

    /// <summary>Online street detail over the built-in map (remembered; off by default for privacy).</summary>
    public bool ShowStreetDetail
    {
        get => AppServices.Settings.Current.MapStreetDetail;
        set
        {
            if (value == ShowStreetDetail)
                return;
            AppServices.Settings.Update(s => s.MapStreetDetail = value);
            OnPropertyChanged();
        }
    }

    async Task LoadMarkersAsync()
    {
        if (Client is not { } client)
            return;
        IsLoadingCollection = true;
        CollectionMessage = null;
        try
        {
            Markers = await client.GetMapMarkersAsync(Core.Settings.MapSettings.Filter(AppServices.Settings.Current, DateTimeOffset.Now), _lifetime.Token);
            if (Markers.Count == 0)
                CollectionMessage = Tr.T("No photos with a location yet.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("Could not load the map", ex);
            CollectionMessage = Tr.T("Couldn't load the map. {0}", NetworkErrors.Describe(ex));
        }
        finally
        {
            IsLoadingCollection = false;
        }
    }

    // ---- Notices ----

    [ObservableProperty]
    public partial string? Notice { get; set; }

    internal async void ShowNotice(string text, bool sticky = false)
    {
        _noticeTimer?.Cancel();
        Notice = text;
        if (sticky)
            return;
        var cts = _noticeTimer = new CancellationTokenSource();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), cts.Token);
            Notice = null;
        }
        catch (OperationCanceledException)
        {
        }
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

    /// <summary>Opens the photos of a stack (RAW+JPEG, a burst…) as their own page.</summary>
    internal void OpenStack(TimelineAsset asset)
    {
        if (asset.StackId is not { } id)
            return;
        OpenDrill(new GridQuery(Tr.T("Stack of {0}", asset.StackCount),
            async (client, ct) => BucketState.FromAssets(await client.GetStackAsync(id, ct))));
    }

    /// <summary>Finds the asset before/after a position, crossing into neighbouring groups.</summary>
    internal async Task<(BucketState Bucket, int Index, TimelineAsset Asset)?> StepAsync(BucketState bucket, int index, int delta, CancellationToken ct)
    {
        var assets = await bucket.GetAssetsAsync(ct);
        var next = index + delta;
        if (next >= 0 && next < assets.Count)
            return (bucket, next, assets[next]);

        var neighbour = bucket.Position + delta;
        while (neighbour >= 0 && neighbour < _buckets.Count)
        {
            var other = _buckets[neighbour];
            var otherAssets = await other.GetAssetsAsync(ct);
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
        _noticeTimer?.Cancel();
        ReleaseRows();
        foreach (var card in Albums.Cast<LibraryCardViewModel>().Concat(People).Concat(Places))
            card.Dispose();
        Duplicates?.Dispose();
        foreach (var card in _memoryCards)
            card.Dispose();
        Privacy?.Dispose();
        if (_privacyHooked)
            AppServices.PrivacyScan.StatusChanged -= OnPrivacyStatus;
        Thumbnails?.Dispose();
    }
}
