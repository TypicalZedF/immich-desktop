namespace ImmichDesktop.Core;

/// <summary>
/// Where the app keeps its settings, state database and logs.
/// Windows: %LOCALAPPDATA%\Immich Splash. Linux: $XDG_DATA_HOME/immich-splash (~/.local/share/immich-splash).
/// Earlier versions used "Immich Desktop" / "immich-desktop"; that folder is moved over on first start.
/// Set IMMICH_DESKTOP_DATA_DIR to override (useful for development and tests).
/// </summary>
public static class AppPaths
{
    static readonly Lazy<string> DataDirLazy = new(ResolveDataDir);

    public static string DataDir => DataDirLazy.Value;
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string CredentialsFile => Path.Combine(DataDir, "credentials.bin");
    public static string DatabaseFile => Path.Combine(DataDir, "state.db");
    public static string LogsDir => Ensure(Path.Combine(DataDir, "logs"));

    static string ResolveDataDir()
    {
        var overrideDir = Environment.GetEnvironmentVariable("IMMICH_DESKTOP_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
            return Ensure(overrideDir);

        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return MoveLegacy(Path.Combine(local, "Immich Desktop"), Path.Combine(local, "Immich Splash"));
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var baseDir = string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
            : xdg;
        return MoveLegacy(Path.Combine(baseDir, "immich-desktop"), Path.Combine(baseDir, "immich-splash"));
    }

    /// <summary>Uses <paramref name="dir"/>, first moving the old folder there if only the old one exists.</summary>
    internal static string MoveLegacy(string legacyDir, string dir)
    {
        try
        {
            if (!Directory.Exists(dir) && Directory.Exists(legacyDir))
                Directory.Move(legacyDir, dir);
        }
        catch
        {
            // Old folder in use or not movable: start fresh rather than fail to launch.
        }
        return Ensure(dir);
    }

    static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
