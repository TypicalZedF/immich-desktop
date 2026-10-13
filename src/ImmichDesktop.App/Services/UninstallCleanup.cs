using ImmichDesktop.Core;
using ImmichDesktop.Core.Platform;

namespace ImmichDesktop.App.Services;

/// <summary>
/// Runs from the uninstaller. Removes the autostart entry, the stored sign-in and logs.
/// Settings and the backup index are kept so a reinstall picks up where it left off without re-checking everything.
/// </summary>
public static class UninstallCleanup
{
    public static void Run()
    {
        try { Autostart.SetEnabled(false); } catch { /* best effort */ }
        try { CredentialStore.Delete(); } catch { /* best effort */ }
        try { Directory.Delete(AppPaths.LogsDir, recursive: true); } catch { /* best effort */ }
    }
}
