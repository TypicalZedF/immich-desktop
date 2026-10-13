using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichDesktop.App.Services;
using ImmichDesktop.Core;
using ImmichDesktop.Core.Api;
using SkiaSharp;

namespace ImmichDesktop.App.ViewModels;

/// <summary>
/// Changing your profile picture, like the Immich web app: pick a photo from your Immich library
/// (or this PC), then move, zoom and rotate it inside the circle and save.
/// </summary>
public sealed partial class ProfilePhotoViewModel : ViewModelBase, IDisposable
{
    readonly MainWindowViewModel _main;
    CancellationTokenSource? _loading;

    public ProfilePhotoViewModel(MainWindowViewModel main)
    {
        _main = main;
        Library = PhotosViewModel.ForPicking(cell => _ = OpenFromLibraryAsync(cell.Asset!));
    }

    /// <summary>Your Immich timeline, for picking a photo.</summary>
    public PhotosViewModel Library { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPicking), nameof(Title), nameof(Subtitle))]
    public partial PhotoCropViewModel? Cropper { get; set; }

    public bool IsPicking => Cropper is null;

    public string Title => IsPicking ? "Choose a profile picture" : "Adjust your picture";

    public string Subtitle => IsPicking
        ? "Pick a photo from your Immich library."
        : "Drag to move it, scroll or use the slider to zoom.";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsSaving { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    async Task OpenFromLibraryAsync(TimelineAsset asset)
    {
        if (!asset.IsImage || AppServices.Account.Client is not { } client)
            return;
        // The preview is already the right way up and plenty sharp for a 512px picture.
        await OpenAsync(ct => client.GetAssetImageAsync(asset.Id, AssetImageSize.Preview, ct));
    }

    [RelayCommand]
    async Task UploadFromComputerAsync()
    {
        var path = await _main.PickImageAsync("Choose a profile picture");
        if (path is null)
            return;
        await OpenAsync(ct => File.ReadAllBytesAsync(path, ct));
    }

    async Task OpenAsync(Func<CancellationToken, Task<byte[]>> load)
    {
        _loading?.Cancel();
        var cts = _loading = new CancellationTokenSource();
        Error = null;
        IsBusy = true;
        try
        {
            var bytes = await load(cts.Token);
            var bitmap = await Task.Run(() => Images.DecodeUpright(bytes, PhotoCropViewModel.MaxSourceSize), cts.Token);
            if (cts.IsCancellationRequested)
            {
                bitmap.Dispose();
                return;
            }
            Cropper?.Dispose();
            Cropper = new PhotoCropViewModel(bitmap);
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidDataException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not open the picture", ex);
            Error = $"Couldn't open that picture. {NetworkErrors.Describe(ex)}";
        }
        finally
        {
            if (ReferenceEquals(cts, _loading))
                IsBusy = false;
        }
    }

    [RelayCommand]
    void Back()
    {
        Error = null;
        Cropper?.Dispose();
        Cropper = null;
    }

    [RelayCommand]
    async Task SaveAsync()
    {
        if (Cropper is not { } cropper || IsSaving)
            return;
        Error = null;
        IsSaving = true;
        try
        {
            var jpeg = await Task.Run(cropper.Render);
            await AppServices.Profile.SetPictureAsync(jpeg);
            Close();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not change the profile picture", ex);
            Error = $"Couldn't change your picture. {NetworkErrors.Describe(ex)}";
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    void Close() => _main.CloseOverlay(this);

    public void Dispose()
    {
        _loading?.Cancel();
        Library.Dispose();
        Cropper?.Dispose();
    }
}

/// <summary>
/// The circle crop: the photo always covers the circle; drag to move, zoom up to 5×, turn in quarter turns.
/// Positions are in the viewport's units (DIPs), the crop is worked out in the photo's pixels.
/// </summary>
public sealed partial class PhotoCropViewModel : ViewModelBase, IDisposable
{
    /// <summary>Side of the square crop area on screen.</summary>
    public const double ViewportSize = 340;

    /// <summary>Photos are scaled down to this before cropping (still several times the uploaded size).</summary>
    public const int MaxSourceSize = 2048;

    public const double MinZoom = 1;
    public const double MaxZoom = 5;

    SKBitmap _source;
    double _offsetX, _offsetY; // photo center relative to the viewport center

    public PhotoCropViewModel(SKBitmap source)
    {
        _source = source;
        Image = Images.ToAvalonia(source);
    }

    [ObservableProperty]
    public partial Bitmap Image { get; private set; }

    [ObservableProperty]
    public partial double Zoom { get; set; } = MinZoom;

    partial void OnZoomChanged(double oldValue, double newValue)
    {
        // Slider zoom keeps the middle of the circle where it is.
        if (oldValue > 0)
        {
            _offsetX *= newValue / oldValue;
            _offsetY *= newValue / oldValue;
        }
        ClampAndUpdate();
    }

    public double Viewport => ViewportSize;
    public double Minimum => MinZoom;
    public double Maximum => MaxZoom;

    /// <summary>Screen units per photo pixel at 1× zoom: just enough to fill the circle.</summary>
    double BaseScale => ViewportSize / Math.Min(_source.Width, _source.Height);

    double Scale => BaseScale * Zoom;

    public double ImageWidth => _source.Width * Scale;
    public double ImageHeight => _source.Height * Scale;
    public double ImageLeft => (ViewportSize - ImageWidth) / 2 + _offsetX;
    public double ImageTop => (ViewportSize - ImageHeight) / 2 + _offsetY;

    public void Pan(double dx, double dy)
    {
        _offsetX += dx;
        _offsetY += dy;
        ClampAndUpdate();
    }

    /// <summary>Zooms keeping the point under the pointer (<paramref name="x"/>, <paramref name="y"/> in the viewport) in place.</summary>
    public void ZoomAt(double zoom, double x, double y)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(zoom - Zoom) < 0.0001)
            return;
        var cx = x - ViewportSize / 2;
        var cy = y - ViewportSize / 2;
        var ratio = zoom / Zoom;
        var offsetX = cx - (cx - _offsetX) * ratio;
        var offsetY = cy - (cy - _offsetY) * ratio;
        Zoom = zoom;
        _offsetX = offsetX;
        _offsetY = offsetY;
        ClampAndUpdate();
    }

    [RelayCommand]
    void ZoomIn() => Zoom = Math.Min(MaxZoom, Zoom + 0.5);

    [RelayCommand]
    void ZoomOut() => Zoom = Math.Max(MinZoom, Zoom - 0.5);

    [RelayCommand]
    void Rotate()
    {
        var rotated = Images.RotateClockwise(_source);
        _source.Dispose();
        _source = rotated;
        var old = Image;
        Image = Images.ToAvalonia(rotated);
        old.Dispose();
        _offsetX = _offsetY = 0;
        if (Zoom == MinZoom)
            ClampAndUpdate();
        else
            Zoom = MinZoom;
    }

    [RelayCommand]
    void Reset()
    {
        _offsetX = _offsetY = 0;
        if (Zoom == MinZoom)
            ClampAndUpdate();
        else
            Zoom = MinZoom;
    }

    /// <summary>The visible square, in photo pixels.</summary>
    public SKRect CropRect
    {
        get
        {
            var size = ViewportSize / Scale;
            var centerX = _source.Width / 2.0 - _offsetX / Scale;
            var centerY = _source.Height / 2.0 - _offsetY / Scale;
            return SKRect.Create((float)(centerX - size / 2), (float)(centerY - size / 2), (float)size, (float)size);
        }
    }

    /// <summary>The picture to upload. Safe to call off the UI thread.</summary>
    public byte[] Render() => Images.RenderCrop(_source, CropRect);

    void ClampAndUpdate()
    {
        // Never let an edge of the photo come inside the circle's square.
        var maxX = Math.Max(0, (ImageWidth - ViewportSize) / 2);
        var maxY = Math.Max(0, (ImageHeight - ViewportSize) / 2);
        _offsetX = Math.Clamp(_offsetX, -maxX, maxX);
        _offsetY = Math.Clamp(_offsetY, -maxY, maxY);
        OnPropertyChanged(nameof(ImageWidth));
        OnPropertyChanged(nameof(ImageHeight));
        OnPropertyChanged(nameof(ImageLeft));
        OnPropertyChanged(nameof(ImageTop));
    }

    public void Dispose()
    {
        Image.Dispose();
        _source.Dispose();
    }
}
