using System.Runtime.InteropServices;

namespace ImmichSplash.Core.Platform;

/// <summary>How long since the keyboard or mouse was last used (for work that should wait until you step away).</summary>
public static partial class UserIdle
{
    /// <summary>Time since the last input, or null where the system doesn't say (then treat the PC as idle).</summary>
    public static TimeSpan? Duration()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info))
                return null;
            var now = (uint)Environment.TickCount;
            return TimeSpan.FromMilliseconds(unchecked(now - info.Time));
        }
        catch (Exception)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);
}
