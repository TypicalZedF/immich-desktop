using CommunityToolkit.Mvvm.ComponentModel;
using ImmichDesktop.App.Services;
using ImmichDesktop.Core.Settings;

namespace ImmichDesktop.App.ViewModels;

/// <summary>Root of the window: onboarding until set up, the lock screen when locked, otherwise the main shell.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    ShellPage _pendingPage = ShellPage.Overview;

    public MainWindowViewModel()
    {
        var setUp = AppServices.Account.IsSignedIn && AppServices.Settings.Current.IsConfigured;
        if (!setUp)
            Content = new OnboardingViewModel(this, resumeSignIn: false);
        else if (IsLockEnabled)
            Content = new LockViewModel(this, Unlock);
        else
            Content = new ShellViewModel(this);
    }

    [ObservableProperty]
    public partial ViewModelBase Content { get; set; }

    [ObservableProperty]
    public partial DialogViewModel? Dialog { get; set; }

    /// <summary>Provided by the window: shows a native folder picker and returns the chosen path.</summary>
    public Func<string, Task<string?>>? PickFolder { get; set; }

    /// <summary>Provided by the window: its native handle (for the Windows Hello prompt).</summary>
    public Func<IntPtr>? GetWindowHandle { get; set; }

    public static bool IsLockEnabled => AppServices.Settings.Current.AppLock != AppLockMode.Off;

    public bool IsLocked => Content is LockViewModel;

    public Task<string?> PickFolderAsync(string title) => PickFolder?.Invoke(title) ?? Task.FromResult<string?>(null);

    public void ShowShell(ShellPage page = ShellPage.Overview)
    {
        Replace(new ShellViewModel(this));
        Navigate(page);
    }

    /// <summary>Back to setup. With <paramref name="resumeSignIn"/>, jumps straight to signing in to the known server.</summary>
    public void ShowOnboarding(bool resumeSignIn) => Replace(new OnboardingViewModel(this, resumeSignIn));

    public void Navigate(ShellPage page)
    {
        if (Content is ShellViewModel shell)
            shell.Navigate(page);
        else
            _pendingPage = page; // shown after unlocking
    }

    /// <summary>Locks the window now (if an app lock is set up).</summary>
    public void Lock()
    {
        if (!IsLockEnabled || IsLocked || Content is OnboardingViewModel)
            return;
        Dialog?.CancelCommand.Execute(null);
        _pendingPage = (Content as ShellViewModel)?.SelectedNav.Page ?? ShellPage.Overview;
        Replace(new LockViewModel(this, Unlock));
    }

    void Unlock() => ShowShell(_pendingPage);

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText = "Cancel", bool danger = false)
    {
        var dialog = new DialogViewModel(title, message, confirmText, cancelText, danger);
        Dialog = dialog;
        try
        {
            return await dialog.Result;
        }
        finally
        {
            if (Dialog == dialog)
                Dialog = null;
        }
    }

    void Replace(ViewModelBase content)
    {
        var old = Content;
        Content = content;
        (old as IDisposable)?.Dispose();
        OnPropertyChanged(nameof(IsLocked));
    }

    public void Dispose()
    {
        Dialog?.CancelCommand.Execute(null);
        (Content as IDisposable)?.Dispose();
    }
}
