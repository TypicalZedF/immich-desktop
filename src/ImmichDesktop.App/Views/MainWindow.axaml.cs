using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ImmichDesktop.App.ViewModels;

namespace ImmichDesktop.App.Views;

public partial class MainWindow : Window
{
    /// <summary>With an app lock set up, the window locks itself after this long without input.</summary>
    static readonly TimeSpan IdleLockAfter = TimeSpan.FromMinutes(5);

    readonly DispatcherTimer _idleTimer;
    DateTime _lastInput = DateTime.UtcNow;

    public MainWindow()
    {
        InitializeComponent();

        var icon = new Uri("avares://ImmichDesktop/Assets/app.ico");
        if (AssetLoader.Exists(icon))
            Icon = new WindowIcon(AssetLoader.Open(icon));

        AddHandler(PointerMovedEvent, (_, _) => _lastInput = DateTime.UtcNow, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, (_, _) => _lastInput = DateTime.UtcNow, RoutingStrategies.Tunnel, handledEventsToo: true);
        _idleTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm && DateTime.UtcNow - _lastInput > IdleLockAfter)
                vm.Lock();
        });
        _idleTimer.Start();
        Closed += (_, _) => _idleTimer.Stop();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel vm)
        {
            vm.PickFolder = PickFolderAsync;
            vm.GetWindowHandle = () => TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainWindowViewModel { Dialog: { } dialog })
        {
            dialog.CancelCommand.Execute(null);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
