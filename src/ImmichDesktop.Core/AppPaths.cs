namespace ImmichDesktop.Core;

/// <summary>
/// Where the app keeps its settings, state database and logs.
/// Windows: %LOCALAPPDATA%\Immich Desktop. Linux: $XDG_DATA_HOME/immich-desktop (~/.local/share/immich-desktop).
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
            return Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Immich Desktop"));

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var baseDir = string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
            : xdg;
        return Ensure(Path.Combine(baseDir, "immich-desktop"));
    }

    static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
