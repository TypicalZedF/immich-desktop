using System.Security.Cryptography;
using System.Text;

namespace ImmichDesktop.Core.Services;

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
