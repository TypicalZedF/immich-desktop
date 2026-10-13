#if WINDOWS
using Windows.Security.Credentials.UI;
#endif

namespace ImmichDesktop.App.Services;

public enum HelloResult
{
    Verified,
    Canceled,
    Failed,
    Unavailable,
}

/// <summary>Windows Hello (face, fingerprint or PIN) verification for the app lock.</summary>
public static class WindowsHello
{
    public static async Task<bool> IsAvailableAsync()
    {
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

    /// <param name="window">Native window handle, so the prompt appears on top of our window.</param>
    public static async Task<HelloResult> VerifyAsync(IntPtr window, string message)
    {
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
