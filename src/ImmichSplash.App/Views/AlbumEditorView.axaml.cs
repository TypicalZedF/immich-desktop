using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ImmichSplash.App.Views;

public partial class AlbumEditorView : UserControl
{
    public AlbumEditorView()
    {
        InitializeComponent();
        // Ready to type as soon as it opens; Esc closes it.
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("NameBox")?.Focus(), DispatcherPriority.Background);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && DataContext is ViewModels.AlbumEditorViewModel vm)
            {
                vm.CancelCommand.Execute(null);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }
}
