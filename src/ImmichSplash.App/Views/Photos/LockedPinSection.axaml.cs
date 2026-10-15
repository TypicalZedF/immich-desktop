using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ImmichSplash.App.ViewModels;

namespace ImmichSplash.App.Views.Photos;

public partial class LockedPinSection : UserControl
{
    readonly TextBox _input;
    readonly ItemsControl _boxes;

    public LockedPinSection()
    {
        InitializeComponent();
        _input = this.FindControl<TextBox>("PinInput")!;
        _boxes = this.FindControl<ItemsControl>("PinBoxes")!;

        // The boxes show where you type only while the (invisible) field has focus.
        _input.GotFocus += (_, _) => _boxes.Classes.Set("typing", true);
        _input.LostFocus += (_, _) => _boxes.Classes.Set("typing", false);

        // Ready to type as soon as the prompt shows: when the page opens, when it's shown again, after a wrong PIN.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is PhotosViewModel vm)
            {
                vm.PropertyChanged += OnViewModelChanged;
                FocusSoon();
            }
        };
        // Coming back to the page (it's kept while you visit others): the field gets a place on screen again.
        var onScreen = false;
        _input.EffectiveViewportChanged += (_, e) =>
        {
            var now = e.EffectiveViewport.Width > 0 && e.EffectiveViewport.Height > 0;
            if (now && !onScreen)
                FocusSoon();
            onScreen = now;
        };
    }

    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PhotosViewModel.CanEnterPin) or nameof(PhotosViewModel.ShowLockedPrompt) or nameof(PhotosViewModel.PinBoxes)
            or nameof(PhotosViewModel.IsUnlocking))
            FocusSoon();
    }

    /// <summary>
    /// Focuses the PIN field once layout has settled. Clicking "Locked folder" in the sidebar leaves the focus on the
    /// sidebar, which happens after the page appears, so it's tried once more a moment later.
    /// </summary>
    void FocusSoon()
    {
        void Try()
        {
            if (DataContext is PhotosViewModel { ShowLockedPrompt: true, CanEnterPin: true, IsUnlocking: false } && _input.IsEffectivelyVisible && !_input.IsFocused)
                _input.Focus();
        }
        Dispatcher.UIThread.Post(Try, DispatcherPriority.Background);
        DispatcherTimer.RunOnce(Try, TimeSpan.FromMilliseconds(200));
    }
}
