using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichDesktop.Core;
using ImmichDesktop.Core.Api;
using ImmichDesktop.Core.Settings;
using Material.Icons;

namespace ImmichDesktop.App.ViewModels;

/// <summary>One line under the search box: a person, place, album, past search or way to search.</summary>
public sealed class SearchSuggestion(SearchHistoryEntry entry, string title, string? subtitle, MaterialIconKind icon, bool isHistory = false)
{
    public SearchHistoryEntry Entry { get; } = entry;
    public string Title { get; } = title;
    public string? Subtitle { get; } = subtitle;
    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);
    public MaterialIconKind Icon { get; } = icon;
    public bool IsHistory { get; } = isHistory;
}

/// <summary>
/// The top-bar search, like the web app's: as you type it suggests people, places and albums by
/// name, file names, and smart search ("dog on the beach"); with nothing typed it shows your recent
/// searches. Picking one opens it in Photos.
/// </summary>
public sealed partial class ShellViewModel
{
    const int MaxHistory = 15;
    const int MaxPerKind = 4;

    public ObservableCollection<SearchSuggestion> Suggestions { get; } = [];

    [ObservableProperty]
    public partial bool IsSuggestionsOpen { get; set; }

    [ObservableProperty]
    public partial SearchSuggestion? SelectedSuggestion { get; set; }

    public bool HasHistory => AppServices.Settings.Current.SearchHistory.Count > 0;
    public bool ShowClearHistory => HasHistory && SearchText.Trim().Length == 0;

    List<PersonInfo>? _people;
    List<PlaceInfo>? _places;
    List<AlbumInfo>? _albums;
    bool _namesLoading;
    bool _choosing;

    partial void OnSearchTextChanged(string value)
    {
        if (_choosing)
            return;
        UpdateSuggestions();
        IsSuggestionsOpen = Suggestions.Count > 0;
    }

    /// <summary>The search box got focus: show suggestions (recent searches when it's empty).</summary>
    public void OnSearchFocused()
    {
        _ = LoadNamesAsync();
        UpdateSuggestions();
        IsSuggestionsOpen = Suggestions.Count > 0;
    }

    /// <summary>Up/Down in the search box move through the suggestions.</summary>
    public void MoveSelection(int delta)
    {
        if (Suggestions.Count == 0)
            return;
        IsSuggestionsOpen = true;
        var index = SelectedSuggestion is null ? (delta > 0 ? -1 : Suggestions.Count) : Suggestions.IndexOf(SelectedSuggestion);
        index = Math.Clamp(index + delta, -1, Suggestions.Count - 1);
        SelectedSuggestion = index < 0 ? null : Suggestions[index];
    }

    public void CloseSuggestions()
    {
        IsSuggestionsOpen = false;
        SelectedSuggestion = null;
    }

    /// <summary>People, places and album names, fetched once (for matching as you type).</summary>
    async Task LoadNamesAsync()
    {
        if (_namesLoading || _people is not null || AppServices.Account.Client is not { } client)
            return;
        _namesLoading = true;
        try
        {
            var people = Try(() => client.GetPeopleAsync(_lifetime.Token));
            var places = Try(() => client.GetPlacesAsync(_lifetime.Token));
            var albums = Try(() => client.GetAlbumsAsync(_lifetime.Token));
            _people = (await people)?.Where(p => p.Name.Length > 0).ToList() ?? [];
            _places = await places ?? [];
            _albums = await albums ?? [];
            if (IsSuggestionsOpen)
                UpdateSuggestions();
        }
        finally
        {
            _namesLoading = false;
        }

        static async Task<List<T>?> Try<T>(Func<Task<List<T>>> load)
        {
            try
            {
                return await load();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Info($"Search names unavailable: {ex.Message}");
                return null;
            }
        }
    }

