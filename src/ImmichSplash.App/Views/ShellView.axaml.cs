using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using ImmichSplash.App.ViewModels;

namespace ImmichSplash.App.Views;

public partial class ShellView : UserControl
{
    public ShellView()
    {
        InitializeComponent();

        // Search suggestions: open on focus, arrow keys move through them, Esc closes, click picks.
        var search = this.FindControl<TextBox>("SearchBox")!;
        var suggestions = this.FindControl<ListBox>("SuggestionList")!;
        var popup = this.FindControl<Avalonia.Controls.Primitives.Popup>("SearchPopup")!;
        // Only when you click or tab into the box: focus coming back on its own (the window being
        // reactivated, a popup closing) mustn't pop the suggestions open again.
        search.GotFocus += (_, e) =>
        {
            if (e.NavigationMethod != NavigationMethod.Unspecified || search.IsPointerOver)
                (DataContext as ShellViewModel)?.OnSearchFocused();
        };
        search.LostFocus += (_, _) =>
        {
            // Clicking a suggestion moves focus into the list; anywhere else closes them.
            if (DataContext is ShellViewModel vm && popup.Child is { IsKeyboardFocusWithin: false, IsPointerOver: false })
                vm.CloseSuggestions();
        };
        search.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (DataContext is not ShellViewModel vm)
                return;
            switch (e.Key)
            {
                case Key.Down: vm.MoveSelection(+1); e.Handled = true; break;
                case Key.Up: vm.MoveSelection(-1); e.Handled = true; break;
                case Key.Escape:
                    if (vm.IsSuggestionsOpen)
                        vm.CloseSuggestions();
                    else
                        vm.ClearSearchCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        suggestions.Tapped += (_, e) =>
        {
            if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
                return; // the remove-from-history button
            if (DataContext is ShellViewModel vm && suggestions.SelectedItem is SearchSuggestion chosen)
                vm.ChooseCommand.Execute(chosen);
        };

        DataContextChanged += (_, _) =>
        {
            if (_shell is not null)
                _shell.SearchStarted -= OnSearchStarted;
            _shell = DataContext as ShellViewModel;
            if (_shell is not null)
                _shell.SearchStarted += OnSearchStarted;
        };

        // Group headings in the sidebar are labels, not pages: they can't be selected.
        var nav = this.FindControl<ListBox>("Nav")!;
        // Clicking the page you're already on takes it back to its start (out of search results,
        // an album, a person…), like the web app.
        nav.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && e.Source is Visual source
                && source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is NavItem item
                && DataContext is ShellViewModel vm && ReferenceEquals(item, vm.SelectedNav))
                vm.ReturnToStart();
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
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

    ShellViewModel? _shell;

    /// <summary>A search opened: take focus off the box so the results get the keyboard.</summary>
    void OnSearchStarted() => TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
}
