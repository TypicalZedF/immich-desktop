#if WINDOWS
using Windows.Networking.Connectivity;
#endif
using ImmichSplash.Core.Platform;

namespace ImmichSplash.App.Services;

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
        return new NetworkSnapshot(profile.ProfileName, metered);
    }
#endif
}
