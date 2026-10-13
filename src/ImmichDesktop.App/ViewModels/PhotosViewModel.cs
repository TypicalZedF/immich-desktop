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
}

/// <summary>What the grid is showing: a title and how to load its groups.</summary>
public sealed record GridQuery(string Title, Func<ImmichClient, CancellationToken, Task<List<BucketState>>> Load, AlbumInfo? Album = null);

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
        _ => LoadGridAsync(),
    };

    /// <summary>An album, person, place, search or map area opened from the page; Back returns to the page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrid), nameof(ShowAlbums), nameof(ShowExplore), nameof(ShowMap), nameof(ShowSharing),
        nameof(ShowLockedPrompt), nameof(IsDrilledIn), nameof(Heading), nameof(CanShareAlbum), nameof(ShowHeading),
        nameof(IsTrash), nameof(IsArchive), nameof(ShowTrashActions), nameof(ShowLockAction))]
    public partial GridQuery? Drill { get; set; }

    public bool IsDrilledIn => Drill is not null;

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
    public bool ShowLockAction => Page == LibraryPage.Locked && IsUnlocked && Drill is null;
    public bool CanShareAlbum => Drill?.Album is not null && !IsPicker;

    public string Heading => Drill?.Title ?? Page switch
    {
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

    [RelayCommand]
    void Back()
    {
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
    [NotifyPropertyChangedFor(nameof(ShowTrashActions))]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTrashActions))]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string EmptyText { get; set; } = "";

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial PhotoViewerViewModel? Viewer { get; set; }

    [RelayCommand]
    Task Refresh() => ShowGrid ? LoadGridAsync() : LoadPageAsync();

    GridQuery CurrentQuery => Drill ?? Page switch
    {
        LibraryPage.Favorites => new GridQuery(Tr.T("Favorites"), (c, ct) => TimelineAsync(c, new TimelineFilter(Favorites: true), ct)),
        LibraryPage.Archive => new GridQuery(Tr.T("Archive"), (c, ct) => TimelineAsync(c, new TimelineFilter(Visibility: "archive"), ct)),
        LibraryPage.Locked => new GridQuery(Tr.T("Locked folder"), (c, ct) => TimelineAsync(c, new TimelineFilter(Visibility: "locked"), ct)),
        LibraryPage.Trash => new GridQuery(Tr.T("Trash"), (c, ct) => TimelineAsync(c, new TimelineFilter(Trashed: true), ct)),
        _ => new GridQuery(Tr.T("Photos"), (c, ct) => TimelineAsync(c, TimelineFilter.All, ct)),
    };

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
            var shortLabel = bucket.Month is { } month ? month.ToString("MMM yyyy", System.Globalization.CultureInfo.CurrentCulture) : bucket.Label;
            sections.Add(new TimelineSection(shortLabel, bucket.Month?.Year, offset, height));
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

    // ---- Albums, Explore (people and places), Sharing ----

    /// <summary>How many people and places Explore shows before "Show all".</summary>
    const int ExplorePreview = 12;

    public ObservableCollection<AlbumCardViewModel> Albums { get; } = [];
    public ObservableCollection<PersonCardViewModel> People { get; } = [];
    public ObservableCollection<PlaceCardViewModel> Places { get; } = [];
    readonly List<PersonCardViewModel> _allPeople = [];
    readonly List<PlaceCardViewModel> _allPlaces = [];

    [ObservableProperty]
    public partial bool IsLoadingCollection { get; set; }

    [ObservableProperty]
    public partial string? CollectionMessage { get; set; }

    [ObservableProperty]
    public partial string? PeopleMessage { get; set; }

    [ObservableProperty]
    public partial string? PlacesMessage { get; set; }

    [ObservableProperty]
    public partial bool HasMorePeople { get; set; }

    [ObservableProperty]
    public partial bool HasMorePlaces { get; set; }

    Task LoadAlbumsAsync() => LoadCardsAsync(async c =>
    {
        var albums = await c.GetAlbumsAsync(_lifetime.Token);
        return albums.OrderByDescending(a => a.AssetCount > 0).ThenBy(a => a.AlbumName, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => new AlbumCardViewModel(this, a)).ToList();
    }, Albums, null, Tr.T("No albums yet. Create one from selected photos with “Add to album”."), m => CollectionMessage = m);

    Task LoadPeopleAsync() => LoadCardsAsync(async c =>
    {
        var people = await c.GetPeopleAsync(_lifetime.Token);
        // Named people first, like the web app.
        return people.OrderByDescending(p => p.Name.Length > 0).Select(p => new PersonCardViewModel(this, p)).ToList();
    }, People, _allPeople, Tr.T("No people yet. Immich finds faces when machine learning is turned on in your server settings."),
        m => PeopleMessage = m, more => HasMorePeople = more);

    Task LoadPlacesAsync() => LoadCardsAsync(async c =>
    {
        var places = await c.GetPlacesAsync(_lifetime.Token);
        return places.Select(p => new PlaceCardViewModel(this, p)).ToList();
    }, Places, _allPlaces, Tr.T("No places yet. Photos with a location show up here."),
        m => PlacesMessage = m, more => HasMorePlaces = more);

    /// <summary>
    /// Loads cards into <paramref name="shown"/>. With <paramref name="all"/>, only a preview is shown
    /// (Explore) and the rest waits for "Show all"; covers load only for cards on screen.
    /// </summary>
    async Task LoadCardsAsync<T>(Func<ImmichClient, Task<List<T>>> load, ObservableCollection<T> shown, List<T>? all,
        string emptyText, Action<string?> setMessage, Action<bool>? setHasMore = null)
        where T : LibraryCardViewModel
    {
        if (Client is not { } client)
            return;
        IsLoadingCollection = true;
        setMessage(null);
        try
        {
            var items = await load(client);
            foreach (var old in shown.Concat(all ?? []).Distinct())
                old.Dispose();
            shown.Clear();
            all?.Clear();
            all?.AddRange(items);
            var preview = all is null ? items : items.Take(ExplorePreview).ToList();
            foreach (var item in preview)
                shown.Add(item);
            setHasMore?.Invoke(items.Count > preview.Count);
            if (items.Count == 0)
                setMessage(emptyText);
            LoadCovers(preview);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("Could not load the library page", ex);
            setMessage(Tr.T("Couldn't load this page. {0}", NetworkErrors.Describe(ex)));
        }
        finally
        {
            IsLoadingCollection = false;
        }
    }

    void LoadCovers(IEnumerable<LibraryCardViewModel> cards)
    {
        if (Thumbnails is not { } loader)
            return;
        foreach (var card in cards)
            _ = card.LoadCoverAsync(loader, CardDecodeWidth, _lifetime.Token);
    }

    [RelayCommand]
    void ShowAllPeople() => ShowAll(_allPeople, People, more => HasMorePeople = more);

    [RelayCommand]
    void ShowAllPlaces() => ShowAll(_allPlaces, Places, more => HasMorePlaces = more);

    void ShowAll<T>(List<T> all, ObservableCollection<T> shown, Action<bool> setHasMore) where T : LibraryCardViewModel
    {
        var rest = all.Skip(shown.Count).ToList();
        foreach (var item in rest)
            shown.Add(item);
        setHasMore(false);
        LoadCovers(rest);
    }

    internal void OpenAlbum(AlbumInfo album) =>
        OpenDrill(new GridQuery(album.AlbumName, (c, ct) => TimelineAsync(c, new TimelineFilter(AlbumId: album.Id), ct), album));

    internal void OpenPerson(PersonInfo person) =>
        OpenDrill(new GridQuery(person.Name.Length > 0 ? person.Name : Tr.T("Unnamed person"), (c, ct) => TimelineAsync(c, new TimelineFilter(PersonId: person.Id), ct)));

    internal void OpenPlace(PlaceInfo place) =>
        OpenDrill(new GridQuery(place.City, async (c, ct) => BucketState.FromAssets(await c.SearchAsync(null, place.City, 2000, ct))));

    internal void OpenPartner(PartnerInfo partner) =>
        OpenDrill(new GridQuery(partner.Name, (c, ct) => TimelineAsync(c, new TimelineFilter(UserId: partner.Id), ct)));

    internal void OnAlbumsChanged()
    {
        if (Page == LibraryPage.Albums)
            _ = LoadAlbumsAsync();
    }

    // Sharing: albums shared with you or by you, your shared links, and partners sharing with you.

    public ObservableCollection<AlbumCardViewModel> SharedAlbums { get; } = [];
    public ObservableCollection<SharedLinkViewModel> SharedLinks { get; } = [];
    public ObservableCollection<PartnerViewModel> Partners { get; } = [];

    [ObservableProperty]
    public partial string? SharedAlbumsMessage { get; set; }

    [ObservableProperty]
    public partial string? SharedLinksMessage { get; set; }

    [ObservableProperty]
    public partial bool HasPartners { get; set; }

    async Task LoadSharingAsync()
    {
        var albums = LoadCardsAsync(async c => (await c.GetSharedAlbumsAsync(_lifetime.Token))
                .OrderBy(a => a.AlbumName, StringComparer.CurrentCultureIgnoreCase).Select(a => new AlbumCardViewModel(this, a)).ToList(),
            SharedAlbums, null, Tr.T("No shared albums yet."), m => SharedAlbumsMessage = m);
        await Task.WhenAll(albums, LoadSharedLinksAsync(), LoadPartnersAsync());
    }

    async Task LoadSharedLinksAsync()
    {
        if (Client is not { } client)
            return;
        try
        {
            var links = await client.GetSharedLinksAsync(_lifetime.Token);
            var baseUrl = await client.GetShareBaseUrlAsync(AppServices.Settings.Current.WebUrl ?? client.ApiUrl.ToString(), _lifetime.Token);
            foreach (var old in SharedLinks)
                old.Dispose();
            SharedLinks.Clear();
            foreach (var link in links)
                SharedLinks.Add(new SharedLinkViewModel(this, link, $"{baseUrl}/share/{link.Key}"));
            SharedLinksMessage = links.Count == 0 ? Tr.T("No shared links yet. Select photos (or open an album) and choose Share.") : null;
            if (Thumbnails is { } loader)
                foreach (var link in SharedLinks)
                    _ = link.LoadCoverAsync(loader, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("Could not load shared links", ex);
            SharedLinksMessage = Tr.T("Couldn't load your shared links. {0}", NetworkErrors.Describe(ex));
        }
    }

    async Task LoadPartnersAsync()
    {
        if (Client is not { } client)
            return;
        try
        {
            var partners = await client.GetPartnersAsync(_lifetime.Token);
            Partners.Clear();
            foreach (var partner in partners)
                Partners.Add(new PartnerViewModel(this, partner));
            HasPartners = Partners.Count > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Info($"Partners unavailable: {ex.Message}");
            HasPartners = false;
        }
    }

    internal async Task DeleteSharedLinkAsync(SharedLinkViewModel link)
    {
        if (Client is not { } client || _main is null)
            return;
        if (!await _main.ConfirmAsync(Tr.T("Delete this link?"), Tr.T("Anyone who has it won't be able to open it anymore. The photos stay in Immich."),
                Tr.T("Delete link"), danger: true))
            return;
        try
        {
            await client.DeleteSharedLinkAsync(link.Link.Id, CancellationToken.None);
            SharedLinks.Remove(link);
            link.Dispose();
            if (SharedLinks.Count == 0)
                SharedLinksMessage = Tr.T("No shared links yet. Select photos (or open an album) and choose Share.");
            ShowNotice(Tr.T("Link deleted"));
        }
        catch (Exception ex)
        {
            ShowNotice(Tr.T("Couldn't delete the link. {0}", NetworkErrors.Describe(ex)));
        }
    }

    // ---- Locked folder (needs the account's PIN, like the web and mobile apps) ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrid), nameof(ShowLockedPrompt), nameof(ShowLockAction))]
    public partial bool IsUnlocked { get; set; }

    [ObservableProperty]
    public partial string Pin { get; set; } = "";

    [ObservableProperty]
    public partial string? LockedMessage { get; set; }

    [ObservableProperty]
    public partial bool CanEnterPin { get; set; }

    [ObservableProperty]
    public partial bool IsUnlocking { get; set; }

    async Task CheckLockedAsync()
    {
        if (Client is not { } client)
            return;
        IsLoading = false;
        LockedMessage = null;
        CanEnterPin = false;
        try
        {
            var status = await client.GetAuthStatusAsync(_lifetime.Token);
            if (!status.HasPin)
            {
                LockedMessage = Tr.T("Set up a PIN code in Immich first (Account settings → PIN code), then come back here.");
                return;
            }
            if (status.IsElevated)
            {
                IsUnlocked = true;
                await LoadGridAsync();
                return;
            }
            CanEnterPin = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ImmichApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
        {
            LockedMessage = Tr.T("The locked folder needs signing in with your password or SSO (an API key can't open it).");
        }
        catch (Exception ex)
        {
            LockedMessage = Tr.T("Couldn't check the locked folder. {0}", NetworkErrors.Describe(ex));
        }
    }

    [RelayCommand]
    async Task UnlockAsync()
    {
        if (Client is not { } client || Pin.Length == 0 || IsUnlocking)
            return;
        IsUnlocking = true;
        LockedMessage = null;
        try
        {
            await client.UnlockSessionAsync(Pin, CancellationToken.None);
            Pin = "";
            IsUnlocked = true;
            await LoadGridAsync();
        }
        catch (ImmichApiException ex) when ((int)ex.StatusCode is >= 400 and < 500)
        {
            LockedMessage = Tr.T("That PIN isn't right.");
        }
        catch (Exception ex)
        {
            LockedMessage = Tr.T("Couldn't unlock. {0}", NetworkErrors.Describe(ex));
        }
        finally
        {
            IsUnlocking = false;
        }
    }

    /// <summary>Locks the folder again (also when leaving the page).</summary>
    [RelayCommand]
    public async Task LockAsync()
    {
        if (!IsUnlocked)
            return;
        IsUnlocked = false;
        Drill = null;
        ClearSelection();
        ReleaseRows();
        Viewer = null;
        CanEnterPin = true;
        try
        {
            if (Client is { } client)
                await client.LockSessionAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Info($"Locking the session failed: {ex.Message}");
        }
    }

    /// <summary>Asks for the PIN (in a panel) and then runs <paramref name="then"/>; used to move photos into the locked folder.</summary>
    async Task<bool> EnsureUnlockedAsync()
    {
        if (Client is not { } client)
            return false;
        try
        {
            var status = await client.GetAuthStatusAsync(CancellationToken.None);
            if (status.IsElevated)
                return true;
            if (!status.HasPin)
            {
                ShowNotice(Tr.T("Set up a PIN code in Immich first (Account settings → PIN code), then come back here."));
                return false;
            }
        }
        catch (Exception ex)
        {
            ShowNotice(Tr.T("Couldn't check the locked folder. {0}", NetworkErrors.Describe(ex)));
            return false;
        }
        var panel = new PinPanelViewModel(this);
        Panel = panel;
        return await panel.Result;
    }

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

    // What the selection bar offers depends on where you are.
    bool InTrash => Page == LibraryPage.Trash && Drill is null;
    bool InArchive => Page == LibraryPage.Archive && Drill is null;
    bool InLocked => Page == LibraryPage.Locked;

    /// <summary>Normal actions (share, album, favorite, download, archive, trash): everywhere but the trash and the locked folder.</summary>
    public bool CanUseNormalActions => !InTrash && !InLocked;
    public bool CanArchive => CanUseNormalActions && !InArchive;
    public bool CanUnarchive => InArchive;
    public bool CanMoveToLocked => CanUseNormalActions;
    public bool CanMoveOutOfLocked => InLocked;
    public bool CanRestore => InTrash;

    partial void OnDrillChanged(GridQuery? value)
    {
        foreach (var name in new[] { nameof(CanUseNormalActions), nameof(CanArchive), nameof(CanUnarchive), nameof(CanMoveToLocked), nameof(CanRestore) })
            OnPropertyChanged(name);
    }

    [RelayCommand]
    Task ArchiveSelectionAsync() => MoveSelectionAsync("archive", Tr.T("Archived {0}"));

    [RelayCommand]
    Task UnarchiveSelectionAsync() => MoveSelectionAsync("timeline", Tr.T("Moved {0} back to your timeline"));

    [RelayCommand]
    async Task MoveSelectionToLockedAsync()
    {
        if (await EnsureUnlockedAsync())
            await MoveSelectionAsync("locked", Tr.T("Moved {0} to the locked folder"));
    }

    [RelayCommand]
    Task MoveSelectionOutOfLockedAsync() => MoveSelectionAsync("timeline", Tr.T("Moved {0} back to your timeline"));

    async Task MoveSelectionAsync(string visibility, string doneFormat)
    {
        if (Client is not { } client || _selected.Count == 0)
            return;
        var ids = SelectedIds;
        try
        {
            await client.SetVisibilityAsync(ids, visibility, CancellationToken.None);
            ClearSelection();
            ShowNotice(string.Format(System.Globalization.CultureInfo.CurrentCulture, doneFormat, Format.Plural(ids.Count, "photo")));
            _ = LoadGridAsync();
        }
        catch (Exception ex)
        {
            ShowNotice(Tr.T("Couldn't move the photos. {0}", NetworkErrors.Describe(ex)));
        }
    }

    [RelayCommand]
    async Task RestoreSelectionAsync()
    {
        if (Client is not { } client || _selected.Count == 0)
            return;
        var ids = SelectedIds;
        try
        {
            await client.RestoreFromTrashAsync(ids, CancellationToken.None);
            ClearSelection();
            ShowNotice(Tr.T("Restored {0}", Format.Plural(ids.Count, "photo")));
            _ = LoadGridAsync();
        }
        catch (Exception ex)
        {
            ShowNotice(Tr.T("Couldn't restore. {0}", NetworkErrors.Describe(ex)));
        }
    }

    [RelayCommand]
    async Task DeleteSelectionForeverAsync()
    {
        if (Client is not { } client || _main is null || _selected.Count == 0)
            return;
        var ids = SelectedIds;
        var what = Format.Plural(ids.Count, "photo");
        if (!await _main.ConfirmAsync(Tr.T("Delete {0} forever?", what), Tr.T("They're removed from Immich for good. This can't be undone."),
                Tr.T("Delete forever"), danger: true))
            return;
        try
        {
            await client.DeletePermanentlyAsync(ids, CancellationToken.None);
            ClearSelection();
            ShowNotice(Tr.T("Deleted {0}", what));
            _ = LoadGridAsync();
        }
        catch (Exception ex)
        {
            ShowNotice(Tr.T("Couldn't delete. {0}", NetworkErrors.Describe(ex)));
        }
    }

    [RelayCommand]
    async Task RestoreAllAsync()
    {
        if (Client is not { } client)
            return;
        try
        {
            await client.RestoreAllFromTrashAsync(CancellationToken.None);
            ShowNotice(Tr.T("Restored everything in the trash"));
            _ = LoadGridAsync();
        }
        catch (Exception ex)
        {
            ShowNotice(Tr.T("Couldn't restore. {0}", NetworkErrors.Describe(ex)));
        }
    }

    [RelayCommand]
    async Task EmptyTrashAsync()
    {
        if (Client is not { } client || _main is null)
            return;
        if (!await _main.ConfirmAsync(Tr.T("Empty the trash?"), Tr.T("Everything in the trash is removed from Immich for good. This can't be undone."),
                Tr.T("Empty trash"), danger: true))
            return;
        try
        {
            await client.EmptyTrashAsync(CancellationToken.None);
            ShowNotice(Tr.T("The trash is empty."));
            _ = LoadGridAsync();
        }
        catch (Exception ex)
        {
            ShowNotice(Tr.T("Couldn't empty the trash. {0}", NetworkErrors.Describe(ex)));
        }
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

    partial void OnPanelChanged(ViewModelBase? oldValue, ViewModelBase? newValue)
    {
        // A PIN prompt closed some other way (Esc) counts as cancelled.
        if (oldValue is PinPanelViewModel pin && newValue is null)
            pin.CloseCommand.Execute(null);
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

    /// <summary>Copies a photo to the clipboard as a picture (full size, capped so it stays pasteable).</summary>
    internal async Task CopyImageAsync(string assetId)
    {
        if (Client is not { } client)
            return;
        ShowNotice(Tr.T("Copying…"), sticky: true);
        try
        {
            byte[] data;
            try
            {
                var size = AppServices.Network.IsMetered ? AssetImageSize.Preview : AssetImageSize.Fullsize;
                data = await client.GetAssetImageAsync(assetId, size, CancellationToken.None);
            }
            catch (ImmichApiException)
            {
                data = await client.GetAssetImageAsync(assetId, AssetImageSize.Preview, CancellationToken.None);
            }
            var window = _main?.GetWindowHandle?.Invoke() ?? IntPtr.Zero;
            await Task.Run(() =>
            {
                using var bitmap = Images.DecodeUpright(data, 4096);
                ImageClipboard.Copy(bitmap, window);
            });
            ShowNotice(Tr.T("Copied to clipboard"));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not copy the picture", ex);
            ShowNotice(Tr.T("Couldn't copy the picture. {0}", NetworkErrors.Describe(ex)));
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
