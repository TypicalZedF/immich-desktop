using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace ImmichSplash.App.Controls;

/// <summary>A month of the grid: where it starts and how tall it is (in the grid's scroll units).</summary>
public sealed record TimelineSection(string Label, int? Year, double Offset, double Height);

/// <summary>
/// The date rail on the right of the photo grid, like the Immich web app. Years down the side with a
/// dot per month; hovering anywhere on the rail shows a line and the month under the pointer; while
/// scrolling, the current month shows briefly. Drag to scroll smoothly, click to jump to a month.
/// </summary>
/// <remarks>
/// It sits over the right edge of the grid (the grid leaves room for it) and draws its date bubble to
/// the left, over the photos. The whole rail is hit-testable, not just the labels.
/// </remarks>
public sealed class TimelineScrubber : Control
{
    public static readonly StyledProperty<IReadOnlyList<TimelineSection>?> SectionsProperty =
        AvaloniaProperty.Register<TimelineScrubber, IReadOnlyList<TimelineSection>?>(nameof(Sections));

    /// <summary>Space above and below the rail, so the first and last labels aren't cut off.</summary>
    const double Pad = 14;
    const double MinLabelGap = 22;
    const double MinDotGap = 7;

    static readonly Typeface YearFont = new("Google Sans", FontStyle.Normal, FontWeight.Medium);

    static readonly Typeface BubbleFont = new("Google Sans", FontStyle.Normal, FontWeight.SemiBold);

    double _offset, _extent = 1, _viewport;
    double? _hoverY;
    Point? _pressAt;
    bool _dragged, _showScrollBubble;
    readonly DispatcherTimer _hideBubble;

