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

        // Only prompt if the window is actually in front of the user (e.g. just opened from the tray);
        // otherwise the window's Activated event triggers it when they come back.
        DispatcherTimer.RunOnce(() =>
        {
            if (TopLevel.GetTopLevel(this) is Window { IsActive: true })
                vm.OnWindowActivated();
        }, TimeSpan.FromMilliseconds(400));
    }
}
