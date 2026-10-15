using Microsoft.Win32;

namespace ImmichDesktop.Core.Platform;

/// <summary>
/// Launch-at-login. Windows: HKCU Run key (no admin needed). Linux: XDG autostart .desktop entry.
/// The app is started with --background so it goes straight to the tray.
/// </summary>
public static class Autostart
{
    public const string BackgroundArgument = "--background";
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    /// <summary>The executable to launch. Under AppImage the real file is in $APPIMAGE, not the temporary mount.</summary>
    static string? ExecutablePath =>
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage ? appImage : Environment.ProcessPath;

    static string LinuxEntryPath
    {
        get
        {
            // In a Flatpak, XDG_CONFIG_HOME is the sandbox's own folder: autostart lives in the real ~/.config.
            var config = Flatpak.IsRunning ? null : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(config))
                config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(config, "autostart", "immich-desktop.desktop");
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(AppInfo.Id) is string;
            }
            if (OperatingSystem.IsLinux())
                return File.Exists(LinuxEntryPath);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read autostart state", ex);
        }
        return false;
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            var exe = ExecutablePath;
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
                if (enabled && exe is not null)
                    key.SetValue(AppInfo.Id, $"\"{exe}\" {BackgroundArgument}");
                else
                    key.DeleteValue(AppInfo.Id, throwOnMissingValue: false);
            }
            else if (OperatingSystem.IsLinux())
            {
                var entry = LinuxEntryPath;
                if (enabled && exe is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
                    File.WriteAllText(entry, $"""
                        [Desktop Entry]
                        Type=Application
                        Name={AppInfo.Name}
                        Comment=Back up photos and videos to Immich
                        Exec={(Flatpak.IsRunning ? Flatpak.LaunchCommand : $"\"{exe}\"")} {BackgroundArgument}
                        Icon={Flatpak.AppId ?? "immich-desktop"}
                        Terminal=false
                        X-GNOME-Autostart-enabled=true
                        """);
                }
                else if (File.Exists(entry))
                {
                    File.Delete(entry);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not change autostart", ex);
        }
    }
}
