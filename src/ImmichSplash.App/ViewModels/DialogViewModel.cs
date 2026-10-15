using CommunityToolkit.Mvvm.Input;

namespace ImmichSplash.App.ViewModels;

/// <summary>An in-window confirmation dialog.</summary>
public sealed partial class DialogViewModel(string title, string message, string confirmText, string cancelText, bool isDanger) : ViewModelBase
{
    readonly TaskCompletionSource<bool> _result = new();

    public string Title { get; } = title;
    public string Message { get; } = message;
    public string ConfirmText { get; } = confirmText;
    public string CancelText { get; } = cancelText;
    public bool IsDanger { get; } = isDanger;

    public Task<bool> Result => _result.Task;

    [RelayCommand]
    void Confirm() => _result.TrySetResult(true);

    [RelayCommand]
    void Cancel() => _result.TrySetResult(false);
}
