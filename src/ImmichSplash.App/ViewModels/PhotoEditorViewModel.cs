using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;
using Material.Icons;

namespace ImmichSplash.App.ViewModels;

/// <summary>One crop shape on the editor's list (Free, Original, 4:3…).</summary>
public sealed partial class AspectChoiceViewModel(string label, double? ratio, bool isOriginal, MaterialIconKind icon) : ObservableObject
{
    public string Label { get; } = label;

    /// <summary>Width divided by height; null for a free crop (and for Original until the photo's shape is known).</summary>
    public double? Ratio { get; } = ratio;

    public bool IsOriginal { get; } = isOriginal;
    public MaterialIconKind Icon { get; } = icon;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>
/// A simple photo editor like the Immich web app's: turn, flip and crop. Immich applies the edits to the
/// original, full-quality photo on the server (the preview here is only for looking).
/// </summary>
public sealed partial class PhotoEditorViewModel : ViewModelBase, IDisposable
{
    readonly PhotosViewModel _owner;
    readonly MainWindowViewModel _main;
    readonly TimelineAsset _asset;
    readonly CancellationTokenSource _lifetime = new();
    int _width, _height;

    public PhotoEditorViewModel(PhotosViewModel owner, MainWindowViewModel main, TimelineAsset asset)
    {
        _owner = owner;
        _main = main;
        _asset = asset;
        Aspects =
        [
            new(Tr.T("Free"), null, false, MaterialIconKind.CropFree),
            new(Tr.T("Original"), null, true, MaterialIconKind.ImageOutline),
            new("5:4", 5.0 / 4, false, MaterialIconKind.CropLandscape),
            new("4:5", 4.0 / 5, false, MaterialIconKind.CropPortrait),
            new("4:3", 4.0 / 3, false, MaterialIconKind.CropLandscape),
            new("3:4", 3.0 / 4, false, MaterialIconKind.CropPortrait),
            new("3:2", 3.0 / 2, false, MaterialIconKind.CropLandscape),
            new("2:3", 2.0 / 3, false, MaterialIconKind.CropPortrait),
            new("16:9", 16.0 / 9, false, MaterialIconKind.CropLandscape),
            new("9:16", 9.0 / 16, false, MaterialIconKind.CropPortrait),
            new(Tr.T("Square"), 1, false, MaterialIconKind.CropSquare),
        ];
        Aspects[0].IsSelected = true;
        _ = LoadAsync();
    }

    public IReadOnlyList<AspectChoiceViewModel> Aspects { get; }

