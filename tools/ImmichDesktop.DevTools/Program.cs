using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using ImmichDesktop.App;
using ImmichDesktop.App.Controls;
using Material.Icons;
using Material.Icons.Avalonia;

namespace ImmichDesktop.DevTools;

/// <summary>
/// Developer utilities that render the real UI headlessly.
///   icons &lt;assetsDir&gt;     regenerate app.ico, tray-*.ico and logo PNGs from the app logo (Assets/logo.png)
///   installer &lt;dir&gt;       regenerate the installer wizard images (BMP)
///   screens &lt;outDir&gt;      render screenshots of every screen in light and dark mode
///   video-test [software] [source]   play a video with libmpv in a real window
/// </summary>
static class Program
{
    static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "video-test")
            return VideoTest.Run(args[1..]);
        if (args.Length < 2 || args[0] is not ("icons" or "installer" or "screens"))
        {
            Console.Error.WriteLine("usage: devtools icons <assetsDir> | installer <dir> | screens <outDir>");
            return 1;
        }

        if (args[0] == "screens")
            Screens.PrepareData(); // must run before anything touches AppPaths

        AppBuilder.Configure<ImmichDesktop.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            
            .SetupWithoutStarting();

        Directory.CreateDirectory(args[1]);
        switch (args[0])
        {
            case "icons": Icons.Generate(args[1]); break;
            case "installer": InstallerArt.Generate(args[1]); break;
            default: Screens.Generate(args[1]); break;
        }
        return 0;
    }
}

static class Render
{
    public static byte[] ToPng(Control control, int size) => ToPng(control, size, size);

    public static byte[] ToPng(Control control, int width, int height)
    {
        using var bitmap = ToBitmap(control, width, height);
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return stream.ToArray();
    }

    /// <summary>24-bit BMP (what Inno Setup wants for wizard images), composited onto white.</summary>
    public static void SaveBmp(Control control, int width, int height, string path)
    {
        using var bitmap = ToBitmap(control, width, height);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        unsafe
        {
            fixed (byte* p = pixels)
                bitmap.CopyPixels(new PixelRect(0, 0, width, height), (nint)p, pixels.Length, stride);
        }

        var rowSize = (width * 3 + 3) & ~3;
        using var file = File.Create(path);
        using var w = new BinaryWriter(file);
        w.Write((byte)'B'); w.Write((byte)'M');
        w.Write(54 + rowSize * height); w.Write(0); w.Write(54);
        w.Write(40); w.Write(width); w.Write(height); w.Write((short)1); w.Write((short)24);
        w.Write(0); w.Write(rowSize * height); w.Write(2835); w.Write(2835); w.Write(0); w.Write(0);
        var row = new byte[rowSize];
        for (var y = height - 1; y >= 0; y--)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * stride + x * 4; // BGRA, premultiplied
                var a = pixels[i + 3];
                for (var c = 0; c < 3; c++)
                    row[x * 3 + c] = (byte)Math.Min(255, pixels[i + c] + (255 - a));
            }
            w.Write(row);
        }
    }

    static RenderTargetBitmap ToBitmap(Control control, int width, int height)
    {
        var size = Math.Max(width, height);
        var host = new Window
        {
            Width = width,
            Height = height,
            Background = Brushes.Transparent,
            Content = control,
            SystemDecorations = SystemDecorations.None,
        };
        host.Show();
        Dispatcher.UIThread.RunJobs();

        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(control);
        host.Close();
        _ = size;
        return bitmap;
    }

    /// <summary>Writes a multi-resolution .ico with PNG-compressed entries (Windows Vista+).</summary>
    public static void WriteIco(string path, IReadOnlyList<(int Size, byte[] Png)> images)
    {
        using var file = File.Create(path);
        using var w = new BinaryWriter(file);
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images)
            w.Write(png);
    }
}

static class Icons
{
    static readonly int[] AppSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
    static readonly int[] TraySizes = [16, 20, 24, 32, 40, 48, 64];

