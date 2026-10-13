using System.Diagnostics;
using ImmichDesktop.Core;
using ImmichDesktop.Core.Platform;
using ImmichDesktop.Core.Updates;

namespace ImmichDesktop.App.Services;

public enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Ready,
    Failed,
}

/// <summary>
/// Checks GitHub Releases for new versions. On an installed Windows copy the new installer is downloaded
/// in the background and run silently ("Restart to update", or automatically when idle if enabled).
/// Elsewhere (portable copy, Linux) it just points to the release page.
/// </summary>
public sealed class UpdateService : IDisposable
{
    static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(2);
    static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    readonly GitHubUpdateChecker? _checker;
    readonly SemaphoreSlim _gate = new(1, 1);
    Timer? _timer;
    string? _installerPath;

    public UpdateService()
    {
        if (!string.IsNullOrWhiteSpace(AppInfo.UpdateRepository))
            _checker = new GitHubUpdateChecker(AppInfo.UpdateRepository);
    }

    public static string DownloadDirectory => Path.Combine(AppPaths.DataDir, "updates");

    /// <summary>True when this copy was installed with the installer (so it can update itself).</summary>
    public static bool IsInstalledCopy =>
        OperatingSystem.IsWindows() && File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    public bool IsEnabled => _checker is not null;
    public UpdateState State { get; private set; } = UpdateState.Idle;
    public ReleaseInfo? Release { get; private set; }
    public double DownloadProgress { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset? LastChecked { get; private set; }

    /// <summary>Set by the app: quits so the installer can replace files.</summary>
    public Action? QuitApp { get; set; }

    /// <summary>Raised (on any thread) whenever <see cref="State"/> or progress changes.</summary>
    public event Action? Changed;

    public string StatusText => State switch
    {
        UpdateState.Checking => "Checking for updates…",
        UpdateState.UpToDate => LastChecked is { } t ? $"You're up to date · checked {Format.Relative(t)}" : "You're up to date",
        UpdateState.Available => $"Version {Release?.Version.ToString(3)} is available.",
        UpdateState.Downloading => $"Downloading version {Release?.Version.ToString(3)}… {DownloadProgress:P0}",
        UpdateState.Ready => $"Version {Release?.Version.ToString(3)} is ready to install.",
        UpdateState.Failed => Error ?? "Couldn't check for updates.",
        _ => "",
    };

    public void Start(bool checkAutomatically)
    {
        _timer?.Dispose();
        _timer = null;
        if (_checker is null || !checkAutomatically)
            return;
        _timer = new Timer(_ => _ = CheckAsync(), null, FirstCheckDelay, CheckInterval);
    }

    public async Task CheckAsync()
    {
        if (_checker is null || !await _gate.WaitAsync(0))
            return;
        try
        {
            if (State == UpdateState.Ready)
                return;

            SetState(UpdateState.Checking);
            Release = await _checker.CheckAsync(CancellationToken.None);
            LastChecked = DateTimeOffset.Now;

            if (Release is null)
            {
                SetState(UpdateState.UpToDate);
                return;
            }

            Log.Info($"Update available: {Release.Version}");
            if (!IsInstalledCopy || Release.Installer is null)
            {
                SetState(UpdateState.Available);
                return;
            }

            // Every release's installer has the same asset name, so cache it under its version
            // (reusing an older download here is what kept re-installing the previous version).
            var version = Release.Version.ToString(3);
            var path = Path.Combine(DownloadDirectory, $"ImmichDesktop-{version}-Setup.exe");
            RemoveOtherDownloads(path);
            if (File.Exists(path) && !IsInstallerFor(path, Release.Version))
                File.Delete(path);
            if (!File.Exists(path))
            {
                DownloadProgress = 0;
                SetState(UpdateState.Downloading);
                await _checker.DownloadAsync(Release.Installer, path, p =>
                {
                    DownloadProgress = p;
                    Changed?.Invoke();
                }, CancellationToken.None);
            }
            if (!IsInstallerFor(path, Release.Version))
            {
                File.Delete(path);
                throw new UpdateException($"The downloaded installer isn't version {version}. It was discarded; try again later.");
            }
            _installerPath = path;
            SetState(UpdateState.Ready);
        }
        catch (Exception ex)
        {
            Log.Warn("Update check failed", ex);
            Error = ex is UpdateException ? ex.Message : "Couldn't reach GitHub to check for updates.";
            SetState(UpdateState.Failed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs the downloaded installer silently and quits; the installer restarts the app afterwards.</summary>
    public void InstallAndRestart()
    {
        if (State != UpdateState.Ready || _installerPath is null || !File.Exists(_installerPath))
        {
            OpenReleasePage();
            return;
        }

        Log.Info($"Installing update {Release?.Version} from {_installerPath}");
        try
        {
            Process.Start(new ProcessStartInfo(_installerPath)
            {
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /relaunch=1",
                UseShellExecute = true, // lets Windows show the UAC prompt for all-users installs
            });
            QuitApp?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("Could not start the update installer", ex);
            Error = "The update couldn't be started. You can download it from the release page instead.";
            SetState(UpdateState.Failed);
        }
    }

    /// <summary>The installer is stamped with its version (VersionInfoVersion in installer.iss).</summary>
    static bool IsInstallerFor(string path, Version expected)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return GitHubUpdateChecker.ParseVersion(info.ProductVersion ?? info.FileVersion) == expected;
        }
        catch
        {
            return false;
        }
    }

    static void RemoveOtherDownloads(string keep)
    {
        if (!Directory.Exists(DownloadDirectory))
            return;
        foreach (var file in Directory.EnumerateFiles(DownloadDirectory))
        {
            if (!string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(file); } catch { /* in use; next time */ }
            }
        }
    }

    public void OpenReleasePage() => ShellLauncher.Open(Release?.PageUrl ?? $"{AppInfo.ProjectUrl}/releases/latest");

    void SetState(UpdateState state)
    {
        State = state;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _checker?.Dispose();
    }
}
