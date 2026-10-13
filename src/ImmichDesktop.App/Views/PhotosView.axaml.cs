using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using ImmichDesktop.App.Controls;
using ImmichDesktop.App.ViewModels;
using ImmichDesktop.Core;

namespace ImmichDesktop.App.Views;

public partial class PhotosView : UserControl
{
    readonly ListBox _grid;
    readonly TimelineScrubber _scrubber;
    readonly MapView _map;
    readonly Panel _stage;
    readonly Image _viewerImage;
    ScrollViewer? _scroll;
    PhotosViewModel? _vm;
    PhotoViewerViewModel? _viewer;

    // Viewer zoom: scale and offset of the photo inside the stage.
    double _zoom = 1;
    Vector _pan;
    Point? _panFrom;

    public PhotosView()
    {
        InitializeComponent();

        _grid = this.FindControl<ListBox>("Grid")!;
        _scrubber = this.FindControl<TimelineScrubber>("Scrubber")!;
        _map = this.FindControl<MapView>("Map")!;
        _stage = this.FindControl<Panel>("ViewerStage")!;
        _viewerImage = this.FindControl<Image>("ViewerImage")!;

        // Rows load their month and thumbnails only while on screen, and let go when scrolled away.
        _grid.ContainerPrepared += (_, e) => (e.Container.DataContext as AssetRowViewModel)?.Realize();
        _grid.ContainerClearing += (_, e) => (e.Container.DataContext as AssetRowViewModel)?.Unrealize();
        _grid.SizeChanged += (_, e) => UpdateLayout(e.NewSize.Width);
        _grid.TemplateApplied += (_, _) => HookScroll();

        // The date rail scrolls the grid (and the wheel over it scrolls the photos, as you'd expect).
        _scrubber.SeekRequested += SeekTo;
        _scrubber.PointerWheelChanged += (_, e) =>
        {
            if (_scroll is null)
                return;
            _scroll.Offset = new Vector(_scroll.Offset.X, Math.Clamp(_scroll.Offset.Y - e.Delta.Y * 120, 0, Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height)));
            e.Handled = true;
        };

        // Map: zoom buttons, "show photos in this area", clicking a bubble.
        this.FindControl<Button>("ZoomInButton")!.Click += (_, _) => _map.ZoomBy(2);
        this.FindControl<Button>("ZoomOutButton")!.Click += (_, _) => _map.ZoomBy(0.5);
        this.FindControl<Button>("FitButton")!.Click += (_, _) => _map.FitToMarkers();
        this.FindControl<Button>("ShowAreaButton")!.Click += (_, _) => _vm?.ShowMapPhotos(_map.VisibleMarkers());
        _map.ClusterOpened += markers => _vm?.ShowMapPhotos(markers);
        _map.ViewChanged += UpdateAreaButton;

