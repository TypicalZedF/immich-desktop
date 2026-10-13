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

public sealed partial class SettingsViewModel : ViewModelBase
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
        SelectedTheme = Themes.First(t => t.Value == s.Theme);
        SelectedConcurrency = Concurrency.FirstOrDefault(c => c.Value == s.MaxConcurrentUploads) ?? Concurrency[1];
        SelectedSpeedLimit = SpeedLimits.FirstOrDefault(o => o.Value == s.UploadLimitKBps) ?? SpeedLimits[0];
        SelectedRescan = RescanIntervals.FirstOrDefault(o => o.Value == s.RescanIntervalMinutes) ?? RescanIntervals[2];
        PauseOnBattery = s.PauseOnBattery;
        _loading = false;
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

    public IReadOnlyList<Option<AppTheme>> Themes { get; } =
    [
        new("Match system", AppTheme.System),
        new("Light", AppTheme.Light),
        new("Dark", AppTheme.Dark),
    ];

    [ObservableProperty]
    public partial Option<AppTheme> SelectedTheme { get; set; }

    partial void OnSelectedThemeChanged(Option<AppTheme> value)
    {
        if (_loading || value is null) return;
        ThemeService.Apply(value.Value);
        AppServices.Settings.Update(s => s.Theme = value.Value);
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
    public bool UpdatesSupported => AppServices.Updates.IsSupported;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
    public partial bool IsCheckingUpdates { get; set; }

    [ObservableProperty]
    public partial string UpdateStatus { get; set; } = AppServices.Updates.ReadyVersion is { } v ? $"Version {v} is ready to install." : "";

    [ObservableProperty]
    public partial bool UpdateReady { get; set; } = AppServices.Updates.ReadyVersion is not null;

    [RelayCommand(CanExecute = nameof(CanCheckUpdates))]
    async Task CheckForUpdatesAsync()
    {
        IsCheckingUpdates = true;
        UpdateStatus = "Checking for updates…";
        try
        {
            UpdateReady = await AppServices.Updates.CheckAsync();
            UpdateStatus = UpdateReady ? $"Version {AppServices.Updates.ReadyVersion} is ready to install." : "You're up to date.";
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    bool CanCheckUpdates() => !IsCheckingUpdates;

    [RelayCommand]
    static void InstallUpdate() => AppServices.Updates.ApplyAndRestart(background: false);
}
