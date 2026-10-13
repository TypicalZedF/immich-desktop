using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichDesktop.App.Services;
using ImmichDesktop.Core;
using ImmichDesktop.Core.Api;
using ImmichDesktop.Core.Settings;

namespace ImmichDesktop.App.ViewModels;

/// <summary>Map settings, like the Immich web app's: which photos the map shows, and how.</summary>
public sealed partial class MapSettingsViewModel : ViewModelBase
{
    readonly PhotosViewModel _owner;

    public MapSettingsViewModel(PhotosViewModel owner)
    {
        _owner = owner;
        var s = AppServices.Settings.Current;
        AllowDark = s.MapAllowDark;
        OnlyFavorites = s.MapOnlyFavorites;
        IncludeArchived = s.MapIncludeArchived;
        IncludePartners = s.MapIncludePartners;
        IncludeSharedAlbums = s.MapIncludeSharedAlbums;
        ShowPanel = s.MapShowPanel;
        HeatMap = s.MapHeat;
        StreetDetail = s.MapStreetDetail;
        DateRanges =
        [
            new(Tr.T("All"), MapDateRange.All),
            new(Tr.T("Past 24 hours"), MapDateRange.Day),
            new(Tr.T("Past 7 days"), MapDateRange.Week),
            new(Tr.T("Past 30 days"), MapDateRange.Month),
            new(Tr.T("Past year"), MapDateRange.Year),
            new(Tr.T("Past 3 years"), MapDateRange.ThreeYears),
            new(Tr.T("Custom dates"), MapDateRange.Custom),
        ];
        SelectedDateRange = DateRanges.FirstOrDefault(r => r.Value == s.MapDateRange) ?? DateRanges[0];
        From = s.MapDateFrom ?? DateTime.Today.AddYears(-1);
        To = s.MapDateTo ?? DateTime.Today;
    }

    [ObservableProperty] public partial bool AllowDark { get; set; }
    [ObservableProperty] public partial bool OnlyFavorites { get; set; }
    [ObservableProperty] public partial bool IncludeArchived { get; set; }
    [ObservableProperty] public partial bool IncludePartners { get; set; }
    [ObservableProperty] public partial bool IncludeSharedAlbums { get; set; }
    [ObservableProperty] public partial bool ShowPanel { get; set; }
    [ObservableProperty] public partial bool HeatMap { get; set; }
    [ObservableProperty] public partial bool StreetDetail { get; set; }

    public IReadOnlyList<Option<MapDateRange>> DateRanges { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustom))]
    public partial Option<MapDateRange> SelectedDateRange { get; set; }

    public bool IsCustom => SelectedDateRange.Value == MapDateRange.Custom;

    [ObservableProperty] public partial DateTime? From { get; set; }
    [ObservableProperty] public partial DateTime? To { get; set; }

    [RelayCommand]
    void Save()
    {
        AppServices.Settings.Update(s =>
        {
            s.MapAllowDark = AllowDark;
            s.MapOnlyFavorites = OnlyFavorites;
            s.MapIncludeArchived = IncludeArchived;
            s.MapIncludePartners = IncludePartners;
            s.MapIncludeSharedAlbums = IncludeSharedAlbums;
            s.MapShowPanel = ShowPanel;
            s.MapHeat = HeatMap;
            s.MapStreetDetail = StreetDetail;
            s.MapDateRange = SelectedDateRange.Value;
            s.MapDateFrom = From?.Date;
            s.MapDateTo = To?.Date;
        });
        _owner.Panel = null;
        _owner.OnMapSettingsSaved();
    }

    [RelayCommand]
    void Cancel() => _owner.Panel = null;
}
