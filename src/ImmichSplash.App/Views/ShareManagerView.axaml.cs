using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ImmichSplash.App.Views;

public partial class ShareManagerView : UserControl
{
    public ShareManagerView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && DataContext is ViewModels.ShareManagerViewModel vm)
            {
                vm.CloseCommand.Execute(null);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }
}
