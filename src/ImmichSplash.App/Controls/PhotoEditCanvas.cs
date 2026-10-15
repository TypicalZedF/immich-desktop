using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ImmichSplash.App.Controls;

/// <summary>
/// The editor's picture: shows the photo turned and flipped as it will be, with a crop frame on top that
/// you can move and pull by its corners and edges (optionally keeping a shape such as 4:3). The crop is
/// kept as fractions (0 to 1) of the picture as shown.
/// </summary>
public sealed class PhotoEditCanvas : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty = AvaloniaProperty.Register<PhotoEditCanvas, Bitmap?>(nameof(Source));
    public static readonly StyledProperty<int> RotationProperty = AvaloniaProperty.Register<PhotoEditCanvas, int>(nameof(Rotation));
    public static readonly StyledProperty<bool> MirrorHorizontalProperty = AvaloniaProperty.Register<PhotoEditCanvas, bool>(nameof(MirrorHorizontal));
    public static readonly StyledProperty<bool> MirrorVerticalProperty = AvaloniaProperty.Register<PhotoEditCanvas, bool>(nameof(MirrorVertical));

    /// <summary>The crop frame as fractions of the shown picture (left, top, width, height).</summary>
    public static readonly StyledProperty<Rect> CropProperty =
        AvaloniaProperty.Register<PhotoEditCanvas, Rect>(nameof(Crop), new Rect(0, 0, 1, 1), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Width divided by height the crop must keep, or null for a free crop.</summary>
    public static readonly StyledProperty<double?> AspectProperty = AvaloniaProperty.Register<PhotoEditCanvas, double?>(nameof(Aspect));

    public Bitmap? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public int Rotation { get => GetValue(RotationProperty); set => SetValue(RotationProperty, value); }
    public bool MirrorHorizontal { get => GetValue(MirrorHorizontalProperty); set => SetValue(MirrorHorizontalProperty, value); }
    public bool MirrorVertical { get => GetValue(MirrorVerticalProperty); set => SetValue(MirrorVerticalProperty, value); }
    public Rect Crop { get => GetValue(CropProperty); set => SetValue(CropProperty, value); }
    public double? Aspect { get => GetValue(AspectProperty); set => SetValue(AspectProperty, value); }

    enum Grab { None, Move, NW, N, NE, E, SE, S, SW, W }

    const double Margin = 28, HandleReach = 14, MinSize = 28;

    Grab _grab;
    Rect _grabStart;
    Point _grabFrom;

    static PhotoEditCanvas()
    {
        AffectsRender<PhotoEditCanvas>(SourceProperty, RotationProperty, MirrorHorizontalProperty, MirrorVerticalProperty, CropProperty);
        FocusableProperty.OverrideDefaultValue<PhotoEditCanvas>(false);
    }

    /// <summary>Size of the picture as shown (after turning), in its own pixels.</summary>
    Size Oriented
    {
        get
        {
            if (Source is not { } s)
                return new Size(4, 3);
            return Rotation is 90 or 270 ? new Size(s.PixelSize.Height, s.PixelSize.Width) : new Size(s.PixelSize.Width, s.PixelSize.Height);
        }
    }

    /// <summary>Where the shown picture sits in this control.</summary>
    Rect ImageRect
    {
        get
        {
            var o = Oriented;
            var room = new Size(Math.Max(1, Bounds.Width - Margin * 2), Math.Max(1, Bounds.Height - Margin * 2));
            var scale = Math.Min(room.Width / o.Width, room.Height / o.Height);
            var size = new Size(o.Width * scale, o.Height * scale);
            return new Rect((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2, size.Width, size.Height);
        }
    }

    Rect CropOnScreen
    {
        get
        {
            var r = ImageRect;
            var c = Crop;
            return new Rect(r.X + c.X * r.Width, r.Y + c.Y * r.Height, c.Width * r.Width, c.Height * r.Height);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Choosing a shape (or the picture changing) re-fits the frame to it.
        if (change.Property == AspectProperty || change.Property == RotationProperty || change.Property == SourceProperty)
            Crop = FitAspect(new Rect(0, 0, 1, 1));
    }

    /// <summary>The biggest frame with the wanted shape, centred in <paramref name="within"/> (fractions).</summary>
    Rect FitAspect(Rect within)
    {
        if (Aspect is not { } aspect || aspect <= 0)
            return within;
        var o = Oriented;
        // In fractions, width/height must equal aspect * (o.Height / o.Width).
        var fractionAspect = aspect * o.Height / o.Width;
        double w = within.Width, h = within.Width / fractionAspect;
        if (h > within.Height)
        {
            h = within.Height;
            w = h * fractionAspect;
        }
        return new Rect(within.X + (within.Width - w) / 2, within.Y + (within.Height - h) / 2, w, h);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (Source is not { } bitmap)
            return;
        var r = ImageRect;

        // The photo: turned about its centre, then flipped on screen.
        var turned = Rotation is 90 or 270;
        var drawn = turned ? new Size(r.Height, r.Width) : new Size(r.Width, r.Height);
        var matrix = Matrix.CreateRotation(Rotation * Math.PI / 180)
            * Matrix.CreateScale(MirrorHorizontal ? -1 : 1, MirrorVertical ? -1 : 1)
            * Matrix.CreateTranslation(r.Center.X, r.Center.Y);
        using (context.PushTransform(matrix))
            context.DrawImage(bitmap, new Rect(bitmap.Size), new Rect(-drawn.Width / 2, -drawn.Height / 2, drawn.Width, drawn.Height));

        // Dim everything outside the frame.
        var c = CropOnScreen;
        var dim = new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0));
        context.FillRectangle(dim, new Rect(r.X, r.Y, r.Width, Math.Max(0, c.Y - r.Y)));
        context.FillRectangle(dim, new Rect(r.X, c.Bottom, r.Width, Math.Max(0, r.Bottom - c.Bottom)));
        context.FillRectangle(dim, new Rect(r.X, c.Y, Math.Max(0, c.X - r.X), c.Height));
        context.FillRectangle(dim, new Rect(c.Right, c.Y, Math.Max(0, r.Right - c.Right), c.Height));

        // The frame, thirds, and handles.
        var white = new Pen(Brushes.White, 2);
        context.DrawRectangle(null, white, c);
        var thin = new Pen(new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255)), 1);
        for (var i = 1; i <= 2; i++)
        {
            context.DrawLine(thin, new Point(c.X + c.Width * i / 3, c.Y), new Point(c.X + c.Width * i / 3, c.Bottom));
            context.DrawLine(thin, new Point(c.X, c.Y + c.Height * i / 3), new Point(c.Right, c.Y + c.Height * i / 3));
        }
        var corner = new Pen(Brushes.White, 5);
        const double L = 18;
        foreach (var (x, y, dx, dy) in new[] { (c.X, c.Y, 1, 1), (c.Right, c.Y, -1, 1), (c.X, c.Bottom, 1, -1), (c.Right, c.Bottom, -1, -1) })
        {
            context.DrawLine(corner, new Point(x, y), new Point(x + dx * L, y));
            context.DrawLine(corner, new Point(x, y), new Point(x, y + dy * L));
        }
    }

    Grab HitTest(Point p)
    {
        var c = CropOnScreen;
        bool NearX(double x) => Math.Abs(p.X - x) <= HandleReach;
        bool NearY(double y) => Math.Abs(p.Y - y) <= HandleReach;
        var inX = p.X >= c.X - HandleReach && p.X <= c.Right + HandleReach;
        var inY = p.Y >= c.Y - HandleReach && p.Y <= c.Bottom + HandleReach;
        if (NearX(c.X) && NearY(c.Y)) return Grab.NW;
        if (NearX(c.Right) && NearY(c.Y)) return Grab.NE;
        if (NearX(c.X) && NearY(c.Bottom)) return Grab.SW;
        if (NearX(c.Right) && NearY(c.Bottom)) return Grab.SE;
        if (NearY(c.Y) && inX) return Grab.N;
        if (NearY(c.Bottom) && inX) return Grab.S;
        if (NearX(c.X) && inY) return Grab.W;
        if (NearX(c.Right) && inY) return Grab.E;
        return c.Contains(p) ? Grab.Move : Grab.None;
    }

    static StandardCursorType CursorFor(Grab g) => g switch
    {
        Grab.NW or Grab.SE => StandardCursorType.TopLeftCorner,
        Grab.NE or Grab.SW => StandardCursorType.TopRightCorner,
        Grab.N or Grab.S => StandardCursorType.SizeNorthSouth,
        Grab.E or Grab.W => StandardCursorType.SizeWestEast,
        Grab.Move => StandardCursorType.SizeAll,
        _ => StandardCursorType.Arrow,
    };

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var p = e.GetPosition(this);
        _grab = HitTest(p);
        if (_grab == Grab.None)
            return;
        _grabStart = CropOnScreen;
        _grabFrom = p;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_grab == Grab.None)
        {
            Cursor = new Cursor(CursorFor(HitTest(p)));
            return;
        }
        var img = ImageRect;
        Rect next;
        if (_grab == Grab.Move)
        {
            var dx = Math.Clamp(p.X - _grabFrom.X, img.Left - _grabStart.Left, img.Right - _grabStart.Right);
            var dy = Math.Clamp(p.Y - _grabFrom.Y, img.Top - _grabStart.Top, img.Bottom - _grabStart.Bottom);
            next = _grabStart.Translate(new Vector(dx, dy));
        }
        else
        {
            next = Resize(_grab, _grabStart, p, img);
        }
        Crop = new Rect((next.X - img.X) / img.Width, (next.Y - img.Y) / img.Height, next.Width / img.Width, next.Height / img.Height);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_grab == Grab.None)
            return;
        _grab = Grab.None;
        e.Pointer.Capture(null);
    }

    Rect Resize(Grab g, Rect start, Point p, Rect img)
    {
        double l = start.Left, t = start.Top, r = start.Right, b = start.Bottom;
        if (g is Grab.NW or Grab.W or Grab.SW) l = Math.Clamp(p.X, img.Left, r - MinSize);
        if (g is Grab.NE or Grab.E or Grab.SE) r = Math.Clamp(p.X, l + MinSize, img.Right);
        if (g is Grab.NW or Grab.N or Grab.NE) t = Math.Clamp(p.Y, img.Top, b - MinSize);
        if (g is Grab.SW or Grab.S or Grab.SE) b = Math.Clamp(p.Y, t + MinSize, img.Bottom);
        var free = new Rect(new Point(l, t), new Point(r, b));
        if (Aspect is not { } a || a <= 0)
            return free;

        if (g is Grab.NW or Grab.NE or Grab.SW or Grab.SE)
        {
            // Pulled by a corner: the opposite corner stays, the shape is kept.
            var anchor = new Point(g is Grab.NW or Grab.SW ? start.Right : start.Left, g is Grab.NW or Grab.NE ? start.Bottom : start.Top);
            var toRight = g is Grab.NE or Grab.SE;
            var toBottom = g is Grab.SW or Grab.SE;
            var w = Math.Max(free.Width, free.Height * a);
            var h = w / a;
            var maxW = toRight ? img.Right - anchor.X : anchor.X - img.Left;
            var maxH = toBottom ? img.Bottom - anchor.Y : anchor.Y - img.Top;
            var scale = Math.Min(1, Math.Min(maxW / w, maxH / h));
            (w, h) = (Math.Max(MinSize, w * scale), Math.Max(MinSize / a, h * scale));
            return new Rect(toRight ? anchor.X : anchor.X - w, toBottom ? anchor.Y : anchor.Y - h, w, h);
        }
        if (g is Grab.E or Grab.W)
        {
            var cy = start.Center.Y;
            var maxH = 2 * Math.Min(cy - img.Top, img.Bottom - cy);
            var w = Math.Min(free.Width, maxH * a);
            var h = w / a;
            return g == Grab.E ? new Rect(start.Left, cy - h / 2, w, h) : new Rect(start.Right - w, cy - h / 2, w, h);
        }
        {
            var cx = start.Center.X;
            var maxW = 2 * Math.Min(cx - img.Left, img.Right - cx);
            var h = Math.Min(free.Height, maxW / a);
            var w = h * a;
            return g == Grab.S ? new Rect(cx - w / 2, start.Top, w, h) : new Rect(cx - w / 2, start.Bottom - h, w, h);
        }
    }
}
