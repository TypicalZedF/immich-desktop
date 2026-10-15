using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using ImmichSplash.App.ViewModels;

namespace ImmichSplash.App.Views.Photos;

public partial class LockedPinSection : UserControl
{
    public LockedPinSection()
    {
        InitializeComponent();
        // Ready to type as soon as the prompt shows (and again after a wrong PIN clears it).
        DataContextChanged += (_, _) =>
        {
            if (DataContext is PhotosViewModel vm)
                vm.PropertyChanged += OnViewModelChanged;
        };
    }

    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PhotosViewModel.CanEnterPin) or nameof(PhotosViewModel.ShowLockedPrompt) or nameof(PhotosViewModel.PinBoxes)
            && sender is PhotosViewModel { ShowLockedPrompt: true, CanEnterPin: true, IsUnlocking: false })
            Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("PinInput")?.Focus(), DispatcherPriority.Background);
    }
}
