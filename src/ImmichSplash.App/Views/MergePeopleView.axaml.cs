using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ImmichSplash.App.Views;

public partial class MergePeopleView : UserControl
{
    public MergePeopleView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("Search")?.Focus(), DispatcherPriority.Background);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && DataContext is ViewModels.MergePeopleViewModel vm)
            {
                vm.CancelCommand.Execute(null);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }
}
