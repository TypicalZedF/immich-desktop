using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core.Api;

namespace ImmichSplash.App.ViewModels;

/// <summary>The "Create album" / "Edit album" dialog.</summary>
public sealed partial class AlbumEditorViewModel : ViewModelBase
{
    readonly PhotosViewModel _owner;
    readonly MainWindowViewModel _main;
    readonly AlbumInfo? _album;

    public AlbumEditorViewModel(PhotosViewModel owner, MainWindowViewModel main, AlbumInfo? album)
    {
        _owner = owner;
        _main = main;
        _album = album;
        Title = album is null ? Tr.T("Create album") : Tr.T("Edit album");
        ConfirmText = album is null ? Tr.T("Create") : Tr.T("Save");
        Name = album?.AlbumName ?? "";
        Description = album?.Description ?? "";
    }

    public string Title { get; }
    public string ConfirmText { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Description { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial bool IsSaving { get; set; }

    public bool CanSave => !IsSaving;

    [RelayCommand]
    async Task SaveAsync()
    {
        var name = Name.Trim();
        if (name.Length == 0)
        {
            Error = Tr.T("Give the album a name.");
            return;
        }
        IsSaving = true;
        Error = null;
        var problem = await _owner.SaveAlbumAsync(_album, name, Description.Trim());
        IsSaving = false;
        if (problem is null)
            _main.CloseOverlay(this);
        else
            Error = problem;
    }

    [RelayCommand]
    void Cancel() => _main.CloseOverlay(this);
}
