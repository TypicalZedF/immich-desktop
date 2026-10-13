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

        // A locked window asks for Windows Hello only when you come back to it, never in the background.
        Activated += (_, _) => (CurrentLock)?.OnWindowActivated();
        Deactivated += (_, _) => (CurrentLock)?.OnWindowDeactivated();
    }

    LockViewModel? CurrentLock => (DataContext as MainWindowViewModel)?.Content as LockViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel vm)
        {
            vm.PickFolder = PickFolderAsync;
            vm.PickImage = PickImageAsync;
            vm.PickSaveFile = PickSaveFileAsync;
            vm.CopyText = text => Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
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
        else if (e.Key == Key.Escape && DataContext is MainWindowViewModel { Overlay: ProfilePhotoViewModel photo } && !photo.IsSaving)
        {
            photo.CloseCommand.Execute(null);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    async Task<string?> PickImageAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Tr.T("Images")) { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.gif", "*.bmp"] }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    async Task<string?> PickSaveFileAsync(string title, string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = Path.GetExtension(suggestedName).TrimStart('.'),
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
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
