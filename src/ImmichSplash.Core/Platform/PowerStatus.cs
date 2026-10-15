using System.Runtime.InteropServices;

namespace ImmichSplash.Core.Platform;

public static partial class PowerStatus
{
    /// <summary>True when the machine is running on battery. Unknown states report false.</summary>
    public static bool IsOnBattery()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return GetSystemPowerStatus(out var status) && status.ACLineStatus == 0;

            if (OperatingSystem.IsLinux())
                return IsOnBatteryLinux();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read power status", ex);
        }
        return false;
    }

    static bool IsOnBatteryLinux()
    {
        const string root = "/sys/class/power_supply";
        if (!Directory.Exists(root))
            return false;

        var hasBattery = false;
        foreach (var supply in Directory.EnumerateDirectories(root))
        {
            var type = ReadTrimmed(Path.Combine(supply, "type"));
            if (type == "Mains" && ReadTrimmed(Path.Combine(supply, "online")) == "1")
                return false;
            if (type == "Battery")
                hasBattery = true;
        }
        return hasBattery;
    }

    static string? ReadTrimmed(string path) => File.Exists(path) ? File.ReadAllText(path).Trim() : null;

    [StructLayout(LayoutKind.Sequential)]
    struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);
}
