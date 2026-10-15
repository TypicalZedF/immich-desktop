using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace ImmichSplash.Core.Platform;

/// <summary>
/// Sets the desktop background. Windows: the standard wallpaper setting, filling the screen.
/// Linux: KDE Plasma through its scripting interface; elsewhere the desktop portal in the Flatpak,
/// or gsettings on GNOME (and GNOME-based desktops).
/// </summary>
public static partial class DesktopWallpaper
{
    public static bool IsSupported => OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && (IsKde || Flatpak.IsRunning || HasGsettings));

    static bool IsKde =>
        (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Split(':').Contains("KDE", StringComparer.OrdinalIgnoreCase);

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
            if (IsKde)
            {
                try
                {
                    LinuxDBus.Wait(LinuxDBus.UseAsync(system: false, bus => SetOnPlasmaAsync(bus, uri)));
                    return;
                }
                catch (Exception ex) when (Flatpak.IsRunning)
                {
                    // Plasma's scripting can be locked down; its desktop portal can set the background too.
                    Log.Info($"Plasma didn't take the background ({ex.Message}); asking the desktop portal");
                    LinuxDBus.Wait(LinuxDBus.UseAsync(system: false, bus => SetThroughPortalAsync(bus, uri)));
                    return;
                }
            }
            if (Flatpak.IsRunning) // gsettings in the sandbox only changes the sandbox's own copy
            {
                LinuxDBus.Wait(LinuxDBus.UseAsync(system: false, bus => SetThroughPortalAsync(bus, uri)));
                return;
            }
            Gsettings("picture-options", "zoom");
            Gsettings("picture-uri", uri);
            Gsettings("picture-uri-dark", uri);
            return;
        }
        throw new PlatformNotSupportedException();
    }

    /// <summary>Every desktop (screen and activity) shows the picture, filling it (FillMode 2: scaled and cropped).</summary>
    static Task<bool> SetOnPlasmaAsync(Tmds.DBus.Protocol.Connection bus, string uri)
    {
        var script = $$"""
            desktops().forEach(function (d) {
                d.wallpaperPlugin = "org.kde.image";
                d.currentConfigGroup = ["Wallpaper", "org.kde.image", "General"];
                d.writeConfig("Image", {{JsonSerializer.Serialize(uri)}});
                d.writeConfig("FillMode", 2);
            });
            """;
        return LinuxDBus.CallAsync(bus, "org.kde.plasmashell", "/PlasmaShell", "org.kde.PlasmaShell", "evaluateScript", "s",
            w => w.WriteString(script), static (_, _) => true);
    }

    /// <summary>
    /// The desktop portal's wallpaper service (GNOME and others). It may ask once whether the app can change the background.
    /// The picture is in the app's data folder, which the desktop can read (~/.var/app/…).
    /// </summary>
    static Task<bool> SetThroughPortalAsync(Tmds.DBus.Protocol.Connection bus, string uri) =>
        LinuxDBus.CallAsync(bus, "org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop",
            "org.freedesktop.portal.Wallpaper", "SetWallpaperURI", "ssa{sv}", w =>
            {
                w.WriteString(""); // no parent window
                w.WriteString(uri);
                var options = w.WriteDictionaryStart();
                w.WriteDictionaryEntryStart();
                w.WriteString("show-preview");
                w.WriteVariantBool(false);
                w.WriteDictionaryEntryStart();
                w.WriteString("set-on");
                w.WriteVariantString("background");
                w.WriteDictionaryEnd(options);
            }, static (_, _) => true);

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
