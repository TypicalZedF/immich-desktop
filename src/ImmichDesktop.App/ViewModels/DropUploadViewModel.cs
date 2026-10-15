using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichDesktop.App.Services;
using ImmichDesktop.Core;
using ImmichDesktop.Core.Api;
using ImmichDesktop.Core.Sync;

namespace ImmichDesktop.App.ViewModels;

/// <summary>
/// Photos and videos dropped on the window: upload them once (they don't become a backup folder),
/// optionally into an album. Files already in Immich are skipped, like in the backup.
/// </summary>
public sealed partial class DropUploadViewModel : ViewModelBase
{
    readonly MainWindowViewModel _main;
    readonly IReadOnlyList<string> _files;
    CancellationTokenSource? _cts;

    public DropUploadViewModel(MainWindowViewModel main, IReadOnlyList<string> files)
    {
        _main = main;
        _files = files;
        if (files.Count == 0)
        {
            Title = Tr.T("Nothing to upload");
            StatusText = Tr.T("There are no photos or videos in what you dropped.");
            IsDone = true;
            return;
        }
        Title = Tr.T("Upload {0} to Immich?", Format.Plural(files.Count, "photo or video", "photos and videos"));
        _ = LoadAlbumNamesAsync();
    }

    public string Title { get; }

    /// <summary>Optional: put everything in this album (an existing one, or a new one with this name).</summary>
    [ObservableProperty]
    public partial string AlbumName { get; set; } = "";

    public string AlbumHint => Tr.T("Add to an album (optional)");

    /// <summary>Your albums, as suggestions while typing.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> AlbumNames { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAsking))]
    public partial bool IsUploading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAsking))]
    public partial bool IsDone { get; set; }

    public bool IsAsking => !IsUploading && !IsDone;

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    async Task LoadAlbumNamesAsync()
    {
        try
        {
            if (AppServices.Account.Client is { } client)
                AlbumNames = (await client.GetAlbumsAsync(CancellationToken.None)).Select(a => a.AlbumName).Distinct().Order().ToList();
        }
        catch (Exception ex)
        {
            Log.Info($"Albums for the drop panel unavailable: {ex.Message}");
        }
    }

    [RelayCommand]
    async Task UploadAsync()
    {
        if (AppServices.Account.Client is not { } client)
            return;
        var cts = _cts = new CancellationTokenSource();
        IsUploading = true;
        StatusText = Tr.T("Checking {0}…", Format.Plural(_files.Count, "file"));
        var progress = new Progress<DropProgress>(p =>
        {
            Progress = p.Total > 0 ? p.Done / (double)p.Total : 0;
            StatusText = Tr.T("{0} of {1} done", p.Done.ToString("N0"), p.Total.ToString("N0"));
        });
        try
        {
            var limiter = new BandwidthLimiter(AppServices.Settings.Current.UploadLimitKBps * 1024L);
            var album = AlbumName.Trim();
            var result = await Task.Run(() => DroppedFilesUploader.UploadAsync(client, _files, album, limiter, progress, cts.Token), cts.Token);
            var parts = new List<string>();
            if (result.Uploaded > 0)
                parts.Add(Tr.T("Uploaded {0}.", Format.Plural(result.Uploaded, "photo or video", "photos and videos")));
            if (result.AlreadyThere > 0)
                parts.Add(Tr.T("{0} already in Immich.", Format.Plural(result.AlreadyThere, "file")));
            if (result.Failed > 0)
                parts.Add(Tr.T("{0} couldn't be uploaded.", Format.Plural(result.Failed, "file")));
            if (album.Length > 0 && result.Failed < _files.Count)
                parts.Add(Tr.T("Added to the album “{0}”.", album));
            StatusText = string.Join(" ", parts);
            AppServices.Activity.Add(result.Failed > 0 ? ActivityKind.Warning : ActivityKind.Success,
                Tr.T("Dropped files: {0}", StatusText));
        }
        catch (OperationCanceledException)
        {
            StatusText = Tr.T("Stopped. Files uploaded so far stay in Immich.");
        }
        catch (Exception ex)
        {
            Log.Warn("Upload of dropped files failed", ex);
            StatusText = Tr.T("Couldn't upload: {0}", NetworkErrors.Describe(ex));
        }
        finally
        {
            IsUploading = false;
            IsDone = true;
            Progress = 1;
        }
    }

    [RelayCommand]
    void Cancel()
    {
        if (IsUploading)
            _cts?.Cancel();
        else
            _main.CloseOverlay(this);
    }

    [RelayCommand]
    void Close() => _main.CloseOverlay(this);
}
