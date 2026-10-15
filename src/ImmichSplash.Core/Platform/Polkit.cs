using Tmds.DBus.Protocol;

namespace ImmichSplash.Core.Platform;

public enum PolkitResult
{
    Verified,
    Canceled,
    Failed,
    Unavailable,
}

/// <summary>
/// Confirms it's you with the desktop's own sign-in prompt (polkit): KDE Plasma, GNOME and others show it, and it
/// takes a fingerprint when one is set up, else the account password. A Flatpak can't add its own polkit action, so
/// this asks for the one every polkit system has and that always asks again (pkexec's "run as administrator";
/// nothing is run). On a computer with several administrators the prompt may offer to pick one.
/// </summary>
public static class Polkit
{
    const string Service = "org.freedesktop.PolicyKit1", Path = "/org/freedesktop/PolicyKit1/Authority", Interface = "org.freedesktop.PolicyKit1.Authority";
    const string Action = "org.freedesktop.policykit.exec";

    public static async Task<bool> IsAvailableAsync()
    {
        if (!OperatingSystem.IsLinux())
            return false;
        try
        {
            return await LinuxDBus.UseAsync(system: true, async bus =>
            {
                await LinuxDBus.GetPropertyAsync(bus, Service, Path, Interface, "BackendName");
                return true;
            });
        }
        catch (Exception ex)
        {
            Log.Info($"polkit isn't available: {ex.Message}");
            return false;
        }
    }

    public static async Task<PolkitResult> AuthenticateAsync()
    {
        if (!await IsAvailableAsync())
            return PolkitResult.Unavailable;
        try
        {
            using var bus = await LinuxDBus.OpenAsync(system: true);
            var (authorized, dismissed) = await LinuxDBus.CallAsync(bus, Service, Path, Interface, "CheckAuthorization", "(sa{sv})sa{ss}us", (ref MessageWriter w) =>
            {
                // The subject is this app's bus connection: polkit finds the session from it, also from inside the Flatpak.
                w.WriteStructureStart();
                w.WriteString("system-bus-name");
                var subject = w.WriteDictionaryStart();
                w.WriteDictionaryEntryStart();
                w.WriteString("name");
                w.WriteVariantString(bus.UniqueName!);
                w.WriteDictionaryEnd(subject);
                w.WriteString(Action);
                // No details: polkit only accepts them from privileged callers ("Only trusted callers … can pass details").
                var details = w.WriteDictionaryStart();
                w.WriteDictionaryEnd(details);
                w.WriteUInt32(1); // AllowUserInteraction: show the prompt
                w.WriteString("");
            }, static (m, _) =>
            {
                var reader = m.GetBodyReader();
                reader.AlignStruct();
                var isAuthorized = reader.ReadBool();
                reader.ReadBool(); // is_challenge
                var dismissed = false;
                var end = reader.ReadDictionaryStart();
                while (reader.HasNext(end))
                {
                    reader.AlignStruct();
                    var key = reader.ReadString();
                    var value = reader.ReadString();
                    dismissed |= key == "polkit.dismissed" && value == "true";
                }
                return (isAuthorized, dismissed);
            }, timeout: TimeSpan.FromMinutes(5)); // someone typing a password
            return authorized ? PolkitResult.Verified : dismissed ? PolkitResult.Canceled : PolkitResult.Failed;
        }
        catch (DBusException ex)
        {
            Log.Warn($"polkit check failed: {ex.ErrorName} {ex.ErrorMessage}");
            return PolkitResult.Failed;
        }
        catch (Exception ex)
        {
            Log.Warn("polkit check failed", ex);
            return PolkitResult.Failed;
        }
    }
}
