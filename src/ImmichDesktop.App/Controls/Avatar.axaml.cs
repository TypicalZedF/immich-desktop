using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ImmichDesktop.App.Services;

namespace ImmichDesktop.App.Controls;

/// <summary>The signed-in user's avatar; updates itself when the profile picture changes.</summary>
public partial class Avatar : UserControl
{
    public static readonly StyledProperty<string?> InitialProperty =
        AvaloniaProperty.Register<Avatar, string?>(nameof(Initial));

    public Avatar() => InitializeComponent();

    public string? Initial
    {
        get => GetValue(InitialProperty);
        set => SetValue(InitialProperty, value);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (AppServices.Profile is { } profile)
            profile.Changed += Refresh;
        Refresh();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        if (AppServices.Profile is { } profile)
            profile.Changed -= Refresh;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == InitialProperty || change.Property == BoundsProperty)
            Refresh();
    }

    void Refresh()
    {
        if (this.FindControl<Border>("Circle") is not { } circle)
            return;
        var size = Math.Min(Bounds.Width, Bounds.Height);
        circle.CornerRadius = new CornerRadius(size / 2);
        circle.Background = AppServices.Profile?.Color;
        if (this.FindControl<TextBlock>("InitialText") is { } initial)
        {
            initial.Text = Initial;
            initial.FontSize = Math.Max(10, size * 0.42);
        }
        if (this.FindControl<Image>("Picture") is { } picture)
            picture.Source = AppServices.Profile?.Picture;
    }
}