    [ObservableProperty]
    public partial Bitmap? Source { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEditor))]
    public partial bool IsLoading { get; set; } = true;

    public bool ShowEditor => !IsLoading && Source is not null;

    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>It was edited before: those edits are replaced when you save (they can be removed too).</summary>
    [ObservableProperty]
    public partial bool HadEdits { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    public partial int Rotation { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    public partial bool MirrorHorizontal { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    public partial bool MirrorVertical { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    public partial Rect Crop { get; set; } = new(0, 0, 1, 1);

    /// <summary>The shape the crop keeps (null: free).</summary>
    [ObservableProperty]
    public partial double? Aspect { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial bool IsSaving { get; set; }

    bool CropIsFull => Crop.X <= 0.001 && Crop.Y <= 0.001 && Crop.Width >= 0.998 && Crop.Height >= 0.998;

    public bool HasChanges => Rotation != 0 || MirrorHorizontal || MirrorVertical || !CropIsFull;

    public bool CanSave => !IsSaving && ShowEditor && (HasChanges || HadEdits);

    partial void OnCropChanged(Rect value) => OnPropertyChanged(nameof(CanSave));
    partial void OnRotationChanged(int value)
    {
        OnPropertyChanged(nameof(CanSave));
        RefreshOriginalAspect();
    }

    partial void OnMirrorHorizontalChanged(bool value) => OnPropertyChanged(nameof(CanSave));
    partial void OnMirrorVerticalChanged(bool value) => OnPropertyChanged(nameof(CanSave));
    partial void OnHadEditsChanged(bool value) => OnPropertyChanged(nameof(CanSave));
    partial void OnSourceChanged(Bitmap? value) => OnPropertyChanged(nameof(ShowEditor));

    async Task LoadAsync()
    {
        if (AppServices.Account.Client is not { } client)
        {
            IsLoading = false;
            Error = Tr.T("Not signed in.");
            return;
        }
        try
        {
            var size = await client.GetAssetSizeAsync(_asset.Id, _lifetime.Token);
            (_width, _height) = (size.Width, size.Height);
            HadEdits = size.IsEdited;
            // The picture before any edits: crops are always measured on the original.
            var bytes = await client.GetAssetImageAsync(_asset.Id, AssetImageSize.Preview, _lifetime.Token, edited: false);
            Source = await Task.Run(() => Images.DecodeForDisplay(bytes, 1800), _lifetime.Token);
            if (_width <= 0 || _height <= 0)
                Error = Tr.T("Couldn't read the size of this photo, so it can't be edited here.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (ImmichApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed)
        {
            Error = Tr.T("This server can't edit photos. Editing needs Immich 2.5 or newer.");
        }
        catch (Exception ex)
        {
            Log.Warn("Could not open the editor", ex);
            Error = Tr.T("Couldn't open the editor. {0}", NetworkErrors.Describe(ex));
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(CanSave));
        }
    }

    [RelayCommand]
    void RotateLeft() => Rotation = (Rotation + 270) % 360;

    [RelayCommand]
    void RotateRight() => Rotation = (Rotation + 90) % 360;

    [RelayCommand]
    void FlipHorizontal() => MirrorHorizontal = !MirrorHorizontal;

    [RelayCommand]
    void FlipVertical() => MirrorVertical = !MirrorVertical;

    [RelayCommand]
    void SelectAspect(AspectChoiceViewModel choice)
    {
        foreach (var a in Aspects)
            a.IsSelected = ReferenceEquals(a, choice);
        if (choice.IsOriginal)
            RefreshOriginalAspect();
        else
            Aspect = choice.Ratio;
        if (choice.Ratio is null && !choice.IsOriginal)
            Crop = new Rect(0, 0, 1, 1);
    }

    /// <summary>"Original" follows the photo's shape (which a quarter turn changes).</summary>
    void RefreshOriginalAspect()
    {
        if (!Aspects.Any(a => a.IsOriginal && a.IsSelected) || _width <= 0 || _height <= 0)
            return;
        Aspect = Rotation is 90 or 270 ? _height / (double)_width : _width / (double)_height;
    }

    [RelayCommand]
    void Reset()
    {
        Rotation = 0;
        MirrorHorizontal = false;
        MirrorVertical = false;
        SelectAspect(Aspects[0]);
        Crop = new Rect(0, 0, 1, 1);
    }

    [RelayCommand]
    async Task SaveAsync()
    {
        if (AppServices.Account.Client is not { } client || !CanSave)
            return;
        IsSaving = true;
        Error = null;
        try
        {
            var edits = ImageEditPlan.Build(_width, _height, Rotation, MirrorHorizontal, MirrorVertical, Crop.X, Crop.Y, Crop.Width, Crop.Height);
            if (edits.Count == 0)
                await client.RemoveEditsAsync(_asset.Id, CancellationToken.None);
            else
                await client.ApplyEditsAsync(_asset.Id, edits, CancellationToken.None);
            _owner.OnAssetEdited(_asset.Id, edits.Count == 0 ? Tr.T("Edits removed") : Tr.T("Edits applied"));
            Close();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not save the edits", ex);
            Error = Tr.T("Couldn't save the edits. {0}", NetworkErrors.Describe(ex));
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>Takes all edits off a photo that has some.</summary>
    [RelayCommand]
    async Task RemoveEditsAsync()
    {
        if (AppServices.Account.Client is not { } client || !HadEdits)
            return;
        if (!await _main.ConfirmAsync(Tr.T("Remove all edits?"), Tr.T("The photo goes back to how it was when you added it."), Tr.T("Remove edits"), danger: true))
            return;
        try
        {
            await client.RemoveEditsAsync(_asset.Id, CancellationToken.None);
            _owner.OnAssetEdited(_asset.Id, Tr.T("Edits removed"));
            Close();
        }
        catch (Exception ex)
        {
            Error = Tr.T("Couldn't remove the edits. {0}", NetworkErrors.Describe(ex));
        }
    }

    [RelayCommand]
    void Close() => _main.CloseOverlay(this);

    public void Dispose()
    {
        _lifetime.Cancel();
        Source?.Dispose();
        Source = null;
    }
}
