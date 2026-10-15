using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using ImmichSplash.App.ViewModels;

namespace ImmichSplash.App.Views;

public partial class ProfilePhotoView : UserControl
{
    Point? _dragFrom;

    public ProfilePhotoView()
    {
        InitializeComponent();

        // Drag to move the photo inside the circle, scroll (or pinch on a touchpad) to zoom.
        var area = this.FindControl<Panel>("CropArea")!;
        area.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(area).Properties.IsLeftButtonPressed)
                return;
            _dragFrom = e.GetPosition(area);
            e.Pointer.Capture(area);
            e.Handled = true;
        };
        area.PointerMoved += (_, e) =>
        {
            if (_dragFrom is not { } from || Cropper is not { } cropper)
                return;
            var to = e.GetPosition(area);
            cropper.Pan(to.X - from.X, to.Y - from.Y);
            _dragFrom = to;
        };
        area.PointerReleased += (_, e) =>
        {
            _dragFrom = null;
            e.Pointer.Capture(null);
        };
        area.PointerCaptureLost += (_, _) => _dragFrom = null;
        area.PointerWheelChanged += (_, e) =>
        {
            if (Cropper is not { } cropper)
                return;
            var at = e.GetPosition(area);
            cropper.ZoomAt(cropper.Zoom * Math.Pow(1.15, e.Delta.Y), at.X, at.Y);
            e.Handled = true;
        };
    }

    PhotoCropViewModel? Cropper => (DataContext as ProfilePhotoViewModel)?.Cropper;
}
