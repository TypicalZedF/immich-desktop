using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ImmichDesktop.App.ViewModels;

namespace ImmichDesktop.App.Views;

public partial class LockView : UserControl
{
    public LockView() => InitializeComponent();

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is not LockViewModel vm)
            return;

        if (vm.UsesPassword)
            this.FindControl<TextBox>("PasswordBox")?.Focus();

        // Let the window finish appearing first so the Hello prompt opens on top of it.
        DispatcherTimer.RunOnce(vm.OnShown, TimeSpan.FromMilliseconds(400));
    }
}
