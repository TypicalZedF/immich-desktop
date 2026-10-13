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

namespace ImmichDesktop.App;

public partial class App : Application
{
    IClassicDesktopStyleApplicationLifetime? _desktop;
    TrayController? _tray;
    MainWindow? _window;
    bool _quitting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            AppServices.Initialize();
            var settings = AppServices.Settings.Current;
            ThemeService.Apply(settings.Theme);

            _tray = new TrayController(this);
            if (Program.Instance is { } instance)
                instance.ActivationRequested += () => Dispatcher.UIThread.Post(() => ShowMainWindow());

            // Launched at login: stay in the tray. Otherwise (or if setup isn't finished) open the window.
            var background = desktop.Args?.Contains(Autostart.BackgroundArgument) == true;
            if (!background || !AppServices.Account.IsSignedIn)
                ShowMainWindow();

            // Keep the autostart entry pointing at the current executable (e.g. after an update).
            if (settings.IsConfigured && settings.LaunchAtStartup)
                Autostart.SetEnabled(true);

            AppServices.Engine.Start();
            AppServices.Updates.StartBackgroundChecks();
            AppServices.Engine.StatusChanged += OnEngineStatus;
            if (_window is null)
                DispatcherTimer.RunOnce(TrimIfHidden, TimeSpan.FromSeconds(15));
            desktop.ShutdownRequested += (_, _) => Log.Info("Shutdown requested by the OS");
            desktop.Exit += (_, _) => AppServices.ShutdownAsync().Wait(TimeSpan.FromSeconds(3));
        }

        base.OnFrameworkInitializationCompleted();
    }

    bool _wasBusy;

    /// <summary>When a sync run finishes while only the tray is showing, hand memory back to the OS.</summary>
    void OnEngineStatus(Core.Sync.SyncStatus status)
    {
        var busy = status.IsBusy;
        if (_wasBusy && !busy)
            Dispatcher.UIThread.Post(TrimIfHidden, DispatcherPriority.Background);
        _wasBusy = busy;
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
        _window?.Close();
        _tray?.Dispose();
        _desktop?.Shutdown();
    }
}
