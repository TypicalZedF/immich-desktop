using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using ImmichDesktop.App.Controls;
using ImmichDesktop.App.Services.Mpv;

namespace ImmichDesktop.DevTools;

/// <summary>
/// video-test [software] [source]: plays a video with libmpv in a real window, on the graphics card or
/// (with "software") drawn into memory as the app does with graphics acceleration off.
/// Without a source it plays mpv's built-in test pattern. Prints when the first frame arrives and how
/// far playback got, then closes after 8 seconds. The window title is "mpv video test".
/// </summary>
static class VideoTest
{
    sealed class TestApp : Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());
    }

    public static int Run(string[] args)
    {
        var software = args.Contains("software");
        var source = args.FirstOrDefault(a => a != "software");
        Console.WriteLine($"libmpv available: {MpvPlayer.IsAvailable}");
        if (!MpvPlayer.IsAvailable)
            return 2;
        return AppBuilder.Configure<TestApp>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.AngleEgl, Win32RenderingMode.Wgl] })
            .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Glx, X11RenderingMode.Egl] })
            .AfterSetup(_ => Start(source ?? "av://lavfi:testsrc2=size=1280x720:rate=30", software))
            .StartWithClassicDesktopLifetime([]);
    }

    static void Start(string source, bool software)
    {
        var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var started = System.Diagnostics.Stopwatch.StartNew();
        var player = MpvPlayer.ForFile(source, software);
        Control view = software ? new Image { Stretch = Avalonia.Media.Stretch.Uniform } : new MpvVideoView { Player = player };
        if (view is Image image)
        {
            player.FrameChanged += () => image.Source = player.Frame;
            player.FrameReady += image.InvalidateVisual;
        }
        var window = new Window
        {
            Title = "mpv video test",
            Width = 960,
            Height = 540,
            Position = new PixelPoint(80, 80),
            Topmost = true,
            Content = view,
        };
        player.FirstFrame += () => Console.WriteLine($"First frame after {started.ElapsedMilliseconds} ms");
        player.Failed += () => Console.WriteLine("Playback failed");
        window.Show();
        player.Play();
        DispatcherTimer.RunOnce(() =>
            Console.WriteLine($"After 5 s: playing={player.IsPlaying} frame={player.HasFrame} time={player.Time.TotalSeconds:0.0}s"), TimeSpan.FromSeconds(5));
        DispatcherTimer.RunOnce(() =>
        {
            if (view is MpvVideoView mpvView)
                mpvView.Player = null;
            player.Dispose();
            window.Close();
            lifetime.Shutdown();
        }, TimeSpan.FromSeconds(8));
    }
}
