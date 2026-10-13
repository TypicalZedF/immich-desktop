namespace ImmichDesktop.Core.Settings;

public enum AppTheme
{
    System,
    Light,
    Dark,
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
    public int RescanIntervalMinutes { get; set; } = 60;

    // App
    public bool LaunchAtStartup { get; set; } = true;
    public AppTheme Theme { get; set; } = AppTheme.System;

    public bool IsConfigured => !string.IsNullOrEmpty(ServerUrl) && !string.IsNullOrEmpty(UserId);
}
