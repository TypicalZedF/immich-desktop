using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ImmichDesktop.App.Services;
using ImmichDesktop.App.ViewModels;
using ImmichDesktop.App.Views;
using ImmichDesktop.Core;
using ImmichDesktop.Core.Platform;
using ImmichDesktop.Core.Settings;
using ImmichDesktop.Core.Sync;

namespace ImmichDesktop.App;

public partial class App : Application
{
    IClassicDesktopStyleApplicationLifetime? _desktop;
    TrayController? _tray;
    MainWindow? _window;
    DispatcherTimer? _autoUpdateTimer;
    bool _quitting;
    bool _wasBusy;
    bool _checkForUpdates;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        LinuxTouchpadScroll.Start();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            AppServices.Initialize();
            var settings = AppServices.Settings.Current;
            ThemeService.Apply(settings.Theme);

            _tray = new TrayController(this);
            if (Program.Instance is { } instance)
                instance.CommandReceived += (command, argument) => Dispatcher.UIThread.Post(() => OnCommand(command, argument));

            // Launched at login: stay in the tray. Otherwise (or if setup isn't finished) open the window.
            var background = desktop.Args?.Contains(Autostart.BackgroundArgument) == true;
            if (!background || !AppServices.Account.IsSignedIn)
                ShowMainWindow();

            // Keep OS integration pointing at the current executable (e.g. after an update or a move).
            if (settings.IsConfigured && settings.LaunchAtStartup)
                Autostart.SetEnabled(true);
            UrlProtocol.Register();

            AppServices.Network.Start();
            AppServices.Engine.Start();
            AppServices.Engine.StatusChanged += OnEngineStatus;
            AppServices.Settings.Changed += OnSettingsChanged;

            AppServices.Updates.QuitApp = Quit;
            AppServices.Updates.Changed += () => Dispatcher.UIThread.Post(TryAutoInstallUpdate);
            _checkForUpdates = settings.CheckForUpdates;
            AppServices.Updates.Start(_checkForUpdates);
            _autoUpdateTimer = new DispatcherTimer(TimeSpan.FromMinutes(10), DispatcherPriority.Background, (_, _) => TryAutoInstallUpdate());
            _autoUpdateTimer.Start();
            ApplyWallpaper(settings);

            if (Program.StartupLink is { } link)
                OAuthCallbacks.Deliver(link);

            if (_window is null)
                DispatcherTimer.RunOnce(TrimIfHidden, TimeSpan.FromSeconds(15));
            desktop.ShutdownRequested += (_, _) => Log.Info("Shutdown requested by the OS");
            desktop.Exit += (_, _) =>
            {
                try
                {
                    AppServices.ShutdownAsync().Wait(TimeSpan.FromSeconds(3));
                }
                catch (Exception ex)
                {
                    Log.Warn("Shutdown didn't finish cleanly", ex.InnerException ?? ex); // quitting anyway
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Applies a new language everywhere: the window is rebuilt and the tray menu recreated.</summary>
    public void ApplyLanguage(string? code)
    {
        Tr.SetLanguage(code);
        _tray?.Dispose();
        _tray = new TrayController(this);
        if (_window?.DataContext is MainWindowViewModel main && !main.IsLocked && main.Content is ShellViewModel)
            main.ShowShell(ShellPage.Settings);
    }

    void OnCommand(SingleInstance.Command command, string? argument)
    {
        switch (command)
        {
            case SingleInstance.Command.Activate:
                ShowMainWindow();
                break;
            case SingleInstance.Command.OpenLink when argument is not null:
                OAuthCallbacks.Deliver(argument);
                ShowMainWindow();
                break;
            case SingleInstance.Command.Quit:
                Quit();
                break;
        }
    }

    (bool, WallpaperSource, string?, int) _wallpaperConfig;

    /// <summary>Restarts the desktop background timer when its settings change.</summary>
    void ApplyWallpaper(AppSettings settings)
    {
        var config = (settings.WallpaperEnabled, settings.WallpaperSource, settings.WallpaperAlbumId, settings.WallpaperIntervalMinutes);
        if (config == _wallpaperConfig)
            return;
        _wallpaperConfig = config;
        AppServices.Wallpaper.Apply(settings);
    }

    void OnSettingsChanged(AppSettings settings) => Dispatcher.UIThread.Post(() =>
    {
        ApplyWallpaper(settings);
        if (settings.CheckForUpdates != _checkForUpdates)
        {
            _checkForUpdates = settings.CheckForUpdates;
            AppServices.Updates.Start(_checkForUpdates);
        }
    });

    /// <summary>When a sync run finishes while only the tray is showing, hand memory back and maybe update.</summary>
    void OnEngineStatus(SyncStatus status)
    {
        var busy = status.IsBusy;
        if (_wasBusy && !busy)
        {
            Dispatcher.UIThread.Post(() =>
            {
                TrimIfHidden();
                TryAutoInstallUpdate();
            }, DispatcherPriority.Background);
        }
        _wasBusy = busy;
    }

    /// <summary>Installs a downloaded update automatically, but only when nobody is looking and nothing is uploading.</summary>
    void TryAutoInstallUpdate()
    {
        if (_quitting || _window is not null || AppServices.Updates.State != UpdateState.Ready)
            return;
        if (!AppServices.Settings.Current.AutoInstallUpdates || AppServices.Engine.Status.IsBusy)
            return;
        Log.Info("Installing update automatically while idle");
        AppServices.Updates.InstallAndRestart();
    }

    void TrimIfHidden()
    {
        if (_window is null)
            MemoryTrimmer.Trim();
    }

    public void ShowMainWindow(ShellPage? page = null)
    {
        if (_quitting)
            return;

        if (_window is null)
        {
            var viewModel = new MainWindowViewModel();
            _window = new MainWindow { DataContext = viewModel };
            _window.Closed += (_, _) =>
            {
                viewModel.Dispose();
                _window = null;
                // Only the tray and the engine remain; release UI memory.
                Dispatcher.UIThread.Post(MemoryTrimmer.Trim, DispatcherPriority.Background);
            };
            _window.Show();
        }
        else
        {
            if (_window.WindowState == WindowState.Minimized)
                _window.WindowState = WindowState.Normal;
            _window.Show();
            // Coax Windows into bringing us to the front when activated from the background.
            _window.Topmost = true;
            _window.Topmost = false;
            _window.Activate();
        }

        if (page is { } p && _window.DataContext is MainWindowViewModel vm)
            vm.Navigate(p);
    }

    public void Quit()
    {
        if (_quitting)
            return;
        _quitting = true;
        Log.Info("Quitting");
        _autoUpdateTimer?.Stop();
        _window?.Close();
        _tray?.Dispose();
        _desktop?.Shutdown();
    }
}
