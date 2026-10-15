using System.Collections.Concurrent;
using ImmichSplash.Core.Api;
using ImmichSplash.Core.Platform;
using ImmichSplash.Core.Services;
using ImmichSplash.Core.Settings;
using ImmichSplash.Core.Storage;

namespace ImmichSplash.Core.Tests;

public class AccountServiceTests
{
    /// <summary>Two copies of the app running at once (as on Linux, where an older copy can stay in the tray), sharing the stored sign-in.</summary>
    sealed class TwoCopies : IDisposable
    {
        public readonly TempDir Temp = new();
        public readonly FakeImmichServer Server = new() { Sessions = new ConcurrentDictionary<string, bool>() };
        public readonly string Credentials;
        public readonly AccountService Older, Newer;
        readonly StateDatabase _olderDb, _newerDb;

        public TwoCopies()
        {
            Credentials = Path.Combine(Temp.Path, "credentials.bin");
            var settings = new SettingsStore(Path.Combine(Temp.Path, "settings.json"));
            settings.Update(s =>
            {
                s.ServerUrl = "https://immich.test/api/";
                s.UserId = "user-1";
            });

            // Both started from the same stored sign-in.
            Server.Sessions!["token-0"] = true;
            CredentialStore.Save(new AuthCredential(AuthKind.AccessToken, "token-0"), Credentials);
            _olderDb = new StateDatabase(Path.Combine(Temp.Path, "older.db"));
            _newerDb = new StateDatabase(Path.Combine(Temp.Path, "newer.db"));
            Older = new AccountService(settings, _olderDb, Credentials) { Handler = Server };
            Newer = new AccountService(settings, _newerDb, Credentials) { Handler = Server };
            Older.Restore();
            Newer.Restore();
        }

        /// <summary>"Sign in again" in the newer copy: lets go of the shared session, then signs in afresh.</summary>
        public async Task SignInAgainInNewerCopyAsync()
        {
            await Newer.SignOutAsync(keepAppLock: true);
            Server.Sessions!["token-1"] = true;
            var credential = new AuthCredential(AuthKind.AccessToken, "token-1");
            CredentialStore.Save(credential, Credentials); // as a completed sign-in does
            Newer.UseClient(new ImmichClient(new Uri("https://immich.test/api/"), credential, Server));
        }

        public void Dispose()
        {
            Older.Dispose();
            Newer.Dispose();
            Temp.Dispose();
        }
    }

    [Fact]
    public async Task Signing_in_again_in_one_copy_of_the_app_does_not_sign_out_the_other()
    {
        using var copies = new TwoCopies();
        await copies.SignInAgainInNewerCopyAsync();

        // The older copy's session was the one just logged out: the next thing it asks the server fails once...
        await Assert.ThrowsAsync<ImmichApiException>(() => copies.Older.Client!.GetMeAsync(TestContext.Current.CancellationToken));

        // ...and it carries on with the newer sign-in instead of saying "signed out" (which used to send you to
        // sign in again there, logging out the other copy in turn).
        Assert.False(copies.Older.IsRejected);
        Assert.NotNull(await copies.Older.Client!.GetMeAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(await copies.Newer.Client!.GetMeAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Signing_out_a_dropped_sign_in_keeps_a_newer_one_stored_by_another_copy()
    {
        using var copies = new TwoCopies();
        // The older copy's session is dropped by the server (nothing newer stored yet), so it shows "signed out".
        copies.Server.Sessions!.Clear();
        await Assert.ThrowsAsync<ImmichApiException>(() => copies.Older.Client!.GetMeAsync(TestContext.Current.CancellationToken));
        Assert.True(copies.Older.IsRejected);

        // Meanwhile the newer copy signs in again; then the older one lets go of its dropped sign-in.
        await copies.SignInAgainInNewerCopyAsync();
        await copies.Older.SignOutAsync(keepAppLock: true);

        Assert.Equal("token-1", CredentialStore.Load(copies.Credentials)?.Secret);
        Assert.NotNull(await copies.Newer.Client!.GetMeAsync(TestContext.Current.CancellationToken));
    }
}
