using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;

namespace ImmichSplash.App.ViewModels;

public sealed partial class PhotosViewModel
{
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
        var me = AppServices.Settings.Current.UserId;
        var albums = LoadCardsAsync(async c => (await c.GetSharedAlbumsAsync(_lifetime.Token))
                .Where(a => a.IsSharedWithPeople(me))
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
}
