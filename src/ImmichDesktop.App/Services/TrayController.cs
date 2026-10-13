using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using ImmichDesktop.App.ViewModels;
using ImmichDesktop.Core;
using ImmichDesktop.Core.Platform;
using ImmichDesktop.Core.Sync;

namespace ImmichDesktop.App.Services;

/// <summary>
/// System tray icon. The icon reflects sync state (backed up / syncing (animated) / paused / offline / needs attention),
/// left-click opens the app, right-click offers quick controls.
/// </summary>
public sealed class TrayController : IDisposable
{
    readonly App _app;
    readonly TrayIcon _tray;
    readonly NativeMenuItem _statusItem;
    readonly NativeMenuItem _detailItem;
    readonly NativeMenuItem _pauseMenu;
    readonly NativeMenuItem _resumeItem;
    readonly NativeMenuItem _updateItem;
    readonly NativeMenuItemSeparator _updateSeparator;
    readonly DispatcherTimer _animation;
    readonly WindowIcon _idle;
    readonly WindowIcon _paused;
    readonly WindowIcon _offline;
    readonly WindowIcon _error;
    readonly WindowIcon[] _syncFrames;
    int _frame;
    string? _lastTooltip;

    public TrayController(App app)
    {
        _app = app;
        _idle = LoadIcon("tray-idle");
        _paused = LoadIcon("tray-paused");
        _offline = LoadIcon("tray-offline");
        _error = LoadIcon("tray-error");
        _syncFrames = [LoadIcon("tray-sync-1"), LoadIcon("tray-sync-2"), LoadIcon("tray-sync-3"), LoadIcon("tray-sync-4")];

        _statusItem = new NativeMenuItem("Starting…") { IsEnabled = false };
        _detailItem = new NativeMenuItem("") { IsEnabled = false };

        var open = Item("Open Immich Desktop", () => _app.ShowMainWindow());
        var web = Item("Open Immich in browser", OpenWeb);

        _pauseMenu = new NativeMenuItem("Pause backup") { Menu = new NativeMenu() };
        _pauseMenu.Menu.Items.Add(Item("For 1 hour", () => AppServices.Engine.Pause(TimeSpan.FromHours(1))));
        _pauseMenu.Menu.Items.Add(Item("For 8 hours", () => AppServices.Engine.Pause(TimeSpan.FromHours(8))));
        _pauseMenu.Menu.Items.Add(Item("Until tomorrow", () => AppServices.Engine.Pause(DateTime.Today.AddDays(1) - DateTime.Now)));
        _pauseMenu.Menu.Items.Add(Item("Until I resume", () => AppServices.Engine.Pause()));
        _resumeItem = Item("Resume backup", () => AppServices.Engine.Resume());

        var syncNow = Item("Sync now", () => AppServices.Engine.SyncNow());
        var settings = Item("Settings", () => _app.ShowMainWindow(ShellPage.Settings));
        _updateItem = Item("Restart to update", () => AppServices.Updates.InstallAndRestart());
        _updateSeparator = new NativeMenuItemSeparator();
        var quit = Item($"Quit {AppInfo.Name}", () => _app.Quit());

        var menu = new NativeMenu();
        menu.Items.Add(_statusItem);
        menu.Items.Add(_detailItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(open);
        menu.Items.Add(web);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_pauseMenu);
        menu.Items.Add(_resumeItem);
        menu.Items.Add(syncNow);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(settings);
        menu.Items.Add(quit);

        _tray = new TrayIcon
        {
            Icon = _idle,
            ToolTipText = AppInfo.Name,
            Menu = menu,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => _app.ShowMainWindow();
        TrayIcon.SetIcons(app, [_tray]);

        _animation = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, (_, _) =>
        {
            _frame = (_frame + 1) % _syncFrames.Length;
            _tray.Icon = _syncFrames[_frame];
        });

        AppServices.Engine.StatusChanged += OnStatusChanged;
        AppServices.Updates.Changed += OnUpdatesChanged;
        Update(AppServices.Engine.Status);
    }

    static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }

    static WindowIcon LoadIcon(string name)
    {
        foreach (var candidate in new[] { name, "app" })
        {
            var uri = new Uri($"avares://ImmichDesktop/Assets/{candidate}.ico");
            if (AssetLoader.Exists(uri))
                return new WindowIcon(AssetLoader.Open(uri));
        }
        throw new InvalidOperationException("Tray icon assets are missing.");
    }

    static void OpenWeb()
    {
        if (AppServices.Settings.Current.WebUrl is { } url)
            ShellLauncher.Open(url);
    }

    void OnStatusChanged(SyncStatus status) => Dispatcher.UIThread.Post(() => Update(status));

    void OnUpdatesChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (AppServices.Updates.State == UpdateState.Ready && _tray.Menu is { } menu && !menu.Items.Contains(_updateItem))
        {
            _updateItem.Header = $"Restart to update to {AppServices.Updates.Release?.Version.ToString(3)}";
            menu.Items.Insert(0, _updateItem);
            menu.Items.Insert(1, _updateSeparator);
        }
    });

    void Update(SyncStatus status)
    {
        var view = StatusPresenter.Describe(status, AppServices.Settings.Current);

        _statusItem.Header = view.Title;
        _detailItem.Header = view.TrayDetail;
        _detailItem.IsVisible = !string.IsNullOrEmpty(view.TrayDetail);

        var paused = status.State == SyncState.Paused;
        _pauseMenu.IsVisible = !paused;
        _resumeItem.IsVisible = paused;

        var tooltip = string.IsNullOrEmpty(view.TrayDetail) ? $"{AppInfo.Name}\n{view.Title}" : $"{AppInfo.Name}\n{view.Title}\n{view.TrayDetail}";
        if (tooltip.Length > 120)
            tooltip = tooltip[..119] + "…";
        if (tooltip != _lastTooltip)
        {
            _tray.ToolTipText = tooltip;
            _lastTooltip = tooltip;
        }

        if (view.Tone == StatusTone.Busy)
        {
            if (!_animation.IsEnabled)
            {
                _frame = 0;
                _tray.Icon = _syncFrames[0];
                _animation.Start();
            }
            return;
        }

        _animation.Stop();
        _tray.Icon = view.Tone switch
        {
            StatusTone.Paused => _paused,
            StatusTone.Warning when status.State == SyncState.Offline => _offline,
            StatusTone.Warning or StatusTone.Error => _error,
            _ => _idle,
        };
    }

    public void Dispose()
    {
        _animation.Stop();
        AppServices.Engine.StatusChanged -= OnStatusChanged;
        AppServices.Updates.Changed -= OnUpdatesChanged;
        _tray.IsVisible = false;
        _tray.Dispose();
    }
}
