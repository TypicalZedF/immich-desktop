using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichDesktop.App.Services;
using ImmichDesktop.Core.Platform;
using ImmichDesktop.Core.Settings;

namespace ImmichDesktop.App.ViewModels;

/// <summary>
/// Shown instead of the app while it's locked. Backup keeps running in the background;
/// only viewing the app requires Windows Hello or the app password.
/// </summary>
public sealed partial class LockViewModel : ViewModelBase
{
    const int MaxAttemptsBeforeDelay = 5;
    static int _failedAttempts;
    static DateTime _lockedOutUntil;

    readonly MainWindowViewModel _main;
    readonly Action _onUnlocked;
    readonly AppLockMode _mode;
    readonly string? _passwordHash;
    bool _autoPrompted;

    public LockViewModel(MainWindowViewModel main, Action onUnlocked)
    {
        _main = main;
        _onUnlocked = onUnlocked;
        var settings = AppServices.Settings.Current;
        _mode = settings.AppLock;
        _passwordHash = settings.LockPasswordHash;
        UserName = settings.UserName ?? settings.UserEmail ?? "";
    }

    public string UserName { get; }
    public bool UsesHello => _mode == AppLockMode.WindowsHello;
    public bool UsesPassword => _mode == AppLockMode.Password;

    public string Subtitle => UsesHello
        ? "Use Windows Hello to unlock. Backup keeps running in the background."
        : "Enter your app password to unlock. Backup keeps running in the background.";

    [ObservableProperty]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnlockWithHelloCommand), nameof(UnlockWithPasswordCommand))]
    public partial bool IsBusy { get; set; }

    bool CanAct() => !IsBusy;

    /// <summary>Called by the view once it's on screen, so the Windows Hello prompt appears right away.</summary>
    public void OnShown()
    {
        if (UsesHello && !_autoPrompted)
        {
            _autoPrompted = true;
            UnlockWithHelloCommand.Execute(null);
        }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    async Task UnlockWithHelloAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            var result = await WindowsHello.VerifyAsync(_main.GetWindowHandle?.Invoke() ?? IntPtr.Zero, "Unlock Immich Desktop");
            switch (result)
            {
                case HelloResult.Verified:
                    _onUnlocked();
                    break;
                case HelloResult.Canceled:
                    break;
                case HelloResult.Unavailable:
                    Error = "Windows Hello isn't available right now. If it was turned off, use “Forgot how to unlock?” below.";
                    break;
                default:
                    Error = "Windows Hello couldn't verify you. Try again.";
                    break;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    async Task UnlockWithPasswordAsync()
    {
        Error = null;
        if (DateTime.UtcNow < _lockedOutUntil)
        {
            Error = $"Too many attempts. Try again in {(int)Math.Ceiling((_lockedOutUntil - DateTime.UtcNow).TotalSeconds)} seconds.";
            return;
        }
        if (Password.Length == 0)
        {
            Error = "Enter your app password.";
            return;
        }

        IsBusy = true;
        try
        {
            var password = Password;
            var ok = await Task.Run(() => PasswordHasher.Verify(password, _passwordHash));
            if (ok)
            {
                _failedAttempts = 0;
                Password = "";
                _onUnlocked();
                return;
            }

            _failedAttempts++;
            Password = "";
            if (_failedAttempts >= MaxAttemptsBeforeDelay)
            {
                _lockedOutUntil = DateTime.UtcNow.AddSeconds(30);
                Error = "Too many attempts. Try again in 30 seconds.";
            }
            else
            {
                Error = "That password isn't right.";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    async Task ForgotAsync()
    {
        var confirmed = await _main.ConfirmAsync(
            "Reset the app lock?",
            "You'll be signed out and need to sign in to Immich again. Your folders and settings are kept, and backup resumes once you're signed in.",
            "Sign out and reset", danger: true);
        if (!confirmed)
            return;

        await AppServices.Account.SignOutAsync();
        _main.ShowOnboarding(resumeSignIn: true);
    }
}
