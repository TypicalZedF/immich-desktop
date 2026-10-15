using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ImmichSplash.App.ViewModels;

namespace ImmichSplash.App.Views.Photos;

public partial class SharePanel : UserControl
{
    public SharePanel()
    {
        InitializeComponent();
        // "Add to album" opens ready to type (search, or a new album's name).
        DataContextChanged += (_, _) =>
        {
            if (DataContext is PhotosViewModel vm)
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(PhotosViewModel.Panel) && vm.Panel is AlbumPickerViewModel)
                        Dispatcher.UIThread.Post(() => this.GetVisualDescendants().OfType<TextBox>()
                            .FirstOrDefault(t => t.Name == "AlbumSearch")?.Focus(), DispatcherPriority.Background);
                };
        };
    }
}
