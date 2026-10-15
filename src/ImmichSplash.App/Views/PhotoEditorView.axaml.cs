using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ImmichSplash.App.Views;

public partial class PhotoEditorView : UserControl
{
    public PhotoEditorView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && DataContext is ViewModels.PhotoEditorViewModel vm)
            {
                vm.CloseCommand.Execute(null);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }
}
