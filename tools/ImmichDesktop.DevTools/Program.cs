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
///   icons &lt;assetsDir&gt;   regenerate app.ico, tray-*.ico and logo PNGs from the AppLogo control
///   screens &lt;outDir&gt;    render screenshots of every screen in light and dark mode
/// </summary>
static class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 2 || args[0] is not ("icons" or "screens"))
        {
            Console.Error.WriteLine("usage: devtools icons <assetsDir> | screens <outDir>");
            return 1;
        }

        if (args[0] == "screens")
            Screens.PrepareData(); // must run before anything touches AppPaths

        AppBuilder.Configure<ImmichDesktop.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();

        Directory.CreateDirectory(args[1]);
        if (args[0] == "icons")
            Icons.Generate(args[1]);
        else
            Screens.Generate(args[1]);
        return 0;
    }
}

static class Render
{
    public static byte[] ToPng(Control control, int size)
    {
        var host = new Window
        {
            Width = size,
            Height = size,
            Background = Brushes.Transparent,
            Content = control,
            SystemDecorations = SystemDecorations.None,
        };
        host.Show();
        Dispatcher.UIThread.RunJobs();

        control.Measure(new Size(size, size));
        control.Arrange(new Rect(0, 0, size, size));
        using var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        bitmap.Render(control);
        host.Close();

        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return stream.ToArray();
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
