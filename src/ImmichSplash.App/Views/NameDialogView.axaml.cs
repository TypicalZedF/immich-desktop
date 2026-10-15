using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ImmichSplash.App.Views;

public partial class NameDialogView : UserControl
{
    public NameDialogView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (this.FindControl<TextBox>("TextBox") is { } box)
            {
                box.Focus();
                box.SelectAll();
            }
        }, DispatcherPriority.Background);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && DataContext is ViewModels.NameDialogViewModel vm)
            {
                vm.CancelCommand.Execute(null);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }
}
