using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichDesktop.App.Services;
using ImmichDesktop.Core;
using ImmichDesktop.Core.Platform;
using ImmichDesktop.Core.Settings;

namespace ImmichDesktop.App.ViewModels;

public sealed record Option<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

public sealed partial class SettingsViewModel : ViewModelBase, IDisposable
{
    readonly ShellViewModel _shell;
    readonly bool _loading;

    public SettingsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _loading = true;
        var s = AppServices.Settings.Current;

        UserName = s.UserName ?? "";
        UserEmail = s.UserEmail ?? "";
        ServerUrl = s.WebUrl?.TrimEnd('/') ?? "";
        ServerVersion = s.ServerVersion ?? "";

        LaunchAtStartup = Autostart.IsEnabled();
        _theme = s.Theme;
        SelectedConcurrency = Concurrency.FirstOrDefault(c => c.Value == s.MaxConcurrentUploads) ?? Concurrency[1];
        SelectedSpeedLimit = SpeedLimits.FirstOrDefault(o => o.Value == s.UploadLimitKBps) ?? SpeedLimits[0];
        SelectedRescan = RescanIntervals.FirstOrDefault(o => o.Value == s.RescanIntervalMinutes) ?? RescanIntervals[2];
        PauseOnBattery = s.PauseOnBattery;
        CheckForUpdates = s.CheckForUpdates;
        AutoInstallUpdates = s.AutoInstallUpdates;
        _lockMode = s.AppLock;
        _loading = false;

