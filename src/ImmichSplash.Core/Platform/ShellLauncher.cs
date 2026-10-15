using System.Diagnostics;

namespace ImmichSplash.Core.Platform;

/// <summary>Opens URLs and folders with the OS default handler (Explorer / xdg-open).</summary>
public static class ShellLauncher
{
    public static void Open(string target)
    {
        try
        {
            if (OperatingSystem.IsLinux())
                Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { target }, UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {target}", ex);
        }
    }

    /// <summary>Opens the folder containing <paramref name="filePath"/> and selects the file where supported.</summary>
    public static void Reveal(string filePath)
    {
        if (OperatingSystem.IsWindows() && File.Exists(filePath))
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", filePath }, UseShellExecute = false });
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("Could not reveal file", ex);
            }
        }
        Open(Path.GetDirectoryName(filePath) ?? filePath);
    }
}
