using System.Runtime.InteropServices;

namespace ImmichSplash.App.Services;

/// <summary>
/// After the window closes the app only runs the tray and the sync engine, so give the
/// UI memory back to the OS instead of holding on to it.
/// </summary>
public static partial class MemoryTrimmer
{
    public static void Trim()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        if (OperatingSystem.IsWindows())
        {
            try
            {
                SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
            }
            catch
            {
                // Purely an optimization.
            }
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);
}