    public static void Generate(string dir)
    {
        Ico(Path.Combine(dir, "app.ico"), AppSizes, s => Logo(s));
        File.WriteAllBytes(Path.Combine(dir, "logo-256.png"), Render.ToPng(Logo(256), 256));
        File.WriteAllBytes(Path.Combine(dir, "logo-512.png"), Render.ToPng(Logo(512), 512));

        Ico(Path.Combine(dir, "tray-idle.ico"), TraySizes, s => Logo(s));
        Ico(Path.Combine(dir, "tray-paused.ico"), TraySizes, s => WithBadge(Logo(s, mono: true), s, "#4B5160", MaterialIconKind.Pause, 0));
        Ico(Path.Combine(dir, "tray-offline.ico"), TraySizes, s => WithBadge(Logo(s), s, "#D98A00", MaterialIconKind.CloudOffOutline, 0));
        Ico(Path.Combine(dir, "tray-error.ico"), TraySizes, s => WithBadge(Logo(s), s, "#D93025", MaterialIconKind.Exclamation, 0));
        for (var frame = 0; frame < 4; frame++)
        {
            var angle = frame * 45; // the sync glyph is symmetric under 180°, so 4 x 45° is one full visual turn
            Ico(Path.Combine(dir, $"tray-sync-{frame + 1}.ico"), TraySizes, s => WithBadge(Logo(s), s, "#1E9E5A", MaterialIconKind.Sync, angle));
        }
        Console.WriteLine($"Icons written to {Path.GetFullPath(dir)}");
    }

    static void Ico(string path, int[] sizes, Func<int, Control> build) =>
        Render.WriteIco(path, sizes.Select(s => (s, Render.ToPng(build(s), s))).ToList());

    static Control Logo(int size, bool mono = false) => new AppLogo { Width = size, Height = size, Monochrome = mono };

    /// <summary>Status badge in the bottom-right corner, like the badges on cloud-drive tray icons.</summary>
    static Control WithBadge(Control logo, int size, string color, MaterialIconKind glyph, double angle)
    {
        var badge = Math.Round(size * (size < 24 ? 0.62 : 0.56));
        var ring = Math.Max(1, Math.Round(size / 16.0));
        return new Panel
        {
            Width = size,
            Height = size,
            Children =
            {
                logo,
                new Border
                {
                    Width = badge,
                    Height = badge,
                    CornerRadius = new CornerRadius(badge / 2),
                    Background = Brush.Parse(color),
                    BorderBrush = Brushes.White,
                    BorderThickness = new Thickness(ring),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Child = new MaterialIcon
                    {
                        Kind = glyph,
                        Foreground = Brushes.White,
                        Width = badge * (size < 24 ? 0.85 : 0.68),
                        Height = badge * (size < 24 ? 0.85 : 0.68),
                        RenderTransform = new RotateTransform(angle),
                    },
                },
            },
        };
    }
}

/// <summary>Wizard artwork for the Inno Setup installer, at 100/150/200% scale.</summary>
static class InstallerArt
{
    public static void Generate(string dir)
    {
        foreach (var (scale, suffix) in new[] { (1.0, "100"), (1.5, "150"), (2.0, "200") })
        {
            int W(double v) => (int)Math.Round(v * scale);

            // Large image: left side of the Welcome and Finish pages.
            var large = new Panel
            {
                Width = W(164),
                Height = W(314),
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.Parse("#EEF0FA"), 0), new GradientStop(Colors.White, 1) },
                },
                Children =
                {
                    new AppLogo { Width = W(96), Height = W(96), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, W(40)) },
                    new TextBlock { Text = "Immich Desktop", FontSize = W(15), FontWeight = FontWeight.Medium, Foreground = new SolidColorBrush(Color.Parse("#1F2430")), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, W(30)) },
                },
            };
            Render.SaveBmp(large, W(164), W(314), Path.Combine(dir, $"wizard-large-{suffix}.bmp"));

            // Small image: top-right corner of the other pages.
            var small = new Panel
            {
                Width = W(55),
                Height = W(55),
                Background = Brushes.White,
                Children = { new AppLogo { Width = W(46), Height = W(46) } },
            };
            Render.SaveBmp(small, W(55), W(55), Path.Combine(dir, $"wizard-small-{suffix}.bmp"));
        }
        Console.WriteLine($"Installer images written to {Path.GetFullPath(dir)}");
    }
}
