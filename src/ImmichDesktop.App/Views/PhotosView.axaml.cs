using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using ImmichDesktop.App.ViewModels;

namespace ImmichDesktop.App.Views;

public partial class PhotosView : UserControl
{
    public PhotosView()
    {
        InitializeComponent();

        var grid = this.FindControl<ListBox>("Grid")!;
        // Rows load their month and thumbnails only while on screen, and let go when scrolled away.
        grid.ContainerPrepared += (_, e) => (e.Container.DataContext as AssetRowViewModel)?.Realize();
        grid.ContainerClearing += (_, e) => (e.Container.DataContext as AssetRowViewModel)?.Unrealize();
        grid.SizeChanged += (_, e) => UpdateLayout(e.NewSize.Width);

        KeyDown += OnKeyDown;
    }

    PhotosViewModel? ViewModel => DataContext as PhotosViewModel;

    void UpdateLayout(double width)
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        // Leave room for the list padding and the scrollbar.
        ViewModel?.SetLayout(width - 12, scaling);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataContextProperty && ViewModel is { } vm)
        {
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PhotosViewModel.Viewer) && vm.Viewer is not null)
                    this.FindControl<Panel>("ViewerLayer")?.Focus();
            };
            if (this.FindControl<ListBox>("Grid") is { Bounds.Width: > 0 } grid)
                UpdateLayout(grid.Bounds.Width);
        }
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel?.Viewer is not { } viewer)
            return;
        switch (e.Key)
        {
            case Key.Escape:
                viewer.CloseCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Right:
                viewer.NextCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Left:
                viewer.PreviousCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
