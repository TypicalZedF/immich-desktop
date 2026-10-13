namespace ImmichDesktop.Core.Settings;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>Which releases the app updates to.</summary>
public enum UpdateChannel
{
    Stable,

    /// <summary>Also pre-releases, built from the beta branch: new features first, maybe rough edges.</summary>
    Beta,
}

/// <summary>What backup does on a metered connection (e.g. a phone hotspot or a capped plan).</summary>
public enum MeteredBackup
{
    Normal,
    PhotosOnly,
    Pause,
}

/// <summary>Where desktop backgrounds come from.</summary>
public enum WallpaperSource
{
    Favorites,
    Album,
    Library,
}

public enum AppLockMode
{
    Off,
    WindowsHello,
    Password,
}

/// <summary>A local folder that is backed up to Immich.</summary>
public sealed class SyncFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool IncludeSubfolders { get; set; } = true;
    public bool IncludePhotos { get; set; } = true;
    public bool IncludeVideos { get; set; } = true;

    /// <summary>When set, uploaded assets from this folder are added to an album with this name.</summary>
    public string? AlbumName { get; set; }

    /// <summary>Glob patterns relative to the folder, e.g. "*.tmp" or "Screenshots".</summary>
    public List<string> ExcludePatterns { get; set; } = [];

    public string DisplayName
    {
        get
        {
            var trimmed = Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            var name = System.IO.Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? Path : name;
        }
    }
}

public sealed class AppSettings
{
    // Account / server
    public string? ServerUrl { get; set; }
    public string? WebUrl { get; set; }
    public string? ServerVersion { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? UserEmail { get; set; }

    // Backup
    public List<SyncFolder> Folders { get; set; } = [];
    public bool Paused { get; set; }

    /// <summary>When paused for a limited time, backup resumes automatically at this moment.</summary>
    public DateTimeOffset? PausedUntil { get; set; }

    public int MaxConcurrentUploads { get; set; } = 2;

    /// <summary>Upload speed limit in KB/s. 0 means unlimited.</summary>
    public int UploadLimitKBps { get; set; }

    public bool PauseOnBattery { get; set; }

    /// <summary>Data saving: what to do on metered connections.</summary>
    public MeteredBackup MeteredBackup { get; set; } = MeteredBackup.PhotosOnly;

    // Server addresses, like the mobile app's automatic URL switching: a local address used on your
    // home network and external addresses (tried in order) everywhere else. ServerUrl stays the
    // address you signed in with, the last resort.
    public bool AutoSwitchServer { get; set; }
    public string? LocalNetworkName { get; set; }
    public string? LocalServerUrl { get; set; }
    public List<string> ExternalServerUrls { get; set; } = [];
    public int RescanIntervalMinutes { get; set; } = 60;

    // App
    public bool LaunchAtStartup { get; set; } = true;
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>App language code (e.g. "de"); null follows the system language.</summary>
    public string? Language { get; set; }

    /// <summary>
    /// Show online street detail on the map. Off: only the built-in map is drawn and nothing is
    /// requested from map services (which would otherwise learn roughly where your photos are).
    /// </summary>
    public bool MapStreetDetail { get; set; }
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// Look through the library on this PC for private pictures (nudity, cards, IDs, documents…) and
    /// suggest moving them to the locked folder. Off until you turn it on; nothing leaves the PC.
    /// </summary>
    public bool PrivacyScanEnabled { get; set; }

    /// <summary>Use the graphics card for the privacy check (much faster); falls back to the CPU.</summary>
    public bool PrivacyScanUseGpu { get; set; } = true;

    /// <summary>Only check while the PC isn't being used, so it never slows down games or calls.</summary>
    public bool PrivacyScanOnlyWhenIdle { get; set; }

    // Security: ask for Windows Hello or a password before showing the window (backup keeps running).
    public AppLockMode AppLock { get; set; } = AppLockMode.Off;

    /// <summary>PBKDF2 hash of the app lock password (never the password itself).</summary>
    public string? LockPasswordHash { get; set; }

    public bool AutoInstallUpdates { get; set; } = true;
    public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Stable;

    // Desktop background: a photo from Immich, changed every so often (like a digital picture frame).
    public bool WallpaperEnabled { get; set; }
    public WallpaperSource WallpaperSource { get; set; } = WallpaperSource.Favorites;
    public string? WallpaperAlbumId { get; set; }
    public string? WallpaperAlbumName { get; set; }
    public int WallpaperIntervalMinutes { get; set; } = 60;

    public bool IsConfigured => !string.IsNullOrEmpty(ServerUrl) && !string.IsNullOrEmpty(UserId);
}
