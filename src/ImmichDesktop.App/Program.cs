using System.Runtime.InteropServices;
using Avalonia;
using ImmichDesktop.App.Services;
using ImmichDesktop.Core;
using ImmichDesktop.Core.Platform;
using ImmichDesktop.Core.Services;

namespace ImmichDesktop.App;

internal static class Program
{
    /// <summary>Held for the lifetime of the process; null in design/preview tools.</summary>
    public static SingleInstance? Instance { get; private set; }

    /// <summary>Set when Windows launched us to deliver an SSO callback link and no other copy was running.</summary>
    public static string? StartupLink { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // Commands used by the installer/uninstaller.
        if (args.Contains("--quit"))
        {
            SingleInstance.RequestQuit(TimeSpan.FromSeconds(15));
            return 0;
        }
        if (args.Contains("--uninstall"))
        {
            SingleInstance.RequestQuit(TimeSpan.FromSeconds(15));
            UninstallCleanup.Run();
            return 0;
        }

        var link = args.FirstOrDefault(OAuthSession.IsCallback);

        Instance = SingleInstance.TryAcquire();
        if (Instance is null)
        {
            // Already running: hand over the SSO link, or bring the window forward (unless this is the silent autostart launch).
            if (link is not null)
                SingleInstance.Send(SingleInstance.Command.OpenLink, link);
            else if (!args.Contains(Autostart.BackgroundArgument))
                SingleInstance.Send(SingleInstance.Command.Activate);
            return 0;
        }
        StartupLink = link;

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Warn("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        Log.Info($"Starting {AppInfo.Name} {AppInfo.Version} on {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})");
        try
        {
            Instance.StartListening();
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception ex)
        {
            Log.Error("Fatal error", ex);
            throw;
        }
        finally
        {
            Instance.Dispose();
        }
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
