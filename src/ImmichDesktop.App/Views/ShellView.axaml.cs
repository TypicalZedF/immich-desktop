using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using ImmichDesktop.App.ViewModels;

namespace ImmichDesktop.App.Views;

public partial class ShellView : UserControl
{
    public ShellView()
    {
        InitializeComponent();

        // Search suggestions: open on focus, arrow keys move through them, Esc closes, click picks.
        var search = this.FindControl<TextBox>("SearchBox")!;
        var suggestions = this.FindControl<ListBox>("SuggestionList")!;
        search.GotFocus += (_, _) => (DataContext as ShellViewModel)?.OnSearchFocused();
        search.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (DataContext is not ShellViewModel vm)
                return;
            switch (e.Key)
            {
                case Key.Down: vm.MoveSelection(+1); e.Handled = true; break;
                case Key.Up: vm.MoveSelection(-1); e.Handled = true; break;
                case Key.Escape: vm.CloseSuggestions(); e.Handled = true; break;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        suggestions.Tapped += (_, e) =>
        {
            if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
                return; // the remove-from-history button
            if (DataContext is ShellViewModel vm && suggestions.SelectedItem is SearchSuggestion chosen)
                vm.ChooseCommand.Execute(chosen);
        };

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
