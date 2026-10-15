using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;
using ImmichSplash.Core.Sync;

namespace ImmichSplash.App.ViewModels;

/// <summary>
/// Photos and videos dropped on the window: upload them once (they don't become a backup folder),
/// optionally into an album. Files already in Immich are skipped, like in the backup.
/// </summary>
public sealed partial class DropUploadViewModel : ViewModelBase
{
    readonly MainWindowViewModel _main;
    readonly IReadOnlyList<string> _files;
    readonly AlbumInfo? _contextAlbum;
    readonly Action? _uploaded;
    CancellationTokenSource? _cts;

    /// <param name="contextAlbum">The album that was open when the files were dropped: they go into it unless you change that.</param>
    /// <param name="uploaded">Called after files went in (so the open page can show them).</param>
    public DropUploadViewModel(MainWindowViewModel main, IReadOnlyList<string> files, AlbumInfo? contextAlbum = null, Action? uploaded = null)
    {
        _main = main;
        _files = files;
        _contextAlbum = contextAlbum;
        _uploaded = uploaded;
        if (contextAlbum is not null)
            AlbumName = contextAlbum.AlbumName;
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

    /// <summary>Says where the files are going when an album was open.</summary>
    public string? ContextText => _contextAlbum is { } a ? Tr.T("They'll be added to the album “{0}”, the one you have open.", a.AlbumName) : null;

    public bool HasContext => _contextAlbum is not null;

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
            var result = await Task.Run(() => DroppedFilesUploader.UploadAsync(client, _files, album, limiter, progress, cts.Token,
                _contextAlbum is { } ctx && album == ctx.AlbumName ? ctx.Id : null), cts.Token);
            if (result.Uploaded + result.AlreadyThere > 0)
            {
                _uploaded?.Invoke();
                if (album.Length > 0)
                    await RememberAlbumAsync(client, album);
            }
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

    /// <summary>Puts the album the files went into at the top of the sidebar's recent albums.</summary>
    async Task RememberAlbumAsync(ImmichClient client, string name)
    {
        try
        {
            var id = _contextAlbum is { } ctx && ctx.AlbumName == name
                ? ctx.Id
                : (await client.GetAlbumsAsync(CancellationToken.None)).FirstOrDefault(a => a.AlbumName == name)?.Id;
            if (id is not null)
                PhotosViewModel.RememberAlbum(id);
            AppServices.RaiseAlbumsChanged();
        }
        catch (Exception ex)
        {
            Log.Info($"Couldn't note the album for the sidebar: {ex.Message}");
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
