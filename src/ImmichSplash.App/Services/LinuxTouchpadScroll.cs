using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ImmichSplash.App.Services;

/// <summary>
/// Linux: touchpads reach the app (through XWayland) as tiny fractions of a wheel click, about 3 clicks
/// for a whole swipe, and Avalonia scrolls a fixed distance per click, so touchpad scrolling crawled
/// compared with every other app. Fractional steps are made bigger before anything sees them; a mouse
/// wheel's whole clicks are left alone.
/// </summary>
public static class LinuxTouchpadScroll
{
    const double Factor = 3;

    sealed class ScaledWheelEventArgs(Interactive source, PointerWheelEventArgs e, TopLevel root, Vector delta)
#pragma warning disable AVP1001 // unstable constructor; fine on Avalonia 11
        : PointerWheelEventArgs(source, e.Pointer, root, e.GetPosition(root), e.Timestamp, e.Properties, e.KeyModifiers, delta);
#pragma warning restore AVP1001

    public static void Start()
    {
        if (!OperatingSystem.IsLinux())
            return;
        InputElement.PointerWheelChangedEvent.AddClassHandler<TopLevel>((root, e) =>
        {
            if (e is ScaledWheelEventArgs || e.Source is not Interactive source || !IsFractional(e.Delta))
                return;
            e.Handled = true;
            source.RaiseEvent(new ScaledWheelEventArgs(source, e, root, e.Delta * Factor));
        }, RoutingStrategies.Tunnel);
    }

    static bool IsFractional(Vector delta) =>
        Math.Abs(delta.X - Math.Round(delta.X)) > 0.001 || Math.Abs(delta.Y - Math.Round(delta.Y)) > 0.001;
}
