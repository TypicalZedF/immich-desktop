using System.Diagnostics;

namespace ImmichDesktop.Core.Platform;

/// <summary>The network this PC is on: its name (the Wi-Fi name, or the wired network's name) and whether it's metered.</summary>
public sealed record NetworkSnapshot(string? Name, bool IsMetered)
{
    public static NetworkSnapshot Unknown { get; } = new(null, false);
}

/// <summary>
/// Reads the current network. Windows: provided by the app (Windows' own network information).
/// Linux: NetworkManager.
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

    static NetworkSnapshot FromNetworkManager()
    {
        // The active connection that carries the default route is listed first.
        var name = Run("nmcli", "-t", "-f", "NAME,TYPE", "connection", "show", "--active")?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(':'))
            .Where(parts => parts.Length >= 2 && parts[1] is not ("loopback" or "bridge" or "tun" or "wireguard"))
            .Select(parts => parts[0].Replace("\\:", ":"))
            .FirstOrDefault();
        var metered = Run("busctl", "get-property", "org.freedesktop.NetworkManager", "/org/freedesktop/NetworkManager",
            "org.freedesktop.NetworkManager", "Metered")?.Trim();
        // NM_METERED_YES = 1, NM_METERED_GUESS_YES = 3
        return new NetworkSnapshot(string.IsNullOrWhiteSpace(name) ? null : name, metered is "u 1" or "u 3");
    }

    static string? Run(string file, params string[] args)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEnd();
            return process.WaitForExit(3000) && process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null; // tool not installed
        }
    }
}
