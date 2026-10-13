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
/// <remarks>
/// Changing menu items rebuilds the open menu, which made it close under the cursor while syncing.
/// So the menu only changes when the state itself changes (e.g. idle → backing up → paused);
/// live progress (counts, speed) goes in the hover tooltip instead.
/// </remarks>
public sealed class TrayController : IDisposable
{
    static readonly TimeSpan TooltipInterval = TimeSpan.FromSeconds(2);

    readonly App _app;
    readonly TrayIcon _tray;
    readonly NativeMenu _menu;
    readonly NativeMenuItem _statusItem;
    readonly NativeMenuItem[] _pauseItems;
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
    string? _statusText;
    bool? _isPaused;
    string? _tooltip;
    SyncState? _tooltipState;
    DateTime _lastTooltip;
    WindowIcon? _staticIcon;

    public TrayController(App app)
    {
        _app = app;
        _idle = LoadIcon("tray-idle");
        _paused = LoadIcon("tray-paused");
        _offline = LoadIcon("tray-offline");
        _error = LoadIcon("tray-error");
        _syncFrames = [LoadIcon("tray-sync-1"), LoadIcon("tray-sync-2"), LoadIcon("tray-sync-3"), LoadIcon("tray-sync-4")];

        _statusItem = new NativeMenuItem(Tr.T("Starting…")) { IsEnabled = false };
        _pauseItems =
        [
            Item(Tr.T("Pause for 1 hour"), () => AppServices.Engine.Pause(TimeSpan.FromHours(1))),
            Item(Tr.T("Pause until tomorrow"), () => AppServices.Engine.Pause(DateTime.Today.AddDays(1) - DateTime.Now)),
            Item(Tr.T("Pause until I resume"), () => AppServices.Engine.Pause()),
        ];
        _resumeItem = Item(Tr.T("Resume backup"), () => AppServices.Engine.Resume());
        _updateItem = Item(Tr.T("Restart to update"), () => AppServices.Updates.InstallAndRestart());
        _updateSeparator = new NativeMenuItemSeparator();

        _menu = new NativeMenu();
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(Item(Tr.T("Open Immich Desktop"), () => _app.ShowMainWindow()));
        _menu.Items.Add(Item(Tr.T("Open Immich in browser"), OpenWeb));
        _menu.Items.Add(new NativeMenuItemSeparator());
        foreach (var item in _pauseItems)
            _menu.Items.Add(item);
        _menu.Items.Add(_resumeItem);
        _menu.Items.Add(Item(Tr.T("Sync now"), () => AppServices.Engine.SyncNow()));
        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(Item(Tr.T("Settings"), () => _app.ShowMainWindow(ShellPage.Settings)));
        _menu.Items.Add(Item(Tr.T("Quit {0}", AppInfo.Name), () => _app.Quit()));

        _tray = new TrayIcon
        {
            Icon = _idle,
            ToolTipText = AppInfo.Name,
            Menu = _menu,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => _app.ShowMainWindow();
        TrayIcon.SetIcons(app, [_tray]);

        _animation = new DispatcherTimer(TimeSpan.FromMilliseconds(450), DispatcherPriority.Background, (_, _) =>
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
        if (AppServices.Updates.State == UpdateState.Ready && !_menu.Items.Contains(_updateItem))
        {
            _updateItem.Header = Tr.T("Restart to update to {0}", AppServices.Updates.Release?.Version);
            _menu.Items.Insert(0, _updateItem);
            _menu.Items.Insert(1, _updateSeparator);
        }
    });

    void Update(SyncStatus status)
    {
        var view = StatusPresenter.Describe(status, AppServices.Settings.Current);

        // Menu: coarse, rarely-changing text only.
        var statusText = status.State switch
        {
            SyncState.Uploading => Tr.T("Backing up…"),
            SyncState.Scanning => Tr.T("Checking for new photos…"),
            _ => view.Title,
        };
        if (statusText != _statusText)
        {
            _statusText = statusText;
            _statusItem.Header = statusText;
        }

        var paused = status.State == SyncState.Paused;
        if (paused != _isPaused)
        {
            _isPaused = paused;
            foreach (var item in _pauseItems)
                item.IsVisible = !paused;
            _resumeItem.IsVisible = paused;
        }

        // Tooltip: live detail, throttled.
        var tooltip = string.IsNullOrEmpty(view.TrayDetail) ? $"{AppInfo.Name}\n{view.Title}" : $"{AppInfo.Name}\n{view.Title}\n{view.TrayDetail}";
        if (tooltip.Length > 120)
            tooltip = tooltip[..119] + "…";
        if (tooltip != _tooltip && (status.State != _tooltipState || DateTime.UtcNow - _lastTooltip > TooltipInterval))
        {
            _tray.ToolTipText = tooltip;
            _tooltip = tooltip;
            _tooltipState = status.State;
            _lastTooltip = DateTime.UtcNow;
        }

        // Icon.
        if (view.Tone == StatusTone.Busy)
        {
            if (!_animation.IsEnabled)
            {
                _staticIcon = null;
                _frame = 0;
                _tray.Icon = _syncFrames[0];
                _animation.Start();
            }
            return;
        }

        _animation.Stop();
        var icon = view.Tone switch
        {
            StatusTone.Paused => _paused,
            StatusTone.Warning when status.State == SyncState.Offline => _offline,
            StatusTone.Warning or StatusTone.Error => _error,
            _ => _idle,
        };
        if (!ReferenceEquals(icon, _staticIcon))
        {
            _staticIcon = icon;
            _tray.Icon = icon;
        }
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