    void UpdateSuggestions()
    {
        Suggestions.Clear();
        SelectedSuggestion = null;
        var text = SearchText.Trim();
        var history = AppServices.Settings.Current.SearchHistory;
        if (text.Length == 0)
        {
            foreach (var past in history)
                Suggestions.Add(new SearchSuggestion(past, past.Text, KindLabel(past.Kind), MaterialIconKind.History, isHistory: true));
            OnPropertyChanged(nameof(ShowClearHistory));
            return;
        }
        bool Matches(string name) => name.Contains(text, StringComparison.CurrentCultureIgnoreCase);
        // Smart search first (what Enter does), then names.
        Suggestions.Add(new SearchSuggestion(new SearchHistoryEntry { Kind = SearchKind.Text, Text = text },
            text, Tr.T("Search photos by what's in them"), MaterialIconKind.Magnify));
        foreach (var past in history.Where(h => Matches(h.Text)).Take(3))
            Suggestions.Add(new SearchSuggestion(past, past.Text, KindLabel(past.Kind), MaterialIconKind.History, isHistory: true));
        foreach (var person in (_people ?? []).Where(p => Matches(p.Name)).OrderBy(p => !p.Name.StartsWith(text, StringComparison.CurrentCultureIgnoreCase)).Take(MaxPerKind))
            Suggestions.Add(new SearchSuggestion(new SearchHistoryEntry { Kind = SearchKind.Person, Text = person.Name, Id = person.Id },
                person.Name, Tr.T("Person"), MaterialIconKind.AccountOutline));
        foreach (var place in (_places ?? []).Where(p => Matches(p.City) || (p.State is { } st && Matches(st)) || (p.Country is { } c && Matches(c)))
                     .OrderBy(p => !p.City.StartsWith(text, StringComparison.CurrentCultureIgnoreCase)).ThenBy(p => !Matches(p.City)).Take(MaxPerKind))
            Suggestions.Add(new SearchSuggestion(new SearchHistoryEntry { Kind = SearchKind.Place, Text = place.City, Id = place.CoverAssetId, Extra = place.Country },
                place.City, string.Join(", ", new[] { place.State, place.Country }.Where(x => !string.IsNullOrWhiteSpace(x))), MaterialIconKind.MapMarkerOutline));
        foreach (var album in (_albums ?? []).Where(a => Matches(a.AlbumName)).Take(MaxPerKind))
            Suggestions.Add(new SearchSuggestion(new SearchHistoryEntry { Kind = SearchKind.Album, Text = album.AlbumName, Id = album.Id },
                album.AlbumName, Tr.T("Album"), MaterialIconKind.ImageAlbum));
        Suggestions.Add(new SearchSuggestion(new SearchHistoryEntry { Kind = SearchKind.FileName, Text = text },
            text, Tr.T("Search file names"), MaterialIconKind.FileOutline));
        OnPropertyChanged(nameof(ShowClearHistory));
    }

    static string KindLabel(SearchKind kind) => kind switch
    {
        SearchKind.Person => Tr.T("Person"),
        SearchKind.Place => Tr.T("Place"),
        SearchKind.Album => Tr.T("Album"),
        SearchKind.FileName => Tr.T("File name"),
        _ => Tr.T("Search"),
    };

    [RelayCommand]
    void Choose(SearchSuggestion suggestion) => Run(suggestion.Entry);

    void Run(SearchHistoryEntry entry)
    {
        CloseSuggestions();
        Remember(entry);
        _choosing = true;
        SearchText = entry.Text;
        _choosing = false;
        Navigate(ShellPage.Photos);
        if (SelectedNav.ViewModel is not PhotosViewModel photos)
            return;
        switch (entry.Kind)
        {
            case SearchKind.Person when entry.Id is { } id:
                photos.OpenPerson(new PersonInfo(id, entry.Text, false));
                break;
            case SearchKind.Place:
                photos.OpenPlace(new PlaceInfo(entry.Text, null, entry.Extra, entry.Id ?? ""));
                break;
            case SearchKind.Album when entry.Id is { } id:
                photos.OpenAlbum(_albums?.FirstOrDefault(a => a.Id == id) ?? new AlbumInfo(id, entry.Text));
                break;
            case SearchKind.FileName:
                photos.SearchFileName(entry.Text);
                break;
            default:
                photos.SearchFor(entry.Text);
                break;
        }
    }

    static void Remember(SearchHistoryEntry entry) => AppServices.Settings.Update(s =>
    {
        s.SearchHistory.RemoveAll(h => h.Kind == entry.Kind && string.Equals(h.Text, entry.Text, StringComparison.CurrentCultureIgnoreCase) && h.Id == entry.Id);
        s.SearchHistory.Insert(0, new SearchHistoryEntry { Kind = entry.Kind, Text = entry.Text, Id = entry.Id, Extra = entry.Extra });
        if (s.SearchHistory.Count > MaxHistory)
            s.SearchHistory.RemoveRange(MaxHistory, s.SearchHistory.Count - MaxHistory);
    });

    [RelayCommand]
    void ForgetSearch(SearchSuggestion suggestion)
    {
        AppServices.Settings.Update(s => s.SearchHistory.RemoveAll(h => h.Kind == suggestion.Entry.Kind && h.Text == suggestion.Entry.Text && h.Id == suggestion.Entry.Id));
        UpdateSuggestions();
        IsSuggestionsOpen = Suggestions.Count > 0;
        OnPropertyChanged(nameof(HasHistory));
    }

    [RelayCommand]
    void ClearSearchHistory()
    {
        AppServices.Settings.Update(s => s.SearchHistory.Clear());
        UpdateSuggestions();
        IsSuggestionsOpen = false;
        OnPropertyChanged(nameof(HasHistory));
    }
}
