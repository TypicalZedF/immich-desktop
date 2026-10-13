using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using ImmichDesktop.App.Services;
using ImmichDesktop.Core.Api;

namespace ImmichDesktop.App.Controls;

/// <summary>
/// A slippy map of where your photos were taken. Photos close together on screen merge into a glowing
/// bubble with a count (a heat map that sharpens as you zoom in). Drag to pan, scroll to zoom,
/// click a bubble to zoom in on it or, once it can't split further, to see its photos.
/// </summary>
public sealed class MapView : Control
{
    public static readonly StyledProperty<IReadOnlyList<MapMarker>?> MarkersProperty =
        AvaloniaProperty.Register<MapView, IReadOnlyList<MapMarker>?>(nameof(Markers));

    const double TileSize = 256;
    const double MinZoom = 1.5;
    const double MaxZoom = 18;
    const double ClusterCell = 64;

    static readonly IBrush LandFallback = new SolidColorBrush(Color.Parse("#E5E7EB"));
    static readonly IBrush LandFallbackDark = new SolidColorBrush(Color.Parse("#1F2937"));
    static readonly Typeface CountFont = new("Google Sans", FontStyle.Normal, FontWeight.SemiBold);

    MapTiles? _tiles;
    (double X, double Y)[] _points = []; // markers projected to 0..1 world coordinates
    double _zoom = 2, _centerX = 0.5, _centerY = 0.45;
    Point? _pressAt, _lastDrag;
    bool _dragged;
    bool _userMoved; // until you move the map, it keeps fitting your photos as it resizes
    readonly List<Cluster> _clusters = [];

    sealed class Cluster
    {
        public double SumX, SumY;
        public readonly List<int> Members = [];
        public Point Center => new(SumX / Members.Count, SumY / Members.Count);
    }

