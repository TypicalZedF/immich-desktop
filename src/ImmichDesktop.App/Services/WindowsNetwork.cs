#if WINDOWS
using Windows.Networking.Connectivity;
#endif
using ImmichDesktop.Core.Platform;

namespace ImmichDesktop.App.Services;

/// <summary>
/// Windows' own view of the current network: its name (the Wi-Fi name for Wi-Fi), whether it's
/// metered, and whether it's set to Private or Public in Windows' network settings.
/// </summary>
public static class WindowsNetwork
{
    public static void Register()
    {
#if WINDOWS
        NetworkStatus.Provider = Read;
#endif
    }

#if WINDOWS
    static NetworkSnapshot Read()
    {
        var profile = NetworkInformation.GetInternetConnectionProfile();
        if (profile is null)
            return NetworkSnapshot.Unknown;
        var cost = profile.GetConnectionCost();
        // "Metered" in Windows settings, a data limit, or roaming.
        var metered = cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable || cost.Roaming || cost.OverDataLimit;
        return new NetworkSnapshot(profile.ProfileName, metered, IsPrivate(profile));
    }

    /// <summary>
    /// Private (home/work, or a work domain) vs Public, as chosen in Windows Settings → Network.
    /// Windows keeps it per network profile in the registry; null if it can't be found.
    /// </summary>
    static bool? IsPrivate(ConnectionProfile profile)
    {
        try
        {
            if (profile.GetDomainConnectivityLevel() == DomainConnectivityLevel.Authenticated)
                return true;
            using var profiles = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles");
            if (profiles is null)
                return null;
            foreach (var id in profiles.GetSubKeyNames())
            {
                using var key = profiles.OpenSubKey(id);
                if (key?.GetValue("ProfileName") is string name && name == profile.ProfileName && key.GetValue("Category") is int category)
                    return category is 1 or 2; // 0 Public, 1 Private, 2 Domain
            }
        }
        catch (Exception ex)
        {
            ImmichDesktop.Core.Log.Info($"Couldn't read the network category: {ex.Message}");
        }
        return null;
    }
#endif
}
