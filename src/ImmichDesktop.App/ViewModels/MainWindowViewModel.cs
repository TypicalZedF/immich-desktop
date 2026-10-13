using CommunityToolkit.Mvvm.ComponentModel;
using ImmichDesktop.App.Services;

namespace ImmichDesktop.App.ViewModels;

/// <summary>Root of the window: shows onboarding until the user is signed in and set up, then the main shell.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    public MainWindowViewModel()
    {
        var setUp = AppServices.Account.IsSignedIn && AppServices.Settings.Current.IsConfigured;
        Content = setUp ? new ShellViewModel(this) : new OnboardingViewModel(this, resumeSignIn: false);
    }

    [ObservableProperty]
    public partial ViewModelBase Content { get; set; }

    [ObservableProperty]
    public partial DialogViewModel? Dialog { get; set; }

    /// <summary>Provided by the window: shows a native folder picker and returns the chosen path.</summary>
    public Func<string, Task<string?>>? PickFolder { get; set; }

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
    }

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
    }

    public void Dispose()
    {
        Dialog?.CancelCommand.Execute(null);
        (Content as IDisposable)?.Dispose();
    }
}
