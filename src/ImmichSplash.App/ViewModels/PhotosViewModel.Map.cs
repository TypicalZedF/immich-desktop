using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;

namespace ImmichSplash.App.ViewModels;

/// <summary>
/// The map page's side panel (the photos of a clicked bubble, or of the area in view) and its
/// settings, like the Immich web app's map.
/// </summary>
public sealed partial class PhotosViewModel
{
    const int MapPanelColumns = 3;

    public bool MapAllowDark => AppServices.Settings.Current.MapAllowDark;

    public bool MapHeat
    {
        get => AppServices.Settings.Current.MapHeat;
        set
        {
            if (value == MapHeat)
                return;
            AppServices.Settings.Update(s => s.MapHeat = value);
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    void OpenMapSettings() => Panel = new MapSettingsViewModel(this);

    internal void OnMapSettingsSaved()
    {
        OnPropertyChanged(nameof(MapAllowDark));
        OnPropertyChanged(nameof(MapHeat));
        OnPropertyChanged(nameof(ShowStreetDetail));
        CloseMapPanel();
        _ = LoadMarkersAsync();
    }

    // ---- Side panel ----

    [ObservableProperty]
    public partial bool ShowMapPanel { get; set; }

    [ObservableProperty]
    public partial string MapPanelTitle { get; set; } = "";

    [ObservableProperty]
    public partial ObservableCollection<AssetRowViewModel> MapPanelRows { get; set; } = [];

    /// <summary>Width of the panel's grid (set by the view), to size its tiles.</summary>
    internal double MapPanelWidth { get; set; } = 360;

    /// <summary>The panel follows the area in view (Map settings → Show asset panel by default) until a bubble is clicked.</summary>
    bool _mapPanelFollowsView;

    /// <summary>A bubble or photo was clicked: show its photos beside the map.</summary>
    public void ShowMapPhotos(IReadOnlyList<MapMarker> markers)
    {
        _mapPanelFollowsView = false;
        OpenMapPanel(markers, null);
    }

    /// <summary>The map moved: with "Show asset panel by default", the panel lists what's in view.</summary>
    internal void OnMapViewChanged(IReadOnlyList<MapMarker> visible)
    {
        if (!AppServices.Settings.Current.MapShowPanel || (ShowMapPanel && !_mapPanelFollowsView))
            return;
        _mapPanelFollowsView = true;
        OpenMapPanel(visible, Format.Plural(visible.Count, Tr.T("photo in view"), Tr.T("photos in view")));
    }

    void OpenMapPanel(IReadOnlyList<MapMarker> markers, string? title)
    {
        foreach (var row in MapPanelRows)
            row.Unrealize();
        var assets = markers.Select(m => new TimelineAsset(m.Id, true, false, 1, null, null)).ToList();
        var place = markers.Select(m => m.City).Where(c => !string.IsNullOrWhiteSpace(c)).GroupBy(c => c).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        MapPanelTitle = title ?? (place is null
            ? Format.Plural(assets.Count, Tr.T("photo"), Tr.T("photos"))
            : $"{Format.Plural(assets.Count, Tr.T("photo"), Tr.T("photos"))} · {place}");
        var tile = Math.Floor((MapPanelWidth - Gap * (MapPanelColumns - 1)) / MapPanelColumns);
        var bucket = BucketState.FromAssets(assets, MapPanelTitle).FirstOrDefault();
        var rows = new ObservableCollection<AssetRowViewModel>();
        if (bucket is not null)
            for (var start = 0; start < assets.Count; start += MapPanelColumns)
                rows.Add(new AssetRowViewModel(this, bucket, start, Math.Min(MapPanelColumns, assets.Count - start), tile));
        MapPanelRows = rows;
        ShowMapPanel = assets.Count > 0;
    }

    [RelayCommand]
    void CloseMapPanel()
    {
        foreach (var row in MapPanelRows)
            row.Unrealize();
        MapPanelRows = [];
        ShowMapPanel = false;
        _mapPanelFollowsView = false;
    }
}