        // Viewer: scroll to zoom, drag to move, double-click to toggle zoom.
        _stage.PointerWheelChanged += OnStageWheel;
        _stage.PointerPressed += OnStagePressed;
        _stage.PointerMoved += OnStageMoved;
        _stage.PointerReleased += (_, e) => { _panFrom = null; e.Pointer.Capture(null); };
        _stage.SizeChanged += (_, _) => UpdateViewerPixels();

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    void HookScroll()
    {
        _scroll = _grid.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_scroll is null)
            return;
        _scroll.ScrollChanged += (_, _) => UpdateScrubber();
        _scroll.SizeChanged += (_, _) => UpdateScrubber();
    }

    /// <summary>With the date rail showing, the grid's own scrollbar would just crowd it.</summary>
    void UpdateScrollBar()
    {
        var rail = _vm?.HasSections == true;
        ScrollViewer.SetVerticalScrollBarVisibility(_grid, rail ? Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        _grid.Padding = new Thickness(0, 0, rail ? 2 : 12, 24);
        if (_grid.Bounds.Width > 0)
            UpdateLayout(_grid.Bounds.Width);
    }

    void UpdateScrubber()
    {
        if (_scroll is not null && _vm is not null && CurrentOffset() is { } offset)
            _scrubber.SetScroll(offset, _vm.ContentHeight, _scroll.Viewport.Height);
    }

    /// <summary>
    /// The exact position in the grid, from the row actually at the top of the screen. (The scrollbar's
    /// numbers are estimates for rows that haven't been drawn yet, so they drift on long timelines.)
    /// </summary>
    double? CurrentOffset()
    {
        if (_vm is null || _scroll is null)
            return null;
        if (_scroll.Offset.Y <= 0.5)
            return 0;
        foreach (var container in _grid.GetRealizedContainers())
        {
            if (container.TranslatePoint(default, _scroll) is not { } top)
                continue;
            if (top.Y <= 0.5 && top.Y + container.Bounds.Height > 0.5)
                return _vm.RowOffset(_grid.IndexFromContainer(container)) - top.Y;
        }
        return null;
    }

    /// <summary>Scrolls so that an exact grid position is at the top: bring its row into view, then line it up.</summary>
    void SeekTo(double offset)
    {
        if (_vm is null || _scroll is null)
            return;
        var (index, within) = _vm.RowAt(offset);
        _grid.ScrollIntoView(index);
        _grid.UpdateLayout();
        if (_grid.ContainerFromIndex(index) is { } container && container.TranslatePoint(default, _scroll) is { } top)
        {
            var target = _scroll.Offset.Y + top.Y + within;
            _scroll.Offset = new Vector(_scroll.Offset.X, Math.Clamp(target, 0, Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height)));
        }
        UpdateScrubber();
    }

    void UpdateLayout(double width)
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        // Leave room for the list padding (which also holds the scrollbar when there's no date rail).
        _vm?.SetLayout(width - _grid.Padding.Right, scaling);
    }

    void UpdateViewerPixels()
    {
        if (_vm is null)
            return;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var longSide = Math.Max(_stage.Bounds.Width, _stage.Bounds.Height) * scaling;
        if (longSide > 0)
            _vm.ViewerPixels = (int)Math.Ceiling(longSide);
    }

    void UpdateAreaButton()
    {
        if (this.FindControl<TextBlock>("ShowAreaText") is not { } text)
            return;
        var count = _map.VisibleMarkers().Count;
        text.Text = count == 0 ? "No photos in this area" : $"Show {Format.Plural(count, "photo")} in this area";
        if (this.FindControl<Button>("ShowAreaButton") is { } button)
            button.IsEnabled = count > 0;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.PropertyChanged -= OnViewModelChanged;
        _vm = DataContext as PhotosViewModel;
        if (_vm is null)
            return;
        _vm.PropertyChanged += OnViewModelChanged;
        UpdateScrollBar();
        if (_grid.Bounds.Width > 0)
            UpdateLayout(_grid.Bounds.Width);
    }

    void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PhotosViewModel.Viewer):
                SetViewer(_vm?.Viewer);
                break;
            case nameof(PhotosViewModel.Rows):
                _scroll?.ScrollToHome();
                break;
            case nameof(PhotosViewModel.HasSections):
                UpdateScrollBar();
                break;
            case nameof(PhotosViewModel.Markers) or nameof(PhotosViewModel.ShowMap):
                Avalonia.Threading.Dispatcher.UIThread.Post(UpdateAreaButton, Avalonia.Threading.DispatcherPriority.Background);
                break;
        }
    }

    void SetViewer(PhotoViewerViewModel? viewer)
    {
        if (_viewer is not null)
            _viewer.PropertyChanged -= OnViewerChanged;
        _viewer = viewer;
        ResetZoom();
        if (viewer is null)
            return;
        viewer.PropertyChanged += OnViewerChanged;
        UpdateViewerPixels();
        this.FindControl<Panel>("ViewerLayer")?.Focus();
    }

    void OnViewerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PhotoViewerViewModel.Generation))
            ResetZoom();
    }

    // ---- Viewer zoom ----

    void ResetZoom()
    {
        _zoom = 1;
        _pan = default;
        ApplyZoom();
    }

    void ZoomAt(double zoom, Point at)
    {
        zoom = Math.Clamp(zoom, 1, 8);
        // Keep the point under the pointer still.
        var center = new Point(_stage.Bounds.Width / 2, _stage.Bounds.Height / 2);
        var rel = at - center - _pan;
        _pan = (Vector)(at - center) - rel * (zoom / _zoom);
        _zoom = zoom;
        if (_zoom > 1.05)
            _viewer?.OnZoomedIn();
        ApplyZoom();
    }

    void ApplyZoom()
    {
        if (_zoom <= 1.001)
        {
            _zoom = 1;
            _pan = default;
        }
        // Don't let the photo slide out of view.
        var maxX = _stage.Bounds.Width * (_zoom - 1) / 2;
        var maxY = _stage.Bounds.Height * (_zoom - 1) / 2;
        _pan = new Vector(Math.Clamp(_pan.X, -maxX, maxX), Math.Clamp(_pan.Y, -maxY, maxY));
        _viewerImage.RenderTransform = new MatrixTransform(Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_pan));
        _stage.Cursor = _zoom > 1 ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default;
    }

    void OnStageWheel(object? sender, PointerWheelEventArgs e)
    {
        ZoomAt(_zoom * Math.Pow(1.25, e.Delta.Y), e.GetPosition(_stage));
        e.Handled = true;
    }

    void OnStagePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_stage).Properties.IsLeftButtonPressed)
            return;
        if (e.ClickCount == 2)
        {
            if (_zoom > 1)
                ResetZoom();
            else
                ZoomAt(2.5, e.GetPosition(_stage));
            return;
        }
        if (_zoom > 1)
        {
            _panFrom = e.GetPosition(_stage);
            e.Pointer.Capture(_stage);
        }
    }

    void OnStageMoved(object? sender, PointerEventArgs e)
    {
        if (_panFrom is not { } from)
            return;
        var to = e.GetPosition(_stage);
        _pan += to - from;
        _panFrom = to;
        ApplyZoom();
    }

    // ---- Keyboard ----

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null)
            return;
        if (e.Key == Key.Escape && _vm.Panel is { } panel)
        {
            _vm.Panel = null;
            e.Handled = true;
            return;
        }
        if (_vm.Viewer is { } viewer)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    viewer.CloseCommand.Execute(null);
                    break;
                case Key.Right:
                    viewer.NextCommand.Execute(null);
                    break;
                case Key.Left:
                    viewer.PreviousCommand.Execute(null);
                    break;
                case Key.F when viewer.CanEdit:
                    viewer.ToggleFavoriteCommand.Execute(null);
                    break;
                case Key.I:
                    viewer.ToggleInfoCommand.Execute(null);
                    break;
                case Key.Delete when viewer.CanEdit:
                    viewer.TrashCommand.Execute(null);
                    break;
                default:
                    return;
            }
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _vm.IsSelecting)
        {
            _vm.ClearSelectionCommand.Execute(null);
            e.Handled = true;
        }
    }
}
