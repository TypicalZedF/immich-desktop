using System.Diagnostics;
using ImmichDesktop.Core.Services;
using Microsoft.Win32;

namespace ImmichDesktop.Core.Platform;

/// <summary>
/// Registers this app as the handler for app.immich:// links so the browser can hand SSO sign-ins back to it.
/// Per-user registration; no admin rights needed.
/// </summary>
public static class UrlProtocol
{
    const string WindowsKey = @"Software\Classes\" + OAuthSession.Scheme;

    static string? Executable =>
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage ? appImage : Environment.ProcessPath;

    public static void Register()
    {
        var exe = Executable;
        if (exe is null)
            return;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.CreateSubKey(WindowsKey);
                key.SetValue("", $"URL:{AppInfo.Name}");
                key.SetValue("URL Protocol", "");
                using (var icon = key.CreateSubKey("DefaultIcon"))
                    icon.SetValue("", $"\"{exe}\",0");
                using var command = key.CreateSubKey(@"shell\open\command");
                command.SetValue("", $"\"{exe}\" \"%1\"");
            }
            else if (OperatingSystem.IsLinux() && !Flatpak.IsRunning) // a Flatpak's own desktop file handles the link
            {
                var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "applications");
                Directory.CreateDirectory(apps);
                File.WriteAllText(Path.Combine(apps, "immich-desktop-url.desktop"), $"""
                    [Desktop Entry]
                    Type=Application
                    Name={AppInfo.Name}
                    Exec="{exe}" %u
                    NoDisplay=true
                    MimeType=x-scheme-handler/{OAuthSession.Scheme};
                    """);
                Process.Start(new ProcessStartInfo("xdg-mime", $"default immich-desktop-url.desktop x-scheme-handler/{OAuthSession.Scheme}") { UseShellExecute = false })?.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not register the app.immich link handler", ex);
        }
    }

    public static void Unregister()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Registry.CurrentUser.DeleteSubKeyTree(WindowsKey, throwOnMissingSubKey: false);
            else if (OperatingSystem.IsLinux())
                File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "applications", "immich-desktop-url.desktop"));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not remove the app.immich link handler", ex);
        }
    }
}
