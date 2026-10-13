using Avalonia;
using Avalonia.Controls;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;

namespace ImmichDesktop.App.Controls;

/// <summary>The official Immich flower logo, as vector paths.</summary>
public partial class ImmichLogo : UserControl
{
    public static readonly StyledProperty<bool> MonochromeProperty =
        AvaloniaProperty.Register<ImmichLogo, bool>(nameof(Monochrome));

    readonly Dictionary<Path, IBrush?> _colors = [];

    public ImmichLogo() => InitializeComponent();

    /// <summary>Greyscale version, used for the "paused" tray icon.</summary>
    public bool Monochrome
    {
        get => GetValue(MonochromeProperty);
        set => SetValue(MonochromeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != MonochromeProperty || this.FindControl<Canvas>("Art") is not { } art)
            return;

        foreach (var path in art.Children.OfType<Path>())
        {
            if (!_colors.ContainsKey(path))
                _colors[path] = path.Fill;
            if (Monochrome && _colors[path] is ISolidColorBrush { Color: var c })
            {
                // Keep the petals distinguishable: map luminance into a mid-grey range.
                var luma = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
                var v = (byte)(110 + luma * 70);
                path.Fill = new SolidColorBrush(Color.FromRgb(v, v, v));
            }
            else
            {
                path.Fill = _colors[path];
            }
        }
    }
}
