using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ImmichDesktop.App.Controls;

/// <summary>The app's own logo (a splash of paint drops, Assets/logo.png from logo.svg), in colour or greyscale.</summary>
public sealed class AppLogo : Image
{
    public static readonly StyledProperty<bool> MonochromeProperty =
        AvaloniaProperty.Register<AppLogo, bool>(nameof(Monochrome));

    static readonly Lazy<Bitmap> Colour = new(() => new Bitmap(AssetLoader.Open(new Uri("avares://ImmichDesktop/Assets/logo.png"))));
    static readonly Lazy<Bitmap> Grey = new(() => ToGrey(Colour.Value));

    public AppLogo()
    {
        Source = Colour.Value;
        Stretch = Stretch.Uniform;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    /// <summary>Greyscale version, used for the "paused" tray icon.</summary>
    public bool Monochrome
    {
        get => GetValue(MonochromeProperty);
        set => SetValue(MonochromeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MonochromeProperty)
            Source = Monochrome ? Grey.Value : Colour.Value;
    }

    static unsafe Bitmap ToGrey(Bitmap colour)
    {
        var grey = new WriteableBitmap(colour.PixelSize, colour.Dpi, Avalonia.Platform.PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = grey.Lock();
        colour.CopyPixels(new PixelRect(colour.PixelSize), buffer.Address, buffer.RowBytes * colour.PixelSize.Height, buffer.RowBytes);
        for (var y = 0; y < colour.PixelSize.Height; y++)
        {
            var row = (byte*)buffer.Address + y * buffer.RowBytes;
            for (var x = 0; x < colour.PixelSize.Width; x++)
            {
                var p = row + x * 4;
                // Plain luminance (pixels are premultiplied, so it stays within the alpha).
                var v = (byte)Math.Round(0.114 * p[0] + 0.587 * p[1] + 0.299 * p[2]);
                p[0] = p[1] = p[2] = v;
            }
        }
        return grey;
    }
}
