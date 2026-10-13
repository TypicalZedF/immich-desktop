using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ImmichDesktop.App.Controls;

public partial class AppLogo : UserControl
{
    public static readonly StyledProperty<bool> MonochromeProperty =
        AvaloniaProperty.Register<AppLogo, bool>(nameof(Monochrome));

    static readonly IBrush GrayBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#9AA0AE"), 0), new GradientStop(Color.Parse("#6B7180"), 1) },
    };

    IBrush? _colorBrush;

    public AppLogo() => InitializeComponent();

    /// <summary>Gray version, used for the "paused" tray icon.</summary>
    public bool Monochrome
    {
        get => GetValue(MonochromeProperty);
        set => SetValue(MonochromeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MonochromeProperty && this.FindControl<Border>("Tile") is { } tile)
        {
            _colorBrush ??= tile.Background;
            tile.Background = Monochrome ? GrayBrush : _colorBrush;
        }
    }
}
