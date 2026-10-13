using Avalonia;
using Avalonia.Controls;
using ImmichDesktop.App.ViewModels;

namespace ImmichDesktop.App.Views;

public partial class ShellView : UserControl
{
    public ShellView()
    {
        InitializeComponent();

        // Group headings in the sidebar are labels, not pages: they can't be selected.
        var nav = this.FindControl<ListBox>("Nav")!;
        nav.ContainerPrepared += (_, e) =>
        {
            if (e.Container is not ListBoxItem item)
                return;
            var header = item.DataContext is NavHeader;
            item.IsEnabled = !header;
            item.Focusable = !header;
            if (item.DataContext is NavHeader h)
            {
                item.Padding = h.IsDivider ? new Thickness(26, 6, 26, 6) : new Thickness(26, 12, 26, 4);
                item.MinHeight = 0;
                item.Cursor = Avalonia.Input.Cursor.Default;
            }
            else
            {
                item.ClearValue(PaddingProperty);
                item.ClearValue(MinHeightProperty);
                item.ClearValue(CursorProperty);
            }
        };
    }
}