    public MapView()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public IReadOnlyList<MapMarker>? Markers
    {
        get => GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    /// <summary>A bubble that can't be split further was clicked.</summary>
    public event Action<IReadOnlyList<MapMarker>>? ClusterOpened;

    /// <summary>The visible area changed (for "Show photos in this area").</summary>
    public event Action? ViewChanged;

    bool IsDark => ActualThemeVariant == ThemeVariant.Dark;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _tiles = new MapTiles();
        _tiles.TileLoaded += InvalidateVisual;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _tiles?.Dispose();
        _tiles = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkersProperty)
        {
            _points = (Markers ?? []).Select(m => Project(m.Lat, m.Lon)).ToArray();
            _userMoved = false;
            FitToMarkers();
        }
        else if (change.Property == BoundsProperty && !_userMoved)
        {
            FitToMarkers();
        }
    }

    static (double X, double Y) Project(double lat, double lon)
    {
        lat = Math.Clamp(lat, -85.05112878, 85.05112878);
        var sin = Math.Sin(lat * Math.PI / 180);
        return ((lon + 180) / 360, 0.5 - Math.Log((1 + sin) / (1 - sin)) / (4 * Math.PI));
    }

    double WorldSize => TileSize * Math.Pow(2, _zoom);

    Point ToScreen(double x, double y) =>
        new((x - _centerX) * WorldSize + Bounds.Width / 2, (y - _centerY) * WorldSize + Bounds.Height / 2);

    (double X, double Y) ToWorld(Point p) =>
        (_centerX + (p.X - Bounds.Width / 2) / WorldSize, _centerY + (p.Y - Bounds.Height / 2) / WorldSize);

    /// <summary>Zooms to show where most photos are (ignoring a few far-away outliers).</summary>
    public void FitToMarkers()
    {
        if (_points.Length == 0 || Bounds.Width <= 0)
        {
            InvalidateVisual();
            return;
        }
        var xs = _points.Select(p => p.X).Order().ToArray();
        var ys = _points.Select(p => p.Y).Order().ToArray();
        var lo = (int)(xs.Length * 0.02);
        var hi = Math.Max(lo, xs.Length - 1 - lo);
        double minX = xs[lo], maxX = xs[hi], minY = ys[lo], maxY = ys[hi];
        _centerX = (minX + maxX) / 2;
        _centerY = (minY + maxY) / 2;
        var spanX = Math.Max(maxX - minX, 1e-6) * TileSize;
        var spanY = Math.Max(maxY - minY, 1e-6) * TileSize;
        // Leave room around the edges (and for the button at the bottom).
        var zoom = Math.Log2(Math.Min(Bounds.Width * 0.75 / spanX, Bounds.Height * 0.62 / spanY));
        _centerY += 0.06 * Bounds.Height / (TileSize * Math.Pow(2, Math.Clamp(zoom, MinZoom, 13)));
        _zoom = Math.Clamp(zoom, MinZoom, 13);
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    public void ZoomBy(double factor) => ZoomAt(_zoom + Math.Log2(factor), new Point(Bounds.Width / 2, Bounds.Height / 2));

    void ZoomAt(double zoom, Point anchor)
    {
        _userMoved = true;
        var (wx, wy) = ToWorld(anchor);
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        // Keep the point under the pointer still.
        _centerX = wx - (anchor.X - Bounds.Width / 2) / WorldSize;
        _centerY = wy - (anchor.Y - Bounds.Height / 2) / WorldSize;
        ClampCenter();
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    void ClampCenter()
    {
        _centerX = ((_centerX % 1) + 1) % 1;
        var half = Bounds.Height / 2 / WorldSize;
        _centerY = half >= 0.5 ? 0.5 : Math.Clamp(_centerY, half, 1 - half);
    }

    /// <summary>Photos currently on screen.</summary>
    public IReadOnlyList<MapMarker> VisibleMarkers()
    {
        var markers = Markers ?? [];
        var result = new List<MapMarker>();
        for (var i = 0; i < _points.Length; i++)
        {
            var p = ScreenOf(i);
            if (p.X >= 0 && p.Y >= 0 && p.X <= Bounds.Width && p.Y <= Bounds.Height)
                result.Add(markers[i]);
        }
        return result;
    }

    /// <summary>Screen position of a marker, using the copy of the world closest to the center.</summary>
    Point ScreenOf(int i)
    {
        var dx = _points[i].X - _centerX;
        dx -= Math.Round(dx);
        return new Point(dx * WorldSize + Bounds.Width / 2, (_points[i].Y - _centerY) * WorldSize + Bounds.Height / 2);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        var dark = IsDark;
        context.FillRectangle(dark ? LandFallbackDark : LandFallback, bounds);
        DrawTiles(context, dark);
        DrawClusters(context, dark);

        // Attribution (required by the map data licence).
        var text = new FormattedText(MapTiles.Attribution, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10.5,
            dark ? Brushes.White : Brushes.Black);
        var box = new Rect(Bounds.Width - text.Width - 12, Bounds.Height - text.Height - 6, text.Width + 12, text.Height + 6);
        context.FillRectangle(new SolidColorBrush(dark ? Color.Parse("#99000000") : Color.Parse("#B3FFFFFF")), box);
        context.DrawText(text, new Point(box.X + 6, box.Y + 3));
    }

    void DrawTiles(DrawingContext context, bool dark)
    {
        if (_tiles is null)
            return;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        // @2x tiles are 512px: pick the level whose pixels best match the screen's.
        var z = (int)Math.Clamp(Math.Round(_zoom + Math.Log2(scaling) - 1), 0, 19);
        var n = 1 << z;
        var tileOnScreen = WorldSize / n;
        var origin = ToScreen(0, 0);
        var firstX = (int)Math.Floor(-origin.X / tileOnScreen);
        var firstY = Math.Max(0, (int)Math.Floor(-origin.Y / tileOnScreen));
        var lastX = (int)Math.Floor((Bounds.Width - origin.X) / tileOnScreen);
        var lastY = Math.Min(n - 1, (int)Math.Floor((Bounds.Height - origin.Y) / tileOnScreen));
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality }))
        {
            for (var ty = firstY; ty <= lastY; ty++)
            {
                for (var tx = firstX; tx <= lastX; tx++)
                {
                    var wrapped = ((tx % n) + n) % n;
                    var dest = new Rect(origin.X + tx * tileOnScreen, origin.Y + ty * tileOnScreen, tileOnScreen + 0.5, tileOnScreen + 0.5);
                    if (_tiles.Get(z, wrapped, ty, dark) is { } tile)
                        context.DrawImage(tile, new Rect(tile.Size), dest);
                }
            }
        }
    }

    void DrawClusters(DrawingContext context, bool dark)
    {
        _clusters.Clear();
        if (_points.Length == 0)
            return;

        // Merge photos that are close together on screen.
        var cells = new Dictionary<(int, int), Cluster>();
        for (var i = 0; i < _points.Length; i++)
        {
            var p = ScreenOf(i);
            if (p.X < -ClusterCell || p.Y < -ClusterCell || p.X > Bounds.Width + ClusterCell || p.Y > Bounds.Height + ClusterCell)
                continue;
            var key = ((int)Math.Floor(p.X / ClusterCell), (int)Math.Floor(p.Y / ClusterCell));
            if (!cells.TryGetValue(key, out var cluster))
                cells[key] = cluster = new Cluster();
            cluster.SumX += p.X;
            cluster.SumY += p.Y;
            cluster.Members.Add(i);
        }
        _clusters.AddRange(cells.Values);

        var accent = dark ? Color.Parse("#ACCBFA") : Color.Parse("#4250AF");
        var onAccent = dark ? Brushes.Black : Brushes.White;
        var ring = new Pen(dark ? Brushes.Black : Brushes.White, 2);

        // Heat: soft glows, stronger where there are more photos.
        foreach (var c in _clusters)
        {
            var glow = 22 + 9 * Math.Log2(c.Members.Count + 1);
            var brush = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb((byte)Math.Min(170, 70 + 14 * Math.Log2(c.Members.Count + 1)), 0xF5, 0x6B, 0x3D), 0),
                    new GradientStop(Color.FromArgb(60, 0xFB, 0xBF, 0x24), 0.55),
                    new GradientStop(Color.FromArgb(0, 0xFB, 0xBF, 0x24), 1),
                },
            };
            context.DrawEllipse(brush, null, c.Center, glow, glow);
        }

        // Bubbles with counts on top.
        foreach (var c in _clusters)
        {
            var label = c.Members.Count >= 1000 ? $"{c.Members.Count / 1000.0:0.#}k" : c.Members.Count.ToString(CultureInfo.CurrentCulture);
            var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, CountFont, 12, onAccent);
            var radius = Math.Max(13, text.Width / 2 + 7);
            context.DrawEllipse(new SolidColorBrush(accent), ring, c.Center, radius, radius);
            context.DrawText(text, new Point(c.Center.X - text.Width / 2, c.Center.Y - text.Height / 2));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _pressAt = _lastDrag = e.GetPosition(this);
        _dragged = false;
        e.Pointer.Capture(this);
        if (e.ClickCount == 2)
        {
            ZoomAt(_zoom + 1, e.GetPosition(this));
            _pressAt = null;
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_lastDrag is { } last && _pressAt is { } start)
        {
            if (!_dragged && Math.Abs(p.X - start.X) + Math.Abs(p.Y - start.Y) > 4)
                _dragged = true;
            if (_dragged)
            {
                _userMoved = true;
                _centerX -= (p.X - last.X) / WorldSize;
                _centerY -= (p.Y - last.Y) / WorldSize;
                ClampCenter();
                _lastDrag = p;
                InvalidateVisual();
            }
        }
        Cursor = HitCluster(p) is not null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetPosition(this);
        if (_pressAt is not null && !_dragged && HitCluster(p) is { } cluster)
            OpenCluster(cluster);
        else if (_dragged)
            ViewChanged?.Invoke();
        _pressAt = _lastDrag = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ZoomAt(_zoom + e.Delta.Y * 0.5, e.GetPosition(this));
        e.Handled = true;
    }

    Cluster? HitCluster(Point p) =>
        _clusters.FirstOrDefault(c => Math.Abs(c.Center.X - p.X) < 18 && Math.Abs(c.Center.Y - p.Y) < 18);

    void OpenCluster(Cluster cluster)
    {
        var markers = Markers ?? [];
        // Zoom in while the bubble would still split apart; otherwise show its photos.
        var xs = cluster.Members.Select(i => ScreenOf(i)).ToList();
        var spread = xs.Max(p => p.X) - xs.Min(p => p.X) + xs.Max(p => p.Y) - xs.Min(p => p.Y);
        if (_zoom < MaxZoom - 1 && spread > 2 && cluster.Members.Count > 1)
            ZoomAt(_zoom + 2, cluster.Center);
        else
            ClusterOpened?.Invoke(cluster.Members.Select(i => markers[i]).ToList());
    }
}
