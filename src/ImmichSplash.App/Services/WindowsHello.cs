#if WINDOWS
using Windows.Security.Credentials.UI;
#endif

namespace ImmichSplash.App.Services;

public enum HelloResult
{
    Verified,
    Canceled,
    Failed,
    Unavailable,
}

/// <summary>
/// The system's own "confirm it's you" for the app lock: Windows Hello (face, fingerprint or PIN), or on Linux the
/// desktop's sign-in prompt through polkit (fingerprint when set up, else the account password).
/// </summary>
public static class WindowsHello
{
    /// <summary>What it's called here, for buttons and settings.</summary>
    public static string Name => OperatingSystem.IsLinux() ? Tr.T("Linux login") : Tr.T("Windows Hello");

    public static string Tip => OperatingSystem.IsLinux() ? Tr.T("Fingerprint or password") : Tr.T("Face, fingerprint or PIN");

    public static string UnlockButtonText => OperatingSystem.IsLinux() ? Tr.T("Unlock with your Linux login") : Tr.T("Unlock with Windows Hello");

    public static string SettingDescription => OperatingSystem.IsLinux()
        ? Tr.T("Require your Linux login (fingerprint or password) or an app password to open this window. Backup keeps running while locked.")
        : Tr.T("Require Windows Hello or a password to open this window. Backup keeps running while locked.");

    public static async Task<bool> IsAvailableAsync()
    {
        if (OperatingSystem.IsLinux())
            return await Core.Platform.Polkit.IsAvailableAsync();
#if WINDOWS
        try
        {
            return await UserConsentVerifier.CheckAvailabilityAsync() == UserConsentVerifierAvailability.Available;
        }
        catch (Exception ex)
        {
            Core.Log.Warn("Windows Hello availability check failed", ex);
            return false;
        }
#else
        await Task.CompletedTask;
        return false;
#endif
    }

    /// <summary>Replaces the real prompt (the screenshot harness shows the lock screen without asking Windows).</summary>
    public static Func<Task<HelloResult>>? FakePrompt { get; set; }

    /// <param name="window">Native window handle, so the prompt appears on top of our window.</param>
    public static async Task<HelloResult> VerifyAsync(IntPtr window, string message)
    {
        if (FakePrompt is { } fake)
            return await fake();
        if (OperatingSystem.IsLinux())
        {
            return await Core.Platform.Polkit.AuthenticateAsync() switch
            {
                Core.Platform.PolkitResult.Verified => HelloResult.Verified,
                Core.Platform.PolkitResult.Canceled => HelloResult.Canceled,
                Core.Platform.PolkitResult.Unavailable => HelloResult.Unavailable,
                _ => HelloResult.Failed,
            };
        }
#if WINDOWS
        try
        {
            var result = window != IntPtr.Zero
                ? await UserConsentVerifierInterop.RequestVerificationForWindowAsync(window, message)
                : await UserConsentVerifier.RequestVerificationAsync(message);
            return result switch
            {
                UserConsentVerificationResult.Verified => HelloResult.Verified,
                UserConsentVerificationResult.Canceled => HelloResult.Canceled,
                UserConsentVerificationResult.DeviceNotPresent or UserConsentVerificationResult.NotConfiguredForUser
                    or UserConsentVerificationResult.DisabledByPolicy => HelloResult.Unavailable,
                _ => HelloResult.Failed,
            };
        }
        catch (Exception ex)
        {
            Core.Log.Warn("Windows Hello verification failed", ex);
            return HelloResult.Failed;
        }
#else
        await Task.CompletedTask;
        return HelloResult.Unavailable;
#endif
    }
}
