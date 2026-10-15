using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;

namespace ImmichSplash.App.ViewModels;

public sealed partial class PhotoViewerViewModel
{
    // ---- The "..." menu (like the Immich web app's) ----

    /// <summary>Photos in the archive offer "Unarchive", others "Archive".</summary>
    public bool InArchive => _owner.IsArchive;

    public bool InLockedFolder => _owner.IsLockedFolder;

    public bool CanArchive => CanEdit && !_owner.IsLockedFolder;

    [RelayCommand]
    async Task ArchiveAsync()
    {
        var archive = !InArchive;
        var ok = await _owner.MoveAssetsAsync([Asset.Id], archive ? "archive" : "timeline",
            archive ? Tr.T("Archived the photo") : Tr.T("Moved the photo back to your timeline"));
        if (ok)
            Close();
    }

    [RelayCommand]
    async Task MoveToLockedAsync()
    {
        var lockIt = !InLockedFolder;
        var ok = await _owner.MoveAssetsAsync([Asset.Id], lockIt ? "locked" : "timeline",
            lockIt ? Tr.T("Moved the photo to the locked folder") : Tr.T("Moved the photo back to your timeline"));
        if (ok)
            Close();
    }

    [RelayCommand]
    void ViewSimilar()
    {
        var id = Asset.Id;
        Close();
        _owner.ShowSimilar(id);
    }

    [RelayCommand]
    void ViewInTimeline() => _owner.ShowInTimeline(Asset);

    [RelayCommand]
    Task SetAsProfilePictureAsync() => _owner.SetAsProfilePictureAsync(Asset.Id);

    [RelayCommand]
    Task RefreshFacesAsync() => _owner.RunJobAsync(Asset.Id, "refresh-faces", Tr.T("Looking for faces again"));

    [RelayCommand]
    Task RefreshMetadataAsync() => _owner.RunJobAsync(Asset.Id, "refresh-metadata", Tr.T("Reading the photo's details again"));

    [RelayCommand]
    Task RefreshThumbnailsAsync() => _owner.RunJobAsync(Asset.Id, "regenerate-thumbnail", Tr.T("Making new thumbnails"));

    // ---- Slideshow: the next photo every few seconds (a video plays to its end first) ----

    DispatcherTimer? _slideshow;

    [ObservableProperty]
    public partial bool IsSlideshow { get; set; }

    [RelayCommand]
    void ToggleSlideshow()
    {
        IsSlideshow = !IsSlideshow;
        if (!IsSlideshow)
        {
            _slideshow?.Stop();
            _owner.ShowNotice(Tr.T("Slideshow stopped"));
            return;
        }
        _owner.ShowNotice(Tr.T("Slideshow started. Press Esc to stop."));
        _slideshow ??= new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => SlideshowStep());
        _slideshow.Start();
    }

    void SlideshowStep()
    {
        if (!IsSlideshow)
        {
            _slideshow?.Stop();
            return;
        }
        // Stay on a video until it's over.
        if (Asset.IsImage == false && _video is { Ended: false })
            return;
        NextCommand.Execute(null);
    }

    void StopSlideshow()
    {
        IsSlideshow = false;
        _slideshow?.Stop();
    }
}
