using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichDesktop.App.Services;
using ImmichDesktop.Core;
using ImmichDesktop.Core.Api;
using ImmichDesktop.Core.Platform;

namespace ImmichDesktop.App.ViewModels;

/// <summary>
/// Full-window viewer with previous/next across the whole grid. Shows the thumbnail instantly, then the
/// preview, then (on big screens or when zoomed in) the full-resolution image, so photos look sharp.
/// </summary>
public sealed partial class PhotoViewerViewModel : ViewModelBase
{
    /// <summary>Largest image decoded when zoomed in (pixels on the long side).</summary>
    const int ZoomedMaxSize = 6000;

    readonly PhotosViewModel _owner;
    CancellationTokenSource? _cts;
    BucketState _bucket;
    int _index;
    Bitmap? _ownedImage; // the viewer's own sharp image; placeholders belong to the grid
    int _loadedSize;     // long side of the best image shown so far
    bool _fullLoaded;

    readonly bool _readOnly;

    /// <param name="readOnly">Just for looking (e.g. from the duplicates review): no favorite, share, trash…</param>
    public PhotoViewerViewModel(PhotosViewModel owner, BucketState bucket, int index, TimelineAsset asset, Bitmap? placeholder, bool readOnly = false)
    {
        _owner = owner;
        _readOnly = readOnly;
        _bucket = bucket;
        _index = index;
        Show(asset, placeholder);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVideo), nameof(DurationText), nameof(IsLocalOnly), nameof(IsInImmich), nameof(CanEdit),
        nameof(DevicePath), nameof(IsOnDevice), nameof(CanCopy), nameof(LocalStatus), nameof(FileName))]
    public partial TimelineAsset Asset { get; set; }

    [ObservableProperty]
    public partial Bitmap? Image { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string DateText { get; set; } = "";

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial bool IsFavorite { get; set; }

    [ObservableProperty]
    public partial bool ShowInfo { get; set; }

    [ObservableProperty]
    public partial AssetDetails? Details { get; set; }

    /// <summary>Bumped on every photo change so the view can reset its zoom.</summary>
    [ObservableProperty]
    public partial int Generation { get; set; }

    public bool IsVideo => !Asset.IsImage;
    public string DurationText => Asset.DurationMs is { } ms and > 0 ? AssetCellViewModel.FormatDuration(ms) : "";
    public bool CanEdit => !_readOnly && !_owner.IsPicker && _owner.CanUseNormalActions && !Asset.IsLocalOnly;

    /// <summary>A file on this PC that isn't in Immich yet.</summary>
    public bool IsLocalOnly => Asset.IsLocalOnly;
    public bool IsInImmich => !Asset.IsLocalOnly;

    /// <summary>Where the photo is on this PC (a local-only file, or the file a backed-up photo came from).</summary>
    public string? DevicePath => Asset.LocalPath ?? _owner.DevicePathFor(Asset.Id);
    public bool IsOnDevice => DevicePath is not null;
    public bool CanCopy => Asset.IsImage;

    public string? LocalStatus => Asset switch
    {
        { IsLocalOnly: true, LocalFailed: true } => Tr.T("On this PC · couldn't back up yet (will retry)"),
        { IsLocalOnly: true } => Tr.T("On this PC · waiting to back up"),
        _ => null,
    };

    // Info panel lines
    public string? FileName => Details?.OriginalFileName ?? (Asset.LocalPath is { } path ? Path.GetFileName(path) : null);
    public string? Dimensions => Details is { Width: > 0, Height: > 0 } d
        ? $"{d.Width} × {d.Height}" + (d.FileSize is > 0 ? $"  ·  {Format.Bytes(d.FileSize.Value)}" : "")
        : Details?.FileSize is > 0 ? Format.Bytes(Details.FileSize.Value) : null;
    public string? Camera => Details?.Camera;
    public string? Lens => Details?.Lens;
    public string? Exposure => Details?.Exposure;
    public string? Place => Details?.Place;
    public string? Description => Details?.Description;
    public string? People => Details is { People.Count: > 0 } d ? string.Join(", ", d.People) : null;

    partial void OnDetailsChanged(AssetDetails? value)
    {
        foreach (var name in new[] { nameof(FileName), nameof(Dimensions), nameof(Camera), nameof(Lens), nameof(Exposure), nameof(Place), nameof(Description), nameof(People) })
            OnPropertyChanged(name);
    }

    void Show(TimelineAsset asset, Bitmap? placeholder)
    {
        Asset = asset;
        IsFavorite = asset.IsFavorite;
        DateText = DateTimeOffset.TryParse(asset.FileCreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToLocalTime() is var local ? $"{local.ToString("D", CultureInfo.CurrentCulture)} · {local.ToString("t", CultureInfo.CurrentCulture)}" : ""
            : "";
        Error = null;
        Details = null;
        VideoProgress = null;
        Generation++;

        // Show the (already loaded) thumbnail instantly, then swap in sharper images.
        Image = placeholder;
        ReleaseOwnedImage();
        _loadedSize = 0;
        _fullLoaded = false;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = LoadAsync(asset, _cts.Token);
        if (ShowInfo && !asset.IsLocalOnly)
            _ = LoadDetailsAsync(asset, _cts.Token);
    }

    async Task LoadAsync(TimelineAsset asset, CancellationToken ct)
    {
        if (_owner.Thumbnails is not { } loader)
            return;
        IsLoading = true;
        try
        {
            var needed = _owner.ViewerPixels;
            if (asset.LocalPath is { } path)
            {
                if (asset.IsImage)
                    await SwapInAsync(asset, () => ThumbnailLoader.LoadLocalForViewerAsync(path, needed, ct), ct);
                return;
            }
            await SwapInAsync(asset, () => loader.LoadForViewerAsync(asset.Id, AssetImageSize.Preview, needed, ct), ct);

            // The preview is ~1440px; on a big or high-DPI screen fetch the full image for a crisp picture.
            // Data saving: not on a metered connection (zooming in still fetches it).
            if (asset.IsImage && _loadedSize < needed * 0.9 && _owner.FullsizeSupported && !AppServices.Network.IsMetered)
                await LoadFullAsync(asset, needed, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(asset, Asset))
                Error = Tr.T("Couldn't load this photo. {0}", NetworkErrors.Describe(ex));
        }
        finally
        {
            if (ReferenceEquals(asset, Asset))
                IsLoading = false;
        }
    }

    /// <summary>Called by the view when zoomed in: load the full-resolution image once.</summary>
    public void OnZoomedIn()
    {
        if (_fullLoaded || !Asset.IsImage || _cts is not { } cts)
            return;
        var asset = Asset;
        if (asset.LocalPath is { } path)
        {
            _fullLoaded = true;
            _ = Task.Run(async () =>
            {
                try { await SwapInAsync(asset, () => ThumbnailLoader.LoadLocalForViewerAsync(path, ZoomedMaxSize, cts.Token), cts.Token); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Info($"Full image failed for {path}: {ex.Message}"); }
            });
            return;
        }
        if (!_owner.FullsizeSupported)
            return;
        _fullLoaded = true;
        _ = Task.Run(async () =>
        {
            try { await LoadFullAsync(asset, ZoomedMaxSize, cts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Info($"Full image failed for {asset.Id}: {ex.Message}"); }
        });
    }

    async Task LoadFullAsync(TimelineAsset asset, int maxSize, CancellationToken ct)
    {
        if (_owner.Thumbnails is not { } loader)
            return;
        _fullLoaded = true;
        try
        {
            await SwapInAsync(asset, () => loader.LoadForViewerAsync(asset.Id, AssetImageSize.Fullsize, maxSize, ct), ct);
        }
        catch (ImmichApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.NotFound)
        {
            _owner.FullsizeSupported = false; // older server: the preview is the best there is
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The preview is already showing; a sharper version failing isn't worth an error.
            Log.Info($"Full-size image unavailable for {asset.Id}: {ex.Message}");
        }
    }

    async Task SwapInAsync(TimelineAsset asset, Func<Task<Bitmap>> load, CancellationToken ct)
    {
        var bitmap = await load();
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            var size = Math.Max(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
            if (ct.IsCancellationRequested || !ReferenceEquals(asset, Asset) || size <= _loadedSize)
            {
                bitmap.Dispose();
                return;
            }
            Image = bitmap;
            ReleaseOwnedImage();
            _ownedImage = bitmap;
            _loadedSize = size;
        });
    }

    async Task LoadDetailsAsync(TimelineAsset asset, CancellationToken ct)
    {
        if (asset.IsLocalOnly || AppServices.Account.Client is not { } client)
            return;
        try
        {
            var details = await client.GetAssetAsync(asset.Id, ct);
            if (!ct.IsCancellationRequested && ReferenceEquals(asset, Asset))
                Details = details;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Info($"Details failed for {asset.Id}: {ex.Message}");
        }
    }

    [RelayCommand]
    Task Next() => StepAsync(+1);

    [RelayCommand]
    Task Previous() => StepAsync(-1);

    async Task StepAsync(int delta)
    {
        try
        {
            var result = await _owner.StepAsync(_bucket, _index, delta, CancellationToken.None);
            if (result is not { } r)
                return;
            _bucket = r.Bucket;
            _index = r.Index;
            Show(r.Asset, null);
        }
        catch (Exception ex)
        {
            Error = NetworkErrors.Describe(ex);
        }
    }

    // ---- Videos ----

    /// <summary>While a video is being fetched for playing: how far along ("Getting video… 40%").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFetchingVideo))]
    public partial string? VideoProgress { get; set; }

    public bool IsFetchingVideo => VideoProgress is not null;

    static string VideoCacheDir => Path.Combine(Path.GetTempPath(), "ImmichDesktop", "videos");

    /// <summary>
    /// Plays the video in the PC's video player. Videos in Immich are fetched first (the server's
    /// playable version) into a temporary folder that's cleaned up after a day.
    /// </summary>
    [RelayCommand]
    async Task PlayAsync()
    {
        if (Asset.IsImage || IsFetchingVideo)
            return;
        if (DevicePath is { } local && File.Exists(local))
        {
            ShellLauncher.Open(local);
            return;
        }
        if (AppServices.Account.Client is not { } client || _cts is not { } cts)
            return;
        var asset = Asset;
        var target = Path.Combine(VideoCacheDir, $"{asset.Id}.mp4");
        try
        {
            if (!File.Exists(target))
            {
                VideoProgress = Tr.T("Getting video…");
                await Task.Run(() => CleanVideoCache(), cts.Token);
                Directory.CreateDirectory(VideoCacheDir);
                var partial = target + ".part";
                await using (var file = File.Create(partial))
                {
                    await client.DownloadVideoAsync(asset.Id, file, p => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (ReferenceEquals(asset, Asset) && IsFetchingVideo)
                            VideoProgress = Tr.T("Getting video… {0}", p.ToString("P0", CultureInfo.CurrentCulture));
                    }), cts.Token);
                }
                File.Move(partial, target, overwrite: true);
            }
            ShellLauncher.Open(target);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not play {asset.Id}", ex);
            if (ReferenceEquals(asset, Asset))
                Error = Tr.T("Couldn't play this video. {0}", NetworkErrors.Describe(ex));
        }
        finally
        {
            VideoProgress = null;
        }
    }

    static void CleanVideoCache()
    {
        try
        {
            if (!Directory.Exists(VideoCacheDir))
                return;
            foreach (var file in new DirectoryInfo(VideoCacheDir).EnumerateFiles())
                if (file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1))
                    try { file.Delete(); } catch (IOException) { } // still open in a player
        }
        catch (Exception ex)
        {
            Log.Info($"Video cache cleanup: {ex.Message}");
        }
    }

    [RelayCommand]
    void ToggleInfo()
    {
        ShowInfo = !ShowInfo;
        if (ShowInfo && Details is null && _cts is { } cts)
            _ = LoadDetailsAsync(Asset, cts.Token);
    }

    [RelayCommand]
    async Task ToggleFavoriteAsync()
    {
        var value = !IsFavorite;
        IsFavorite = value;
        if (!await _owner.SetFavoriteAsync([Asset.Id], value))
            IsFavorite = !value;
        else
            Asset = Asset with { IsFavorite = value };
    }

    [RelayCommand]
    void Share() => _owner.ShowShare([Asset.Id]);

    [RelayCommand]
    Task CopyImageAsync() => _owner.CopyImageAsync(Asset.Id);

    [RelayCommand]
    void AddToAlbum() => _owner.ShowAlbumPicker([Asset.Id]);

    [RelayCommand]
    Task DownloadAsync() => _owner.DownloadAsync([Asset.Id]);

    [RelayCommand]
    async Task TrashAsync()
    {
        if (await _owner.TrashAsync([Asset.Id]))
            Close();
    }

    void ReleaseOwnedImage()
    {
        var old = _ownedImage;
        _ownedImage = null;
        if (old is not null && !ReferenceEquals(old, Image))
            old.Dispose();
    }

    [RelayCommand]
    void Close()
    {
        _cts?.Cancel();
        _owner.CloseViewer();
        Image = null;
        ReleaseOwnedImage();
    }

    /// <summary>Opens the file on this PC in its default app (how local videos are played).</summary>
    [RelayCommand]
    void OpenFile()
    {
        if (DevicePath is { } path)
            ShellLauncher.Open(path);
    }

    [RelayCommand]
    void ShowInFolder()
    {
        if (DevicePath is { } path)
            ShellLauncher.Reveal(path);
    }

    [RelayCommand]
    void OpenInImmich()
    {
        if (AppServices.Settings.Current.WebUrl is { } web)
            ShellLauncher.Open($"{web.TrimEnd('/')}/photos/{Asset.Id}");
    }
}
