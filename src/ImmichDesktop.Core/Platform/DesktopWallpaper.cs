using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ImmichDesktop.Core.Platform;

/// <summary>
/// Sets the desktop background. Windows: the standard wallpaper setting, filling the screen.
/// Linux: GNOME (and GNOME-based desktops) through gsettings.
/// </summary>
public static partial class DesktopWallpaper
{
    public static bool IsSupported => OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && HasGsettings);

    static bool HasGsettings =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(dir => File.Exists(Path.Combine(dir, "gsettings")));

    public static void Set(string imagePath)
    {
        if (OperatingSystem.IsWindows())
        {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop"))
            {
                key.SetValue("WallpaperStyle", "10"); // fill
                key.SetValue("TileWallpaper", "0");
            }
            if (!SystemParametersInfo(SpiSetDeskWallpaper, 0, imagePath, SpifUpdateIniFile | SpifSendChange))
                throw new InvalidOperationException($"Windows refused the new background (error {Marshal.GetLastWin32Error()}).");
            return;
        }
        if (OperatingSystem.IsLinux())
        {
            var uri = new Uri(imagePath).AbsoluteUri;
            Gsettings("picture-options", "zoom");
            Gsettings("picture-uri", uri);
            Gsettings("picture-uri-dark", uri);
            return;
        }
        throw new PlatformNotSupportedException();
    }

    static void Gsettings(string key, string value)
    {
        using var process = Process.Start(new ProcessStartInfo("gsettings", ["set", "org.gnome.desktop.background", key, value])
        {
            UseShellExecute = false,
            RedirectStandardError = true,
        });
        process?.WaitForExit(5000);
    }

    const uint SpiSetDeskWallpaper = 0x0014;
    const uint SpifUpdateIniFile = 0x01;
    const uint SpifSendChange = 0x02;

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, string value, uint flags);
}
