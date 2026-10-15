using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ImmichSplash.App.ViewModels;

/// <summary>A small popup that asks for one line of text (naming a person…).</summary>
public sealed partial class NameDialogViewModel : ViewModelBase
{
    readonly MainWindowViewModel _main;
    readonly Func<string, Task<string?>> _save;

    /// <param name="save">Does the work with the text; returns what went wrong, or null.</param>
    public NameDialogViewModel(MainWindowViewModel main, string title, string label, string initial, string confirmText, Func<string, Task<string?>> save)
    {
        _main = main;
        _save = save;
        Title = title;
        Label = label;
        ConfirmText = confirmText;
        Text = initial;
    }

    public string Title { get; }
    public string Label { get; }
    public string ConfirmText { get; }

    [ObservableProperty]
    public partial string Text { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial bool IsSaving { get; set; }

    public bool CanSave => !IsSaving;

    [RelayCommand]
    async Task SaveAsync()
    {
        if (IsSaving)
            return; // Enter pressed again while saving
        var text = Text.Trim();
        if (text.Length == 0)
        {
            Error = Tr.T("Type a name first.");
            return;
        }
        IsSaving = true;
        Error = null;
        var problem = await _save(text);
        IsSaving = false;
        if (problem is null)
            _main.CloseOverlay(this);
        else
            Error = problem;
    }

    [RelayCommand]
    void Cancel() => _main.CloseOverlay(this);
}
