using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace ImmichDesktop.App.Controls;

/// <summary>A month of the grid: where it starts and how tall it is (in the grid's scroll units).</summary>
public sealed record TimelineSection(string Label, int? Year, double Offset, double Height);

/// <summary>
/// The date rail on the right of the photo grid, like the Immich web app: years down the side, a dot per
/// month, a bubble with the date under the pointer (and briefly while scrolling). Click or drag to jump.
/// </summary>
public sealed class TimelineScrubber : Control
{
    public static readonly StyledProperty<IReadOnlyList<TimelineSection>?> SectionsProperty =
        AvaloniaProperty.Register<TimelineScrubber, IReadOnlyList<TimelineSection>?>(nameof(Sections));

    static readonly Typeface YearFont = new("Google Sans", FontStyle.Normal, FontWeight.Medium);
    static readonly Typeface BubbleFont = new("Google Sans", FontStyle.Normal, FontWeight.SemiBold);

    double _offset, _extent = 1, _viewport;
    double? _hoverY;
    bool _dragging, _showScrollBubble;
    readonly DispatcherTimer _hideBubble;

    public TimelineScrubber()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
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

    /// <summary>Called by the grid when it scrolls or resizes.</summary>
    public void SetScroll(double offset, double extent, double viewport)
    {
        var moved = Math.Abs(offset - _offset) > 1;
        _offset = offset;
        _extent = Math.Max(1, extent);
        _viewport = viewport;
        if (moved && !_dragging)
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

    // The rail maps the whole scroll range onto its height (top padding for the first label).
    const double Pad = 10;
    double RailHeight => Math.Max(1, Bounds.Height - Pad * 2);
    double ToY(double offset) => Pad + offset / _extent * RailHeight;
    double ToOffset(double y) => Math.Clamp((y - Pad) / RailHeight, 0, 1) * _extent;

    TimelineSection? SectionAt(double offset)
    {
        if (Sections is not { Count: > 0 } sections)
            return null;
        var found = sections[0];
        foreach (var s in sections)
        {
            if (s.Offset > offset)
                break;
            found = s;
        }
        return found;
    }

    public override void Render(DrawingContext context)
    {
        if (Sections is not { Count: > 1 } sections || _extent <= _viewport)
            return;

        var secondary = this.FindResource("AppTextSecondaryBrush") as IBrush ?? Brushes.Gray;
        var tertiary = this.FindResource("AppTextTertiaryBrush") as IBrush ?? Brushes.Gray;
        var primary = this.FindResource("AppPrimaryBrush") as IBrush ?? Brushes.SteelBlue;
        var onPrimary = this.FindResource("AppOnPrimaryBrush") as IBrush ?? Brushes.White;
        var page = this.FindResource("AppPageBrush") as IBrush ?? Brushes.White;

        // A year label where each year starts (skipping ones that would overlap), dots for months.
        var lastLabelY = double.NegativeInfinity;
        int? lastYear = null;
        foreach (var s in sections)
        {
            var y = ToY(s.Offset);
            if (s.Year != lastYear && s.Year is { } year && y - lastLabelY > 18)
            {
                var text = new FormattedText(year.ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, YearFont, 11.5, secondary);
                context.DrawText(text, new Point(Bounds.Width - text.Width - 8, y - text.Height / 2));
                lastLabelY = y;
            }
            else if (y - lastLabelY > 8)
            {
                context.DrawEllipse(tertiary, null, new Point(Bounds.Width - 14, y), 1.6, 1.6);
            }
            lastYear = s.Year;
        }

        // Where you are: a short bar left of the labels.
        var at = ToY(_offset);
        context.DrawRectangle(primary, null, new Rect(4, at - 1.5, 18, 3), 1.5, 1.5);

        var bubbleY = _hoverY ?? (_showScrollBubble || _dragging ? at : (double?)null);
        if (bubbleY is { } by && SectionAt(_hoverY is { } h ? ToOffset(h) : _offset) is { } section)
        {
            var text = new FormattedText(section.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, BubbleFont, 12.5, onPrimary);
            // The rail is narrow; the bubble may extend left over the grid.
            var box = new Rect(-text.Width - 22 + 26, Math.Clamp(by - 13, 0, Bounds.Height - 26), text.Width + 22, 26);
            context.DrawRectangle(primary, new Pen(page, 1), box, 13, 13);
            context.DrawText(text, new Point(box.X + 11, box.Y + (box.Height - text.Height) / 2));
            if (_hoverY is { } hy)
                context.FillRectangle(primary, new Rect(4, hy - 0.5, Bounds.Width - 8, 1));
        }
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _hoverY = e.GetPosition(this).Y;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverY = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _dragging = true;
        e.Pointer.Capture(this);
        Seek(e.GetPosition(this).Y);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var y = e.GetPosition(this).Y;
        _hoverY = y >= 0 && y <= Bounds.Height ? y : null;
        if (_dragging)
            Seek(y);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    void Seek(double y)
    {
        // Jump to the start of the month under the pointer, like the web app.
        var offset = ToOffset(y);
        var target = SectionAt(offset)?.Offset ?? offset;
        SeekRequested?.Invoke(Math.Min(target, Math.Max(0, _extent - _viewport)));
    }
}
