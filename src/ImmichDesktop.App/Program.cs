using System.Runtime.InteropServices;
using Avalonia;
using ImmichDesktop.App.Services;
using ImmichDesktop.Core;
using ImmichDesktop.Core.Platform;
using Velopack;

namespace ImmichDesktop.App;

internal static class Program
{
    /// <summary>Held for the lifetime of the process; null in design/preview tools.</summary>
    public static SingleInstance? Instance { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // Must run first: handles install/update/uninstall hooks and exits early when invoked by the installer.
        var velopack = VelopackApp.Build();
        if (OperatingSystem.IsWindows())
            velopack.OnBeforeUninstallFastCallback(_ => UninstallCleanup.Run());
        velopack.Run();

        Instance = SingleInstance.TryAcquire();
        if (Instance is null)
        {
            // Already running: bring the existing window forward (unless this was the silent autostart launch).
            if (!args.Contains(Autostart.BackgroundArgument))
                SingleInstance.SignalExisting();
            return 0;
        }

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
            .WithInterFont()
            .LogToTrace();
}
