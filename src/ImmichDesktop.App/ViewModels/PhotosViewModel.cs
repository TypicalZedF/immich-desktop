using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichDesktop.App.Controls;
using ImmichDesktop.App.Services;
using ImmichDesktop.Core;
using ImmichDesktop.Core.Api;

namespace ImmichDesktop.App.ViewModels;

public enum PhotosTab
{
    Photos,
    Favorites,
    Albums,
    People,
    Places,
    Map,
}

/// <summary>What the grid is showing: a title and how to load its groups.</summary>
public sealed record GridQuery(string Title, Func<ImmichClient, CancellationToken, Task<List<BucketState>>> Load, AlbumInfo? Album = null);

/// <summary>
/// Your Immich library, like the web app: the timeline, favorites, albums, people, places and a map,
/// plus search. Grids only build what's on screen: months load as their rows scroll into view and
/// thumbnails are released when they scroll away. Select photos to share, favorite, add to an album,
/// download or move to the trash.
/// </summary>
public sealed partial class PhotosViewModel : ViewModelBase, IDisposable
{
    public const double Gap = 4;

    /// <summary>Height of a month heading in the grid (fixed, so the date rail knows where months are).</summary>
    public const double HeaderHeight = 54;
    const double TargetTile = 168;
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
    bool _albumsLoaded, _peopleLoaded, _placesLoaded, _markersLoaded;

    public PhotosViewModel(ShellViewModel shell) : this(shell.Main, null)
    {
    }

    PhotosViewModel(MainWindowViewModel? main, Action<AssetCellViewModel>? pick)
    {
        _main = main;
        _pick = pick;
        if (Client is { } client)
            Thumbnails = new ThumbnailLoader(client);
        _ = LoadGridAsync();
    }

    /// <summary>The same library for choosing a photo (e.g. a profile picture): clicking a photo picks it instead of opening it.</summary>
    public static PhotosViewModel ForPicking(Action<AssetCellViewModel> pick) => new(null, pick);

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

