using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace ImmichSplash.App.Controls;

/// <summary>
/// The video's progress bar: a generous target you can press and drag (the pointer is captured, so it keeps
/// working when you leave the bar), which shows where you'd land, and tells the player when you start,
/// move and let go. While dragging it follows your pointer, not the video, so it can't jitter.
/// </summary>
public sealed class VideoSeekBar : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<VideoSeekBar, double>(nameof(Value));

    /// <summary>Where the video is, from 0 to 1.</summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Pressed: dragging starts at this position (0 to 1).</summary>
    public event Action<double>? ScrubStarted;

    /// <summary>The pointer moved while dragging.</summary>
    public event Action<double>? ScrubMoved;

    /// <summary>Let go: the final position.</summary>
    public event Action<double>? ScrubCompleted;

    /// <summary>The pointer is over the bar at this position (0 to 1) and x pixels from its left; null when it leaves.</summary>
    public event Action<double?, double>? Hovered;

    bool _scrubbing, _hover;
    double _scrubValue;

    static VideoSeekBar()
    {
        AffectsRender<VideoSeekBar>(ValueProperty);
        FocusableProperty.OverrideDefaultValue<VideoSeekBar>(false);
        CursorProperty.OverrideDefaultValue<VideoSeekBar>(new Cursor(StandardCursorType.Hand));
    }

    public VideoSeekBar()
    {
        Height = 30;
    }

    public bool IsScrubbing => _scrubbing;

    double Fraction(PointerEventArgs e) =>
        Bounds.Width <= 0 ? 0 : Math.Clamp(e.GetPosition(this).X / Bounds.Width, 0, 1);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _scrubbing = true;
        _scrubValue = Fraction(e);
        e.Pointer.Capture(this);
        e.Handled = true;
        ScrubStarted?.Invoke(_scrubValue);
        Hovered?.Invoke(_scrubValue, e.GetPosition(this).X);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var fraction = Fraction(e);
        if (_scrubbing)
        {
            if (Math.Abs(fraction - _scrubValue) < 0.0002)
                return;
            _scrubValue = fraction;
            ScrubMoved?.Invoke(fraction);
            InvalidateVisual();
        }
        else if (!_hover)
        {
            _hover = true;
            InvalidateVisual();
        }
        Hovered?.Invoke(fraction, Math.Clamp(e.GetPosition(this).X, 0, Bounds.Width));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_scrubbing)
            return;
        _scrubValue = Fraction(e);
        _scrubbing = false;
        e.Pointer.Capture(null);
        ScrubCompleted?.Invoke(_scrubValue);
        if (!IsPointerOver)
        {
            _hover = false;
            Hovered?.Invoke(null, 0);
        }
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!_scrubbing)
            return;
        _scrubbing = false;
        ScrubCompleted?.Invoke(_scrubValue);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_scrubbing)
            return;
        _hover = false;
        Hovered?.Invoke(null, 0);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        if (width <= 0)
            return;
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size)); // the whole strip takes the pointer, not just the thin line

        var active = _scrubbing || _hover;
        var thickness = active ? 6.0 : 4.0;
        var centre = Bounds.Height / 2;
        var track = new Rect(0, centre - thickness / 2, width, thickness);
        var value = Math.Clamp(_scrubbing ? _scrubValue : Value, 0, 1);
        var played = new Rect(0, track.Y, width * value, thickness);

        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), null, track, thickness / 2, thickness / 2);
        context.DrawRectangle(Brushes.White, null, played, thickness / 2, thickness / 2);
        if (active)
            context.DrawEllipse(Brushes.White, null, new Point(Math.Clamp(width * value, 7, width - 7), centre), 7, 7);
    }
}
