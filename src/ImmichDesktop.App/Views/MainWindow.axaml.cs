using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using ImmichDesktop.App.ViewModels;

namespace ImmichDesktop.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var icon = new Uri("avares://ImmichDesktop/Assets/app.ico");
        if (AssetLoader.Exists(icon))
            Icon = new WindowIcon(AssetLoader.Open(icon));
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel vm)
            vm.PickFolder = PickFolderAsync;
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