    // ---- Tabs ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrid), nameof(ShowAlbums), nameof(ShowPeople), nameof(ShowPlaces), nameof(ShowMap),
        nameof(IsPhotosTab), nameof(IsFavoritesTab), nameof(IsAlbumsTab), nameof(IsPeopleTab), nameof(IsPlacesTab), nameof(IsMapTab), nameof(Heading))]
    public partial PhotosTab Tab { get; set; }

    public bool IsPhotosTab { get => Tab == PhotosTab.Photos; set { if (value) SelectTab(PhotosTab.Photos); } }
    public bool IsFavoritesTab { get => Tab == PhotosTab.Favorites; set { if (value) SelectTab(PhotosTab.Favorites); } }
    public bool IsAlbumsTab { get => Tab == PhotosTab.Albums; set { if (value) SelectTab(PhotosTab.Albums); } }
    public bool IsPeopleTab { get => Tab == PhotosTab.People; set { if (value) SelectTab(PhotosTab.People); } }
    public bool IsPlacesTab { get => Tab == PhotosTab.Places; set { if (value) SelectTab(PhotosTab.Places); } }
    public bool IsMapTab { get => Tab == PhotosTab.Map; set { if (value) SelectTab(PhotosTab.Map); } }

    void SelectTab(PhotosTab tab)
    {
        if (Tab == tab && Drill is null)
            return;
        Tab = tab;
        Drill = null;
        ClearSelection();
        switch (tab)
        {
            case PhotosTab.Photos or PhotosTab.Favorites:
                _ = LoadGridAsync();
                break;
            case PhotosTab.Albums when !_albumsLoaded:
                _ = LoadAlbumsAsync();
                break;
            case PhotosTab.People when !_peopleLoaded:
                _ = LoadPeopleAsync();
                break;
            case PhotosTab.Places when !_placesLoaded:
                _ = LoadPlacesAsync();
                break;
            case PhotosTab.Map when !_markersLoaded:
                _ = LoadMarkersAsync();
                break;
        }
    }

    /// <summary>An album, person, place, search or map area opened from a tab; Back returns to the tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrid), nameof(ShowAlbums), nameof(ShowPeople), nameof(ShowPlaces), nameof(ShowMap),
        nameof(IsDrilledIn), nameof(Heading), nameof(CanShareAlbum), nameof(ShowHeading))]
    public partial GridQuery? Drill { get; set; }

    public bool IsDrilledIn => Drill is not null;

    /// <summary>The profile picture dialog has its own title, so only show ours once you've opened something.</summary>
    public bool ShowHeading => !IsPicker || IsDrilledIn;
    public bool ShowGrid => Drill is not null || Tab is PhotosTab.Photos or PhotosTab.Favorites;
    public bool ShowAlbums => Drill is null && Tab == PhotosTab.Albums;
    public bool ShowPeople => Drill is null && Tab == PhotosTab.People;
    public bool ShowPlaces => Drill is null && Tab == PhotosTab.Places;
    public bool ShowMap => Drill is null && Tab == PhotosTab.Map;
    public bool CanShareAlbum => Drill?.Album is not null && !IsPicker;

    public string Heading => Drill?.Title ?? Tab switch
    {
        PhotosTab.Favorites => Tr.T("Favorites"),
        PhotosTab.Albums => Tr.T("Albums"),
        PhotosTab.People => Tr.T("People"),
        PhotosTab.Places => Tr.T("Places"),
        PhotosTab.Map => Tr.T("Map"),
        _ => Tr.T("Photos"),
    };

    [RelayCommand]
    void Back()
    {
        if (Drill is null)
            return;
        Drill = null;
        ClearSelection();
        ReleaseRows();
        if (Tab is PhotosTab.Photos or PhotosTab.Favorites)
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
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string EmptyText { get; set; } = "";

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial PhotoViewerViewModel? Viewer { get; set; }

    [RelayCommand]
    Task Refresh()
    {
        if (ShowGrid)
            return LoadGridAsync();
        return Tab switch
        {
            PhotosTab.Albums => LoadAlbumsAsync(),
            PhotosTab.People => LoadPeopleAsync(),
            PhotosTab.Places => LoadPlacesAsync(),
            PhotosTab.Map => LoadMarkersAsync(),
            _ => Task.CompletedTask,
        };
    }

    GridQuery CurrentQuery => Drill ?? (Tab == PhotosTab.Favorites
        ? new GridQuery(Tr.T("Favorites"), (c, ct) => TimelineAsync(c, new TimelineFilter(Favorites: true), ct))
        : new GridQuery(Tr.T("Photos"), (c, ct) => TimelineAsync(c, TimelineFilter.All, ct)));

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
            var buckets = await query.Load(client, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            _buckets = buckets;
            var total = _buckets.Sum(b => b.Count);
            Summary = Format.Plural(total, Tr.T("photo or video"), Tr.T("photos and videos"));
            IsEmpty = total == 0;
            EmptyText = Drill is not null ? Tr.T("Nothing here.")
                : Tab == PhotosTab.Favorites ? Tr.T("No favorites yet. Tap the heart on a photo to add it here.")
                : Tr.T("No photos yet. They'll appear here once they're backed up.");
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
            _rowOffsets = [0];
            Rows = [];
            Sections = [];
            return;
        }

        var tile = TileSize(_columns);
        var rows = new List<object>();
        var sections = new List<TimelineSection>();
        double offset = 0;
        foreach (var bucket in _buckets)
        {
            var rowCount = (bucket.Count + _columns - 1) / _columns;
            var height = HeaderHeight + rowCount * (tile + Gap);
            var longLabel = bucket.Month is { } month ? month.ToString("MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture) : bucket.Label;
            sections.Add(new TimelineSection(longLabel, bucket.Month?.Year, offset, height));
            offset += height;

            rows.Add(new MonthHeaderViewModel(bucket.Label, Format.Plural(bucket.Count, "item")));
            for (var start = 0; start < bucket.Count; start += _columns)
                rows.Add(new AssetRowViewModel(this, bucket, start, Math.Min(_columns, bucket.Count - start), tile));
        }
        _rowOffsets = new double[rows.Count + 1];
        for (var i = 0; i < rows.Count; i++)
            _rowOffsets[i + 1] = _rowOffsets[i] + (rows[i] is AssetRowViewModel row ? row.RowHeight : HeaderHeight);
        Rows = new ObservableCollection<object>(rows);
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

    // ---- Albums, people, places ----

    public ObservableCollection<AlbumCardViewModel> Albums { get; } = [];
    public ObservableCollection<PersonCardViewModel> People { get; } = [];
    public ObservableCollection<PlaceCardViewModel> Places { get; } = [];

    [ObservableProperty]
    public partial bool IsLoadingCollection { get; set; }

    [ObservableProperty]
    public partial string? CollectionMessage { get; set; }

    Task LoadAlbumsAsync() => LoadCollectionAsync(Albums, async c =>
    {
        _albumsLoaded = true;
        var albums = await c.GetAlbumsAsync(_lifetime.Token);
        return albums.OrderByDescending(a => a.AssetCount > 0).ThenBy(a => a.AlbumName, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => new AlbumCardViewModel(this, a)).ToList();
    }, Tr.T("No albums yet. Create one from selected photos with “Add to album”."));

    Task LoadPeopleAsync() => LoadCollectionAsync(People, async c =>
    {
        _peopleLoaded = true;
        var people = await c.GetPeopleAsync(_lifetime.Token);
        // Named people first, like the web app.
        return people.OrderByDescending(p => p.Name.Length > 0).Select(p => new PersonCardViewModel(this, p)).ToList();
    }, Tr.T("No people yet. Immich finds faces when machine learning is turned on in your server settings."));

    Task LoadPlacesAsync() => LoadCollectionAsync(Places, async c =>
    {
        _placesLoaded = true;
        var places = await c.GetPlacesAsync(_lifetime.Token);
        return places.Select(p => new PlaceCardViewModel(this, p)).ToList();
    }, Tr.T("No places yet. Photos with a location show up here."));

    async Task LoadCollectionAsync<T>(ObservableCollection<T> target, Func<ImmichClient, Task<List<T>>> load, string emptyText)
        where T : LibraryCardViewModel
    {
        if (Client is not { } client)
            return;
        IsLoadingCollection = true;
        CollectionMessage = null;
        try
        {
            var items = await load(client);
            foreach (var old in target)
                old.Dispose();
            target.Clear();
            foreach (var item in items)
                target.Add(item);
            if (items.Count == 0)
                CollectionMessage = emptyText;
            if (Thumbnails is { } loader)
                foreach (var item in items)
                    _ = item.LoadCoverAsync(loader, CardDecodeWidth, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("Could not load the library page", ex);
            CollectionMessage = Tr.T("Couldn't load this page. {0}", NetworkErrors.Describe(ex));
        }
        finally
        {
            IsLoadingCollection = false;
        }
    }

    internal void OpenAlbum(AlbumInfo album) =>
        OpenDrill(new GridQuery(album.AlbumName, (c, ct) => TimelineAsync(c, new TimelineFilter(AlbumId: album.Id), ct), album));

    internal void OpenPerson(PersonInfo person) =>
        OpenDrill(new GridQuery(person.Name.Length > 0 ? person.Name : Tr.T("Unnamed person"), (c, ct) => TimelineAsync(c, new TimelineFilter(PersonId: person.Id), ct)));

    internal void OpenPlace(PlaceInfo place) =>
        OpenDrill(new GridQuery(place.City, async (c, ct) => BucketState.FromAssets(await c.SearchAsync(null, place.City, 2000, ct))));

    internal void OnAlbumsChanged() => _albumsLoaded = false;

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
            _markersLoaded = true;
            Markers = await client.GetMapMarkersAsync(_lifetime.Token);
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

    /// <summary>Shows the photos of a map area (or one cluster) as a grid.</summary>
    public void ShowMapPhotos(IReadOnlyList<MapMarker> markers)
    {
        if (markers.Count == 0)
            return;
        var place = markers.Select(m => m.City).Where(c => !string.IsNullOrWhiteSpace(c)).GroupBy(c => c).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        var assets = markers.Select(m => new TimelineAsset(m.Id, true, false, 1, null, null)).ToList();
        OpenDrill(new GridQuery(place is null ? Tr.T("This area") : Tr.T("Around {0}", place),
            (_, _) => Task.FromResult(BucketState.FromAssets(assets, Format.Plural(assets.Count, "photo")))));
    }

    // ---- Selection ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelecting), nameof(SelectionText))]
    public partial int SelectionCount { get; set; }

    public bool IsSelecting => SelectionCount > 0;
    public string SelectionText => Tr.T("{0} selected", SelectionCount);

    internal bool IsAssetSelected(string id) => _selected.Contains(id);

    internal void ToggleSelection(AssetCellViewModel cell)
    {
        if (IsPicker || cell.Asset is not { } asset)
            return;
        if (!_selected.Remove(asset.Id))
            _selected.Add(asset.Id);
        cell.IsSelected = _selected.Contains(asset.Id);
        SelectionCount = _selected.Count;
    }

    [RelayCommand]
    void ClearSelection()
    {
        _selected.Clear();
        SelectionCount = 0;
        foreach (var cell in VisibleCells)
            cell.IsSelected = false;
    }

    IReadOnlyList<string> SelectedIds => _selected.ToList();

    [RelayCommand]
    void ShareSelection() => ShowShare(SelectedIds);

    [RelayCommand]
    void AddSelectionToAlbum() => ShowAlbumPicker(SelectedIds);

    [RelayCommand]
    async Task FavoriteSelectionAsync()
    {
        if (await SetFavoriteAsync(SelectedIds, true))
            ClearSelection();
    }

    [RelayCommand]
    Task DownloadSelectionAsync() => DownloadAsync(SelectedIds);

    [RelayCommand]
    async Task TrashSelectionAsync()
    {
        if (await TrashAsync(SelectedIds))
            ClearSelection();
    }

    // ---- Actions (selection and viewer) ----

    /// <summary>A small panel over the page: sharing or adding to an album.</summary>
    [ObservableProperty]
    public partial ViewModelBase? Panel { get; set; }

    internal void ClosePanel(ViewModelBase panel)
    {
        if (ReferenceEquals(Panel, panel))
            Panel = null;
    }

    internal void ShowShare(IReadOnlyList<string> assetIds) =>
        Panel = new SharePanelViewModel(this, Format.Plural(assetIds.Count, "photo"), assetIds, null);

    [RelayCommand]
    void ShareAlbum()
    {
        if (Drill?.Album is { } album)
            Panel = new SharePanelViewModel(this, Tr.T("the album “{0}”", album.AlbumName), null, album.Id);
    }

    internal void ShowAlbumPicker(IReadOnlyList<string> assetIds) => Panel = new AlbumPickerViewModel(this, assetIds);

    internal async Task<bool> SetFavoriteAsync(IReadOnlyList<string> ids, bool favorite)
    {
        if (Client is not { } client || ids.Count == 0)
            return false;
        try
        {
            await client.SetFavoriteAsync(ids, favorite, CancellationToken.None);
            var set = ids.ToHashSet();
            foreach (var bucket in _buckets)
                foreach (var id in set)
                    bucket.Replace(id, a => a with { IsFavorite = favorite });
            foreach (var cell in VisibleCells.Where(c => c.Asset is { } a && set.Contains(a.Id)))
                cell.Asset = cell.Asset! with { IsFavorite = favorite };
            if (ids.Count > 1)
                ShowNotice(favorite ? Tr.T("Added {0} to favorites", Format.Plural(ids.Count, "photo")) : Tr.T("Removed from favorites"));
            return true;
        }
        catch (Exception ex)
        {
            ShowNotice(Tr.T("Couldn't change favorites. {0}", NetworkErrors.Describe(ex)));
            return false;
        }
    }

    internal async Task DownloadAsync(IReadOnlyList<string> ids)
    {
        if (Client is not { } client || _main is null || ids.Count == 0)
            return;
        try
        {
            if (ids.Count == 1)
            {
                var details = await client.GetAssetAsync(ids[0], CancellationToken.None);
                var path = await _main.PickSaveFileAsync(Tr.T("Save photo"), details.OriginalFileName ?? $"{ids[0]}.jpg");
                if (path is null)
                    return;
                await using (var file = File.Create(path))
                    await client.DownloadOriginalAsync(ids[0], file, CancellationToken.None);
                ShowNotice(Tr.T("Saved {0}", Path.GetFileName(path)));
                return;
            }

            var folder = await _main.PickFolderAsync(Tr.T("Save {0} photos to…", ids.Count));
            if (folder is null)
                return;
            var done = 0;
            foreach (var id in ids)
            {
                ShowNotice(Tr.T("Downloading {0} of {1}…", done + 1, ids.Count), sticky: true);
                var details = await client.GetAssetAsync(id, CancellationToken.None);
                var path = UniquePath(folder, details.OriginalFileName ?? $"{id}.jpg");
                await using (var file = File.Create(path))
                    await client.DownloadOriginalAsync(id, file, CancellationToken.None);
                done++;
            }
            ShowNotice(Tr.T("Saved {0} to {1}", Format.Plural(done, "photo"), Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar))));
        }
        catch (Exception ex)
        {
            Log.Warn("Download failed", ex);
            ShowNotice(Tr.T("Couldn't download. {0}", NetworkErrors.Describe(ex)));
        }
    }

    static string UniquePath(string folder, string name)
    {
        name = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(folder, name);
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
        return path;
    }

    internal async Task<bool> TrashAsync(IReadOnlyList<string> ids)
    {
        if (Client is not { } client || _main is null || ids.Count == 0)
            return false;
        var what = Format.Plural(ids.Count, "photo");
        if (!await _main.ConfirmAsync(Tr.T("Move {0} to the trash?", what),
                Tr.T("They're removed from your library on every device. You can restore them from the trash in Immich for a while."),
                Tr.T("Move to trash"), danger: true))
            return false;
        try
        {
            await client.TrashAssetsAsync(ids, CancellationToken.None);
            ShowNotice(Tr.T("Moved {0} to the trash", what));
            _ = LoadGridAsync();
            return true;
        }
        catch (Exception ex)
        {
            ShowNotice(Tr.T("Couldn't move to the trash. {0}", NetworkErrors.Describe(ex)));
            return false;
        }
    }

    internal Task CopyTextAsync(string text) => _main?.CopyTextAsync(text) ?? Task.CompletedTask;

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
        Thumbnails?.Dispose();
    }
}
