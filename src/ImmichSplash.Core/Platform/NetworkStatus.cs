using Tmds.DBus.Protocol;

namespace ImmichSplash.Core.Platform;

/// <summary>
/// The network this PC is on: its name (the Wi-Fi name, or the wired network's name), whether it's
/// and whether it's metered.
/// </summary>
public sealed record NetworkSnapshot(string? Name, bool IsMetered)
{
    public static NetworkSnapshot Unknown { get; } = new(null, false);
}

/// <summary>
/// Reads the current network. Windows: provided by the app (Windows' own network information).
/// Linux: NetworkManager (over D-Bus, so it also works in the Flatpak).
/// </summary>
public static class NetworkStatus
{
    /// <summary>Set by the app on Windows, where the system API needs the Windows SDK projection.</summary>
    public static Func<NetworkSnapshot>? Provider { get; set; }

    /// <summary>For tests and the screenshot tool.</summary>
    public static NetworkSnapshot? Override { get; set; }

    public static NetworkSnapshot Current()
    {
        if (Override is { } fixedValue)
            return fixedValue;
        try
        {
            if (Provider is { } provider)
                return provider();
            if (OperatingSystem.IsLinux())
                return FromNetworkManager();
        }
        catch (Exception ex)
        {
            Log.Info($"Could not read the network: {ex.Message}");
        }
        return NetworkSnapshot.Unknown;
    }

    static NetworkSnapshot FromNetworkManager() => LinuxDBus.Wait(LinuxDBus.UseAsync(system: true, FromNetworkManagerAsync));

    static async Task<NetworkSnapshot> FromNetworkManagerAsync(Connection bus)
    {
        const string nm = "org.freedesktop.NetworkManager", root = "/org/freedesktop/NetworkManager";
        // The connection that carries the default route, unless that's a VPN: then the network underneath it.
        var connections = new List<string> { (await LinuxDBus.GetPropertyAsync(bus, nm, root, nm, "PrimaryConnection")).GetObjectPathAsString() };
        connections.AddRange((await LinuxDBus.GetPropertyAsync(bus, nm, root, nm, "ActiveConnections")).GetArray<ObjectPath>().Select(p => p.ToString()));
        string? name = null;
        foreach (var path in connections.Where(p => p is not ("/" or "")).Distinct())
        {
            var type = (await LinuxDBus.GetPropertyAsync(bus, nm, path, nm + ".Connection.Active", "Type")).GetString();
            if (type is "loopback" or "bridge" or "tun" or "wireguard" or "vpn")
                continue;
            name = (await LinuxDBus.GetPropertyAsync(bus, nm, path, nm + ".Connection.Active", "Id")).GetString();
            break;
        }
        // NM_METERED_YES = 1, NM_METERED_GUESS_YES = 3
        var metered = (await LinuxDBus.GetPropertyAsync(bus, nm, root, nm, "Metered")).GetUInt32();
        return new NetworkSnapshot(string.IsNullOrWhiteSpace(name) ? null : name, metered is 1 or 3);
    }
}
