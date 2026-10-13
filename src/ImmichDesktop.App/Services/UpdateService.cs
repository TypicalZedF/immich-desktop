using ImmichDesktop.Core;
using ImmichDesktop.Core.Platform;
using Velopack;
using Velopack.Sources;

namespace ImmichDesktop.App.Services;

/// <summary>
/// Background auto-updates via Velopack (GitHub Releases). Downloads quietly and applies on restart.
/// Disabled when running from source or when <see cref="AppInfo.UpdateRepositoryUrl"/> is empty.
/// </summary>
public sealed class UpdateService : IDisposable
{
    readonly UpdateManager? _manager;
    Timer? _timer;
    UpdateInfo? _ready;

    public UpdateService()
    {
        if (string.IsNullOrWhiteSpace(AppInfo.UpdateRepositoryUrl))
            return;
        try
        {
            var manager = new UpdateManager(new GithubSource(AppInfo.UpdateRepositoryUrl, null, false));
            if (manager.IsInstalled)
                _manager = manager;
        }
        catch (Exception ex)
        {
            Log.Warn("Updater unavailable", ex);
        }
    }

    /// <summary>True when this copy was installed by the installer and an update feed is configured.</summary>
    public bool IsSupported => _manager is not null;

    public string? ReadyVersion => _ready?.TargetFullRelease.Version.ToString();

    /// <summary>Raised (on a background thread) when an update has been downloaded and is ready to install.</summary>
    public event Action? UpdateReady;

    public void StartBackgroundChecks()
    {
        if (_manager is null)
            return;
        _timer = new Timer(_ => _ = CheckAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(6));
    }

    /// <summary>Checks for and downloads an update. Returns true if one is ready to install.</summary>
    public async Task<bool> CheckAsync()
    {
        if (_manager is null)
            return false;
        if (_ready is not null)
            return true;
        try
        {
            var info = await _manager.CheckForUpdatesAsync();
            if (info is null)
                return false;

            await _manager.DownloadUpdatesAsync(info);
            _ready = info;
            Log.Info($"Update {ReadyVersion} downloaded");
            UpdateReady?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Update check failed", ex);
            return false;
        }
    }

    public void ApplyAndRestart(bool background)
    {
        if (_manager is null || _ready is null)
            return;
        Log.Info($"Restarting to install {ReadyVersion}");
        _manager.ApplyUpdatesAndRestart(_ready.TargetFullRelease, background ? [Autostart.BackgroundArgument] : []);
    }

    public void Dispose() => _timer?.Dispose();
}