        ThemeService.Changed += OnThemeChanged;
        AppServices.Updates.Changed += OnUpdatesChanged;
        _ = DetectHelloAsync();
    }

    public void Dispose()
    {
        ThemeService.Changed -= OnThemeChanged;
        AppServices.Updates.Changed -= OnUpdatesChanged;
    }

    // ---- Account ----

    public string UserName { get; }
    public string UserEmail { get; }
    public string ServerUrl { get; }
    public string ServerVersion { get; }
    public string UserInitial => UserName.Length > 0 ? char.ToUpperInvariant(UserName[0]).ToString() : "?";

    [RelayCommand]
    void OpenServer()
    {
        if (ServerUrl.Length > 0)
            ShellLauncher.Open(ServerUrl);
    }

    [RelayCommand]
    async Task SignOutAsync()
    {
        var confirmed = await _shell.Main.ConfirmAsync(
            "Sign out?",
            "Backup will stop until you sign in again. Your folders and settings are kept, and nothing is removed from Immich.",
            "Sign out", danger: true);
        if (!confirmed)
            return;

        await AppServices.Account.SignOutAsync();
        _shell.Main.ShowOnboarding(resumeSignIn: false);
    }

    // ---- General ----

    public bool AutostartSupported => Autostart.IsSupported;

    public string AutostartDescription => OperatingSystem.IsWindows()
        ? "Open in the system tray when you sign in to Windows."
        : "Open in the system tray when you log in.";

    [ObservableProperty]
    public partial bool LaunchAtStartup { get; set; }

    partial void OnLaunchAtStartupChanged(bool value)
    {
        if (_loading) return;
        Autostart.SetEnabled(value);
        AppServices.Settings.Update(s => s.LaunchAtStartup = value);
    }

    AppTheme _theme;

    public bool ThemeSystem { get => _theme == AppTheme.System; set { if (value) ThemeService.Set(AppTheme.System); } }
    public bool ThemeLight { get => _theme == AppTheme.Light; set { if (value) ThemeService.Set(AppTheme.Light); } }
    public bool ThemeDark { get => _theme == AppTheme.Dark; set { if (value) ThemeService.Set(AppTheme.Dark); } }

    void OnThemeChanged(AppTheme theme)
    {
        _theme = theme;
        OnPropertyChanged(nameof(ThemeSystem));
        OnPropertyChanged(nameof(ThemeLight));
        OnPropertyChanged(nameof(ThemeDark));
    }

    // ---- Security: app lock ----

    AppLockMode _lockMode;

    [ObservableProperty]
    public partial bool HelloAvailable { get; set; }

    public bool LockOff { get => _lockMode == AppLockMode.Off; set { if (value) _ = SetLockModeAsync(AppLockMode.Off); } }
    public bool LockHello { get => _lockMode == AppLockMode.WindowsHello; set { if (value) _ = SetLockModeAsync(AppLockMode.WindowsHello); } }
    public bool LockPassword { get => _lockMode == AppLockMode.Password; set { if (value) _ = SetLockModeAsync(AppLockMode.Password); } }

    public bool IsLockEnabled => _lockMode != AppLockMode.Off;

    [ObservableProperty]
    public partial bool IsSettingPassword { get; set; }

    [ObservableProperty]
    public partial string NewPassword { get; set; } = "";

    [ObservableProperty]
    public partial string ConfirmPassword { get; set; } = "";

    [ObservableProperty]
    public partial string? LockError { get; set; }

    async Task DetectHelloAsync() => HelloAvailable = await WindowsHello.IsAvailableAsync();

    async Task SetLockModeAsync(AppLockMode mode)
    {
        LockError = null;
        IsSettingPassword = false;
        switch (mode)
        {
            case AppLockMode.Off:
                SaveLock(AppLockMode.Off, null);
                break;
            case AppLockMode.WindowsHello:
                // Make sure Hello actually works for this user before relying on it.
                var result = await WindowsHello.VerifyAsync(_shell.Main.GetWindowHandle?.Invoke() ?? IntPtr.Zero, "Confirm it's you to turn on the app lock");
                if (result == HelloResult.Verified)
                    SaveLock(AppLockMode.WindowsHello, null);
                else if (result != HelloResult.Canceled)
                    LockError = "Windows Hello isn't set up on this PC. Set it up in Windows Settings → Accounts → Sign-in options, or use a password.";
                break;
            case AppLockMode.Password:
                NewPassword = "";
                ConfirmPassword = "";
                IsSettingPassword = true;
                break;
        }
        RaiseLockProperties();
    }

    [RelayCommand]
    async Task SavePasswordAsync()
    {
        LockError = null;
        if (NewPassword.Length < 4)
        {
            LockError = "Use at least 4 characters.";
            return;
        }
        if (NewPassword != ConfirmPassword)
        {
            LockError = "The passwords don't match.";
            return;
        }
        var password = NewPassword;
        var hash = await Task.Run(() => PasswordHasher.Hash(password));
        NewPassword = "";
        ConfirmPassword = "";
        IsSettingPassword = false;
        SaveLock(AppLockMode.Password, hash);
        RaiseLockProperties();
    }

    [RelayCommand]
    void CancelPassword()
    {
        IsSettingPassword = false;
        NewPassword = "";
        ConfirmPassword = "";
        LockError = null;
        RaiseLockProperties();
    }

    [RelayCommand]
    void LockNow() => _shell.Main.Lock();

    void SaveLock(AppLockMode mode, string? hash)
    {
        _lockMode = mode;
        AppServices.Settings.Update(s =>
        {
            s.AppLock = mode;
            s.LockPasswordHash = mode == AppLockMode.Password ? hash : null;
        });
    }

    void RaiseLockProperties()
    {
        OnPropertyChanged(nameof(LockOff));
        OnPropertyChanged(nameof(LockHello));
        OnPropertyChanged(nameof(LockPassword));
        OnPropertyChanged(nameof(IsLockEnabled));
    }

    // ---- Backup ----

    public IReadOnlyList<Option<int>> Concurrency { get; } =
    [
        new("1 at a time", 1),
        new("2 at a time", 2),
        new("3 at a time", 3),
        new("4 at a time", 4),
        new("6 at a time", 6),
    ];

    [ObservableProperty]
    public partial Option<int> SelectedConcurrency { get; set; }

    partial void OnSelectedConcurrencyChanged(Option<int> value)
    {
        if (_loading || value is null) return;
        AppServices.Settings.Update(s => s.MaxConcurrentUploads = value.Value);
    }

    public IReadOnlyList<Option<int>> SpeedLimits { get; } =
    [
        new("Unlimited", 0),
        new("1 MB/s", 1024),
        new("2 MB/s", 2 * 1024),
        new("5 MB/s", 5 * 1024),
        new("10 MB/s", 10 * 1024),
        new("25 MB/s", 25 * 1024),
        new("50 MB/s", 50 * 1024),
    ];

    [ObservableProperty]
    public partial Option<int> SelectedSpeedLimit { get; set; }

    partial void OnSelectedSpeedLimitChanged(Option<int> value)
    {
        if (_loading || value is null) return;
        AppServices.Settings.Update(s => s.UploadLimitKBps = value.Value);
    }

    public IReadOnlyList<Option<int>> RescanIntervals { get; } =
    [
        new("Every 15 minutes", 15),
        new("Every 30 minutes", 30),
        new("Every hour", 60),
        new("Every 3 hours", 180),
        new("Every 6 hours", 360),
    ];

    [ObservableProperty]
    public partial Option<int> SelectedRescan { get; set; }

    partial void OnSelectedRescanChanged(Option<int> value)
    {
        if (_loading || value is null) return;
        AppServices.Settings.Update(s => s.RescanIntervalMinutes = value.Value);
    }

    [ObservableProperty]
    public partial bool PauseOnBattery { get; set; }

    partial void OnPauseOnBatteryChanged(bool value)
    {
        if (_loading) return;
        AppServices.Settings.Update(s => s.PauseOnBattery = value);
    }

    // ---- Troubleshooting ----

    [RelayCommand]
    async Task RecheckAllAsync()
    {
        var confirmed = await _shell.Main.ConfirmAsync(
            "Re-check all files?",
            "Every file in your folders will be compared with your Immich library again. Nothing is uploaded twice, but this can take a while for large libraries.",
            "Re-check");
        if (confirmed)
            AppServices.Engine.ResetAndRescan();
    }

    [RelayCommand]
    static void OpenLogs() => ShellLauncher.Open(AppPaths.LogsDir);

    [RelayCommand]
    static void OpenDataFolder() => ShellLauncher.Open(AppPaths.DataDir);

    // ---- About & updates ----

    public string Version => $"Version {AppInfo.Version}";
    public bool UpdatesEnabled => AppServices.Updates.IsEnabled;
    public bool CanSelfUpdate => UpdateService.IsInstalledCopy;

    public string UpdateStatus => AppServices.Updates.StatusText;
    public bool HasUpdateStatus => UpdateStatus.Length > 0;
    public bool IsCheckingUpdates => AppServices.Updates.State is UpdateState.Checking or UpdateState.Downloading;
    public bool UpdateReady => AppServices.Updates.State == UpdateState.Ready;
    public bool UpdateAvailable => AppServices.Updates.State == UpdateState.Available;
    public bool CanCheck => !UpdateReady && !UpdateAvailable;

    void OnUpdatesChanged() => Dispatcher.UIThread.Post(() =>
    {
        OnPropertyChanged(nameof(UpdateStatus));
        OnPropertyChanged(nameof(HasUpdateStatus));
        OnPropertyChanged(nameof(IsCheckingUpdates));
        OnPropertyChanged(nameof(UpdateReady));
        OnPropertyChanged(nameof(UpdateAvailable));
        OnPropertyChanged(nameof(CanCheck));
    });

    [RelayCommand]
    static Task CheckForUpdatesAsync() => AppServices.Updates.CheckAsync();

    [RelayCommand]
    static void InstallUpdate() => AppServices.Updates.InstallAndRestart();

    [RelayCommand]
    static void OpenReleasePage() => AppServices.Updates.OpenReleasePage();

    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; }

    partial void OnCheckForUpdatesChanged(bool value)
    {
        if (_loading) return;
        AppServices.Settings.Update(s => s.CheckForUpdates = value);
    }

    [ObservableProperty]
    public partial bool AutoInstallUpdates { get; set; }

    partial void OnAutoInstallUpdatesChanged(bool value)
    {
        if (_loading) return;
        AppServices.Settings.Update(s => s.AutoInstallUpdates = value);
    }
}
