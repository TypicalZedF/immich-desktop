using Avalonia.Input;
using Avalonia.Interactivity;
using ImmichDesktop.Core;

namespace ImmichDesktop.App.Services;

/// <summary>
/// Linux: logs the size of the first scroll steps after each start (touchpads report much smaller
/// steps through XWayland than a mouse wheel), to tune scrolling from real numbers.
/// </summary>
public static class ScrollDiagnostics
{
    const int Samples = 40;
    static int _logged;
    static RoutedEventArgs? _last; // the handler runs for every element on the route; log each event once

    public static void Start()
    {
        if (!OperatingSystem.IsLinux())
            return;
        InputElement.PointerWheelChangedEvent.AddClassHandler<InputElement>((_, e) =>
        {
            if (ReferenceEquals(e, _last) || _logged >= Samples)
                return;
            _last = e;
            _logged++;
            Log.Info($"Scroll step: delta {e.Delta.X:0.###},{e.Delta.Y:0.###} pointer {e.Pointer.Type} at {e.Timestamp} ms");
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }
}
