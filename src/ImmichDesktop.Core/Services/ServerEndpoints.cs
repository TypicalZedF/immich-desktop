using ImmichDesktop.Core.Settings;

namespace ImmichDesktop.Core.Services;

/// <summary>Which server addresses to try, in order, on the current network.</summary>
public static class ServerEndpoints
{
    /// <summary>
    /// Turns what the user typed ("192.168.1.5:2283", "https://photos.example.com") into an API address.
    /// Returns null when it isn't a usable address.
    /// </summary>
    public static Uri? ToApiUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;
        var text = input.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
            return null;
        var path = uri.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            path += "/api";
        return new UriBuilder(uri.Scheme, uri.Host, uri.Port, path + "/").Uri;
    }

    /// <summary>True when <paramref name="networkName"/> is the network marked as local (names compare case-insensitively).</summary>
    public static bool IsLocalNetwork(AppSettings settings, string? networkName) =>
        settings.AutoSwitchServer && !string.IsNullOrWhiteSpace(settings.LocalNetworkName) && networkName is not null &&
        string.Equals(settings.LocalNetworkName.Trim(), networkName.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The addresses to try, best first: on the local network its local address, then the external
    /// addresses in your order, then the address you signed in with.
    /// </summary>
    public static IReadOnlyList<Uri> Candidates(AppSettings settings, string? networkName)
    {
        var result = new List<Uri>();
        void Add(Uri? uri)
        {
            if (uri is not null && !result.Contains(uri))
                result.Add(uri);
        }

        if (settings.AutoSwitchServer)
        {
            if (IsLocalNetwork(settings, networkName))
                Add(ToApiUrl(settings.LocalServerUrl));
            foreach (var external in settings.ExternalServerUrls)
                Add(ToApiUrl(external));
        }
        if (Uri.TryCreate(settings.ServerUrl, UriKind.Absolute, out var primary))
            Add(primary);
        return result;
    }
}
