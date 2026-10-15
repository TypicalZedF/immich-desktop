namespace ImmichSplash.App.Services;

/// <summary>Routes app.immich:// links (from the browser, via a second launch of the app) to the sign-in screen.</summary>
public static class OAuthCallbacks
{
    public static event Action<string>? Received;

    public static void Deliver(string url) => Received?.Invoke(url);
}