    public TimelineScrubber()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        ClipToBounds = false;
        _hideBubble = new DispatcherTimer(TimeSpan.FromSeconds(1.2), DispatcherPriority.Background, (_, _) =>
        {
            _showScrollBubble = false;
            _hideBubble!.Stop();
            InvalidateVisual();
        });
    }

    public IReadOnlyList<TimelineSection>? Sections
    {
        get => GetValue(SectionsProperty);
        set => SetValue(SectionsProperty, value);
    }

    /// <summary>Asks the grid to scroll to an offset.</summary>
    public event Action<double>? SeekRequested;

    bool IsActive => Sections is { Count: > 1 } && _extent > _viewport + 1;

    /// <summary>Called by the grid when it scrolls or resizes.</summary>
    public void SetScroll(double offset, double extent, double viewport)
    {
        var moved = Math.Abs(offset - _offset) > 0.5;
        _offset = offset;
        _extent = Math.Max(1, extent);
        _viewport = viewport;
        if (moved && _pressAt is null && _hoverY is null)
        {
            _showScrollBubble = true;
            _hideBubble.Stop();
            _hideBubble.Start();
        }
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SectionsProperty)
            InvalidateVisual();
    }

    double RailHeight => Math.Max(1, Bounds.Height - Pad * 2);

    /// <summary>The scroll range maps proportionally onto the rail, like the web app (busy months get more room).</summary>
    double ToY(double offset) => Pad + Math.Clamp(offset / _extent, 0, 1) * RailHeight;

    double ToOffset(double y) => Math.Clamp((y - Pad) / RailHeight, 0, 1) * _extent;

    double MaxOffset => Math.Max(0, _extent - _viewport);

    TimelineSection? SectionAt(double offset)
    {
        if (Sections is not { Count: > 0 } sections)
            return null;
        var found = sections[0];
        foreach (var s in sections)
        {
            if (s.Offset > offset + 0.5)
                break;
            found = s;
        }
        return found;
    }

    /// <summary>A theme color for the current light/dark variant.</summary>
    T Resource<T>(string key, T fallback) where T : class =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is T typed ? typed : fallback;

    public override void Render(DrawingContext context)
    {
        // Transparent, but it makes the whole rail respond to the pointer (not just the text).
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (!IsActive || Sections is not { } sections)
            return;

        // Styled like the Immich web app: a dotted column of months on the right edge, compact year
        // labels beside it, a line where you are, and a tag with the month under the pointer.
        var secondary = Resource<IBrush>("AppTextSecondaryBrush", Brushes.Gray);
        var tertiary = Resource<IBrush>("AppTextTertiaryBrush", Brushes.Gray);
        var primary = Resource<IBrush>("AppPrimaryBrush", Brushes.SteelBlue);
        var text = Resource<IBrush>("AppTextBrush", Brushes.Black);
        var card = Resource<IBrush>("AppCardBrush", Brushes.White);
        var cardBorder = Resource<IBrush>("AppCardBorderBrush", Brushes.LightGray);

        var dotX = Bounds.Width - 7;
        var labelRight = Bounds.Width - 15;

        var lastLabelY = double.NegativeInfinity;
        var lastDotY = double.NegativeInfinity;
        int? lastYear = null;
        foreach (var s in sections)
        {
            var y = ToY(s.Offset);
            if (y - lastDotY >= 4)
            {
                context.DrawEllipse(tertiary, null, new Point(dotX, y), 1.2, 1.2);
                lastDotY = y;
            }
            if (s.Year is { } year && year != lastYear && y - lastLabelY >= 13)
            {
                var label = new FormattedText(year.ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, YearFont, 11, secondary);
                context.DrawText(label, new Point(labelRight - label.Width, y - label.Height / 2));
                lastLabelY = y;
            }
            lastYear = s.Year;
        }

        // Where you are now: a short line over the dots, clear of the year labels.
        var at = ToY(_offset);
        context.FillRectangle(primary, new Rect(labelRight + 2, at - 1, Bounds.Width - labelRight - 2, 2));

        // The month under the pointer (or, while scrolling, where you are).
        var hovering = _hoverY is not null && _pressAt is null;
        var tagY = hovering ? _hoverY!.Value : _pressAt is not null || _showScrollBubble ? at : (double?)null;
        if (tagY is not { } ty || SectionAt(hovering ? ToOffset(ty) : _offset) is not { } section)
            return;
        var tag = new FormattedText(section.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, BubbleFont, 12.5, text);
        var box = new Rect(Bounds.Width - 34 - tag.Width - 20, Math.Clamp(ty - 26, 0, Math.Max(0, Bounds.Height - 26)), tag.Width + 20, 26);
        context.DrawRectangle(card, new Pen(cardBorder, 1), box, 7, 7,
            new BoxShadows(new BoxShadow { Blur = 10, OffsetY = 2, Color = Color.FromArgb(50, 0, 0, 0) }));
        context.DrawText(tag, new Point(box.X + 10, box.Y + (box.Height - tag.Height) / 2));
        // A line from the tag to the rail, under the tag.
        context.FillRectangle(primary, new Rect(box.X + 4, box.Bottom - 1.5, Bounds.Width - box.X - 4, 1.5));
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _hoverY = e.GetPosition(this).Y;
        _showScrollBubble = false;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_pressAt is null)
            _hoverY = null;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_pressAt is { } start)
        {
            if (!_dragged && Math.Abs(p.Y - start.Y) > 3)
                _dragged = true;
            if (_dragged)
                SeekRequested?.Invoke(Math.Min(ToOffset(p.Y), MaxOffset)); // smooth while dragging
        }
        _hoverY = p.X >= 0 && p.X <= Bounds.Width && p.Y >= 0 && p.Y <= Bounds.Height ? p.Y : _pressAt is not null ? p.Y : null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsActive || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _pressAt = e.GetPosition(this);
        _dragged = false;
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressAt is { } start && !_dragged)
        {
            // A click jumps to the start of the month under the pointer.
            var section = SectionAt(ToOffset(start.Y));
            SeekRequested?.Invoke(Math.Min(section?.Offset ?? ToOffset(start.Y), MaxOffset));
        }
        _pressAt = null;
        _dragged = false;
        e.Pointer.Capture(null);
        var p = e.GetPosition(this);
        if (p.X < 0 || p.X > Bounds.Width || p.Y < 0 || p.Y > Bounds.Height)
            _hoverY = null;
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pressAt = null;
        _dragged = false;
        InvalidateVisual();
    }
}
