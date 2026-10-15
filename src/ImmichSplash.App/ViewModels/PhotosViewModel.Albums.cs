using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;

namespace ImmichSplash.App.ViewModels;

public enum AlbumFilterMode { All, Owned, Shared }

public enum AlbumSortMode { MostRecentPhoto, OldestPhoto, Title, ItemCount, LastModified, Created }

public sealed record AlbumSortChoice(AlbumSortMode Mode, string Label);

/// <summary>A year's worth of albums on the Albums page (or all of them, when not grouped).</summary>
public sealed partial class AlbumGroupViewModel(string title, string countText, bool showHeader, bool isList, bool isExpanded) : ObservableObject
{
    public string Title { get; } = title;
    public string CountText { get; } = countText;
    public bool ShowHeader { get; } = showHeader;
    public bool IsList { get; } = isList;
    public bool IsGrid => !IsList;
    public ObservableCollection<AlbumCardViewModel> Albums { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen), nameof(IsGridOpen), nameof(IsListOpen))]
    public partial bool IsExpanded { get; set; } = isExpanded;

    public bool IsOpen => !ShowHeader || IsExpanded;
    public bool IsGridOpen => IsOpen && IsGrid;
    public bool IsListOpen => IsOpen && IsList;

    /// <summary>Remembers which years are folded away across rebuilds.</summary>
    internal Action<AlbumGroupViewModel>? Changed { get; set; }

    partial void OnIsExpandedChanged(bool value) => Changed?.Invoke(this);

    [RelayCommand]
    void Toggle() => IsExpanded = !IsExpanded;
}

public sealed partial class PhotosViewModel
{
    // ---- The Albums page: filter, search, sort, group by year, create, edit ----

    public ObservableCollection<AlbumGroupViewModel> AlbumGroups { get; } = [];

    public IReadOnlyList<AlbumSortChoice> AlbumSortChoices { get; } =
    [
        new(AlbumSortMode.MostRecentPhoto, Tr.T("Most recent photo")),
        new(AlbumSortMode.OldestPhoto, Tr.T("Oldest photo")),
        new(AlbumSortMode.Title, Tr.T("Album title")),
        new(AlbumSortMode.ItemCount, Tr.T("Number of items")),
        new(AlbumSortMode.LastModified, Tr.T("Last modified")),
        new(AlbumSortMode.Created, Tr.T("Date created")),
    ];

