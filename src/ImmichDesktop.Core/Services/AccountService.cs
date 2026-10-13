using ImmichDesktop.Core.Api;
using ImmichDesktop.Core.Platform;
using ImmichDesktop.Core.Settings;
using ImmichDesktop.Core.Storage;

namespace ImmichDesktop.Core.Services;

/// <summary>Owns the signed-in session: credentials, the API client and the account identity.</summary>
public sealed class AccountService(SettingsStore settings, StateDatabase db) : IDisposable
{
    const string AccountMetaKey = "account";

    public ImmichClient? Client { get; private set; }

    public bool IsSignedIn => Client is not null;

    /// <summary>Raised when the user signs in or out. May fire on any thread.</summary>
    public event Action? SessionChanged;

    /// <summary>Restores the previous session from disk, if any.</summary>
    public void Restore()
    {
        var s = settings.Current;
        if (!s.IsConfigured || !Uri.TryCreate(s.ServerUrl, UriKind.Absolute, out var apiUrl))
            return;

        var credential = CredentialStore.Load();
        if (credential is null)
            return;

        Client = new ImmichClient(apiUrl, credential) { DeviceId = Environment.MachineName };
        Log.Info($"Restored session for {s.UserEmail} on {apiUrl.Host}");
    }

    public async Task<UserInfo> SignInWithPasswordAsync(ServerInfo server, string email, string password, CancellationToken ct)
    {
        string token;
        using (var anonymous = new ImmichClient(server.ApiUrl, null))
            token = await anonymous.LoginAsync(email, password, ct);
        return await CompleteSignInAsync(server, new AuthCredential(AuthKind.AccessToken, token), ct);
    }

    public Task<UserInfo> SignInWithApiKeyAsync(ServerInfo server, string apiKey, CancellationToken ct) =>
        CompleteSignInAsync(server, new AuthCredential(AuthKind.ApiKey, apiKey.Trim()), ct);

    async Task<UserInfo> CompleteSignInAsync(ServerInfo server, AuthCredential credential, CancellationToken ct)
    {
        var client = new ImmichClient(server.ApiUrl, credential) { ServerVersion = server.Version, DeviceId = Environment.MachineName };
        try
        {
            var me = await client.GetMeAsync(ct);

            // The local index belongs to one account on one server; start fresh if that changes.
            var accountKey = $"{server.ApiUrl}|{me.Id}";
            if (db.GetMeta(AccountMetaKey) != accountKey)
            {
                db.ResetAll();
                db.SetMeta(AccountMetaKey, accountKey);
            }

            CredentialStore.Save(credential);
            settings.Update(s =>
            {
                s.ServerUrl = server.ApiUrl.ToString();
                s.WebUrl = server.WebUrl.ToString();
                s.ServerVersion = server.Version.ToString();
                s.UserId = me.Id;
                s.UserName = me.Name;
                s.UserEmail = me.Email;
            });

            var previous = Client;
            Client = client;
            previous?.Dispose();
            Log.Info($"Signed in as {me.Email} on {server.Host} ({server.Version})");
            SessionChanged?.Invoke();
            return me;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task SignOutAsync()
    {
        var client = Client;
        Client = null;
        SessionChanged?.Invoke();

        if (client is not null)
        {
            if (client.Credential?.Kind == AuthKind.AccessToken)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await client.LogoutAsync(cts.Token);
                }
                catch (Exception ex)
                {
                    Log.Warn("Server logout failed (signing out locally anyway)", ex);
                }
            }
            client.Dispose();
        }

        CredentialStore.Delete();
        settings.Update(s =>
        {
            s.UserId = null;
            s.UserName = null;
            s.UserEmail = null;
        });
        Log.Info("Signed out");
    }

    /// <summary>For tests: use a pre-built client without touching disk.</summary>
    internal void UseClient(ImmichClient client)
    {
        Client = client;
        SessionChanged?.Invoke();
    }

    public void Dispose() => Client?.Dispose();
}
