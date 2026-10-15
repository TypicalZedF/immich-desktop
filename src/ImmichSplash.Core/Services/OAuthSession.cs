using System.Security.Cryptography;
using System.Text;

namespace ImmichSplash.Core.Services;

/// <summary>
/// One in-flight SSO sign-in (OAuth authorization code + PKCE).
/// The browser comes back to <see cref="RedirectUri"/>, the same callback the official mobile app uses,
/// so it works with identity providers already configured for Immich.
/// </summary>
public sealed class OAuthSession
{
    public const string Scheme = "app.immich";
    public const string RedirectUri = "app.immich:///oauth-callback";

    OAuthSession(string state, string codeVerifier)
    {
        State = state;
        CodeVerifier = codeVerifier;
        CodeChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
    }

    public string State { get; }
    public string CodeVerifier { get; }
    public string CodeChallenge { get; }

    public static OAuthSession Create() =>
        new(Base64Url(RandomNumberGenerator.GetBytes(24)), Base64Url(RandomNumberGenerator.GetBytes(48)));

    public static bool IsCallback(string? url) =>
        url is not null && url.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Browsers and OSes sometimes rewrite custom-scheme links (e.g. "app.immich:/oauth-callback" with one slash,
    /// or a trailing slash). The server must see exactly <see cref="RedirectUri"/> or the token exchange fails,
    /// so rebuild it from the query string, as Immich does for its own mobile redirect.
    /// </summary>
    public static string Normalize(string callbackUrl)
    {
        var index = callbackUrl.IndexOf('?');
        return index < 0 ? RedirectUri : RedirectUri + callbackUrl[index..];
    }

    /// <summary>Parameter names only (never values), for logging.</summary>
    public static string Describe(string callbackUrl)
    {
        var index = callbackUrl.IndexOf('?');
        var path = index < 0 ? callbackUrl : callbackUrl[..index];
        var names = index < 0 ? [] : callbackUrl[(index + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=')[0]);
        return $"{path} (params: {string.Join(", ", names)})";
    }

    /// <summary>When the identity provider refused the sign-in, its error message; otherwise null.</summary>
    public static string? ProviderError(string callbackUrl)
    {
        string? error = null, description = null;
        foreach (var (key, value) in Query(callbackUrl))
        {
            if (key == "error") error = value;
            if (key == "error_description") description = value;
        }
        return error is null ? null : description ?? error;
    }

    static IEnumerable<(string Key, string Value)> Query(string url)
    {
        var index = url.IndexOf('?');
        if (index < 0)
            yield break;
        foreach (var part in url[(index + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            yield return (pieces[0], pieces.Length == 2 ? Uri.UnescapeDataString(pieces[1].Replace('+', ' ')) : "");
        }
    }

    /// <summary>True when the callback belongs to this sign-in attempt (protects against forged callbacks).</summary>
    public bool Matches(string callbackUrl)
    {
        var query = callbackUrl.Contains('?') ? callbackUrl[(callbackUrl.IndexOf('?') + 1)..] : "";
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            if (pieces[0] == "state" && pieces.Length == 2)
                return CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(Uri.UnescapeDataString(pieces[1])), Encoding.UTF8.GetBytes(State));
        }
        return false;
    }

    static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