    readonly HashSet<string> _foldedYears = [];
    bool _albumPrefsLoaded;
    bool _albumPrefsLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterAll), nameof(IsFilterOwned), nameof(IsFilterShared))]
    public partial AlbumFilterMode AlbumFilter { get; set; }

    public bool IsFilterAll => AlbumFilter == AlbumFilterMode.All;
    public bool IsFilterOwned => AlbumFilter == AlbumFilterMode.Owned;
    public bool IsFilterShared => AlbumFilter == AlbumFilterMode.Shared;

    [ObservableProperty]
    public partial string AlbumSearchText { get; set; } = "";

    [ObservableProperty]
    public partial AlbumSortChoice SelectedAlbumSort { get; set; } = null!;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlbumSortIcon))]
    public partial bool AlbumSortDescending { get; set; } = true;

    public Material.Icons.MaterialIconKind AlbumSortIcon =>
        AlbumSortDescending ? Material.Icons.MaterialIconKind.ArrowDown : Material.Icons.MaterialIconKind.ArrowUp;

    [ObservableProperty]
    public partial bool AlbumGroupByYear { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlbumViewIcon), nameof(AlbumViewText))]
    public partial bool AlbumListView { get; set; }

    public Material.Icons.MaterialIconKind AlbumViewIcon =>
        AlbumListView ? Material.Icons.MaterialIconKind.ViewGridOutline : Material.Icons.MaterialIconKind.FormatListBulleted;

    public string AlbumViewText => AlbumListView ? Tr.T("Cover") : Tr.T("List");

    [ObservableProperty]
    public partial string? AlbumsEmptyText { get; set; }

    [ObservableProperty]
    public partial bool HasAnyAlbums { get; set; }

    void LoadAlbumPrefs()
    {
        if (_albumPrefsLoaded)
            return;
        _albumPrefsLoaded = true;
        var s = AppServices.Settings.Current;
        _albumPrefsLoading = true;
        SelectedAlbumSort = AlbumSortChoices.FirstOrDefault(c => c.Mode.ToString() == s.AlbumSort) ?? AlbumSortChoices[0];
        AlbumSortDescending = s.AlbumSortDescending;
        AlbumGroupByYear = s.AlbumGroupByYear;
        AlbumListView = s.AlbumListView;
        _albumPrefsLoading = false;
    }

    void AlbumArrangementChanged()
    {
        if (_albumPrefsLoading || SelectedAlbumSort is null)
            return;
        var sort = SelectedAlbumSort.Mode.ToString();
        var descending = AlbumSortDescending;
        var group = AlbumGroupByYear;
        var list = AlbumListView;
        AppServices.Settings.Update(s =>
        {
            s.AlbumSort = sort;
            s.AlbumSortDescending = descending;
            s.AlbumGroupByYear = group;
            s.AlbumListView = list;
        });
        RebuildAlbumGroups();
    }

    partial void OnAlbumFilterChanged(AlbumFilterMode value) => RebuildAlbumGroups();
    partial void OnAlbumSearchTextChanged(string value) => RebuildAlbumGroups();
    partial void OnSelectedAlbumSortChanged(AlbumSortChoice value) => AlbumArrangementChanged();
    partial void OnAlbumSortDescendingChanged(bool value) => AlbumArrangementChanged();
    partial void OnAlbumGroupByYearChanged(bool value) => AlbumArrangementChanged();
    partial void OnAlbumListViewChanged(bool value) => AlbumArrangementChanged();

    [RelayCommand]
    void ShowAllAlbums() => AlbumFilter = AlbumFilterMode.All;

    [RelayCommand]
    void ShowOwnedAlbums() => AlbumFilter = AlbumFilterMode.Owned;

    [RelayCommand]
    void ShowSharedAlbums() => AlbumFilter = AlbumFilterMode.Shared;

    [RelayCommand]
    void ToggleAlbumSortDirection() => AlbumSortDescending = !AlbumSortDescending;

    [RelayCommand]
    void ToggleAlbumGrouping() => AlbumGroupByYear = !AlbumGroupByYear;

    [RelayCommand]
    void ExpandAllAlbumGroups()
    {
        _foldedYears.Clear();
        foreach (var g in AlbumGroups)
            g.IsExpanded = true;
    }

    [RelayCommand]
    void CollapseAllAlbumGroups()
    {
        foreach (var g in AlbumGroups.Where(g => g.ShowHeader))
            g.IsExpanded = false;
    }

    [RelayCommand]
    void ToggleAlbumView() => AlbumListView = !AlbumListView;

    /// <summary>The date an album is sorted (and grouped into years) by.</summary>
    static DateTimeOffset? SortDate(AlbumInfo a, AlbumSortMode mode) => mode switch
    {
        AlbumSortMode.OldestPhoto => a.StartDate ?? a.CreatedAt,
        AlbumSortMode.Created => a.CreatedAt,
        AlbumSortMode.LastModified => a.UpdatedAt ?? a.CreatedAt,
        _ => a.EndDate ?? a.CreatedAt,
    };

    void RebuildAlbumGroups()
    {
        LoadAlbumPrefs();
        SelectedAlbumSort ??= AlbumSortChoices[0];
        var me = AppServices.Settings.Current.UserId;
        var mode = SelectedAlbumSort.Mode;
        HasAnyAlbums = Albums.Count > 0;

        IEnumerable<AlbumCardViewModel> query = Albums;
        query = AlbumFilter switch
        {
            AlbumFilterMode.Owned => query.Where(c => c.Album.IsOwnedBy(me)),
            AlbumFilterMode.Shared => query.Where(c => c.Album.IsSharedWithPeople(me)),
            _ => query,
        };
        var search = AlbumSearchText.Trim();
        if (search.Length > 0)
            query = query.Where(c => CultureInfo.CurrentCulture.CompareInfo.IndexOf(c.Album.AlbumName + " " + c.Album.Description, search,
                CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);

        var comparer = StringComparer.CurrentCultureIgnoreCase;
        var sorted = (mode, AlbumSortDescending) switch
        {
            (AlbumSortMode.Title, false) => query.OrderBy(c => c.Album.AlbumName, comparer),
            (AlbumSortMode.Title, true) => query.OrderByDescending(c => c.Album.AlbumName, comparer),
            (AlbumSortMode.ItemCount, false) => query.OrderBy(c => c.Album.AssetCount),
            (AlbumSortMode.ItemCount, true) => query.OrderByDescending(c => c.Album.AssetCount),
            (_, false) => query.OrderBy(c => SortDate(c.Album, mode) ?? DateTimeOffset.MinValue),
            (_, true) => query.OrderByDescending(c => SortDate(c.Album, mode) ?? DateTimeOffset.MinValue),
        };
        var list = sorted.ToList();

        foreach (var old in AlbumGroups)
            old.Changed = null;
        AlbumGroups.Clear();
        AlbumsEmptyText = list.Count == 0
            ? (Albums.Count == 0 ? Tr.T("No albums yet. Create one with “Create album”.") : Tr.T("No albums match."))
            : null;
        if (list.Count == 0)
            return;

        if (!AlbumGroupByYear)
        {
            var all = new AlbumGroupViewModel("", "", showHeader: false, AlbumListView, isExpanded: true);
            foreach (var card in list)
                all.Albums.Add(card);
            AlbumGroups.Add(all);
            return;
        }

        // The date-based sorts keep their own order inside each year; the years follow the sort direction.
        var byYear = list.GroupBy(c => SortDate(c.Album, mode)?.ToLocalTime().Year);
        var years = mode is AlbumSortMode.Title or AlbumSortMode.ItemCount || AlbumSortDescending
            ? byYear.OrderByDescending(g => g.Key ?? int.MinValue)
            : byYear.OrderBy(g => g.Key ?? int.MaxValue);
        foreach (var year in years)
        {
            var title = year.Key?.ToString(CultureInfo.CurrentCulture) ?? Tr.T("No date");
            var group = new AlbumGroupViewModel(title, "(" + Format.Plural(year.Count(), "album") + ")", showHeader: true, AlbumListView,
                isExpanded: !_foldedYears.Contains(title));
            group.Changed = g =>
            {
                if (g.IsExpanded)
                    _foldedYears.Remove(g.Title);
                else
                    _foldedYears.Add(g.Title);
            };
            foreach (var card in year)
                group.Albums.Add(card);
            AlbumGroups.Add(group);
        }
    }

    // ---- Create and edit ----

    [ObservableProperty]
    public partial bool IsEditingAlbum { get; set; }

    [ObservableProperty]
    public partial string AlbumEditorTitle { get; set; } = "";

    [ObservableProperty]
    public partial string AlbumEditorName { get; set; } = "";

    [ObservableProperty]
    public partial string AlbumEditorDescription { get; set; } = "";

    [ObservableProperty]
    public partial string? AlbumEditorError { get; set; }

    AlbumInfo? _editedAlbum;

    [RelayCommand]
    void CreateAlbum()
    {
        _editedAlbum = null;
        AlbumEditorTitle = Tr.T("Create album");
        AlbumEditorName = "";
        AlbumEditorDescription = "";
        AlbumEditorError = null;
        IsEditingAlbum = true;
    }

    internal void EditAlbum(AlbumInfo album)
    {
        _editedAlbum = album;
        AlbumEditorTitle = Tr.T("Edit album");
        AlbumEditorName = album.AlbumName;
        AlbumEditorDescription = album.Description ?? "";
        AlbumEditorError = null;
        IsEditingAlbum = true;
    }

    [RelayCommand]
    void CancelAlbumEditor() => IsEditingAlbum = false;

    [RelayCommand]
    async Task SaveAlbumEditorAsync()
    {
        var name = AlbumEditorName.Trim();
        if (name.Length == 0)
        {
            AlbumEditorError = Tr.T("Give the album a name.");
            return;
        }
        if (Client is not { } client)
            return;
        try
        {
            if (_editedAlbum is { } existing)
            {
                await client.UpdateAlbumAsync(existing.Id, name, AlbumEditorDescription.Trim(), CancellationToken.None);
                ShowNotice(Tr.T("Album updated"));
                IsEditingAlbum = false;
                await LoadAlbumsAsync();
            }
            else
            {
                var created = await client.CreateAlbumAsync(name, null, CancellationToken.None);
                ShowNotice(Tr.T("Created the album “{0}”", name));
                IsEditingAlbum = false;
                await LoadAlbumsAsync();
                OpenAlbum(created with { AlbumName = name });
            }
        }
        catch (Exception ex)
        {
            AlbumEditorError = Tr.T("Couldn't save the album. {0}", NetworkErrors.Describe(ex));
        }
    }

    internal void ShareAlbumFromCard(AlbumInfo album) =>
        Panel = new SharePanelViewModel(this, Tr.T("the album “{0}”", album.AlbumName), null, album.Id);

    internal async Task DownloadAlbumAsync(AlbumInfo album)
    {
        if (Client is not { } client || _main is null)
            return;
        var parent = await _main.PickFolderAsync(Tr.T("Save the album “{0}” to…", album.AlbumName));
        if (parent is null)
            return;
        try
        {
            var ids = new List<string>();
            foreach (var bucket in await TimelineAsync(client, new TimelineFilter(AlbumId: album.Id), _lifetime.Token))
                ids.AddRange((await bucket.GetAssetsAsync(_lifetime.Token)).Where(a => a.LocalPath is null).Select(a => a.Id));
            var folder = Directory.CreateDirectory(Path.Combine(parent, string.Concat(album.AlbumName.Split(Path.GetInvalidFileNameChars())).Trim() is { Length: > 0 } name ? name : "Album")).FullName;
            await DownloadToFolderAsync(ids.Distinct().ToList(), folder);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowNotice(Tr.T("Couldn't download the album. {0}", NetworkErrors.Describe(ex)));
        }
    }
}
