using ImmichDesktop.Core.Api;
using ImmichDesktop.Core.Platform;
using ImmichDesktop.Core.Services;
using ImmichDesktop.Core.Settings;
using ImmichDesktop.Core.Storage;

namespace ImmichDesktop.Core.Tests;

/// <summary>Automatic server switching: local address at home, external addresses (in order) elsewhere.</summary>
public class NetworkTests
{
    static AppSettings Configured() => new()
    {
        ServerUrl = "https://photos.example.com/api/",
        UserId = "u1",
        AutoSwitchServer = true,
        LocalNetworkName = "Home WiFi",
        LocalServerUrl = "192.168.1.10:2283",
        ExternalServerUrls = ["https://vpn.example.com", "https://photos.example.com"],
    };

    [Theory]
    [InlineData("192.168.1.10:2283", "http://192.168.1.10:2283/api/")]
    [InlineData("https://photos.example.com", "https://photos.example.com/api/")]
    [InlineData("https://example.com/immich/api", "https://example.com/immich/api/")]
    [InlineData("not a url at all", null)]
    [InlineData("ftp://example.com", null)]
    public void Normalizes_typed_addresses(string input, string? expected) =>
        Assert.Equal(expected, ServerEndpoints.ToApiUrl(input)?.ToString());

    [Fact]
    public void Home_network_tries_the_local_address_first()
    {
        var candidates = ServerEndpoints.Candidates(Configured(), "home wifi").Select(u => u.ToString());
        Assert.Equal(["http://192.168.1.10:2283/api/", "https://vpn.example.com/api/", "https://photos.example.com/api/"], candidates);
    }

    [Fact]
    public void Away_uses_external_addresses_in_order_and_switching_off_uses_the_sign_in_address()
    {
        var settings = Configured();
        Assert.Equal(["https://vpn.example.com/api/", "https://photos.example.com/api/"],
            ServerEndpoints.Candidates(settings, "Coffee Shop").Select(u => u.ToString()));

        settings.AutoSwitchServer = false;
        Assert.Equal(["https://photos.example.com/api/"], ServerEndpoints.Candidates(settings, "Home WiFi").Select(u => u.ToString()));
    }

    [Fact]
    public async Task Monitor_switches_to_the_first_address_that_answers()
    {
        using var temp = new TempDir();
        var store = new SettingsStore(Path.Combine(temp.Path, "settings.json"));
        var configured = Configured();
        store.Update(s =>
        {
            s.ServerUrl = configured.ServerUrl;
            s.UserId = configured.UserId;
            s.AutoSwitchServer = true;
            s.LocalNetworkName = configured.LocalNetworkName;
            s.LocalServerUrl = configured.LocalServerUrl;
            s.ExternalServerUrls = configured.ExternalServerUrls;
        });
        var db = new StateDatabase(Path.Combine(temp.Path, "state.db"));
        using var account = new AccountService(store, db);
        account.UseClient(new ImmichClient(new Uri("https://photos.example.com/api/"), new AuthCredential(AuthKind.ApiKey, "k")));
        var reachable = new HashSet<string> { "192.168.1.10", "photos.example.com" };
        using var monitor = new NetworkMonitor(store, account, (uri, _) => Task.FromResult(reachable.Contains(uri.Host)));

        try
        {
            NetworkStatus.Override = new NetworkSnapshot("Home WiFi", false);
            await monitor.CheckAsync();
            Assert.Equal("192.168.1.10", account.Client!.ApiUrl.Host);
            Assert.True(monitor.IsUsingLocal);

            // Away: the VPN address is down, so the public one is used.
            NetworkStatus.Override = new NetworkSnapshot("Phone hotspot", true);
            await monitor.CheckAsync();
            Assert.Equal("photos.example.com", account.Client!.ApiUrl.Host);
            Assert.False(monitor.IsUsingLocal);
            Assert.True(monitor.IsMetered);
        }
        finally
        {
            NetworkStatus.Override = null;
        }
    }
}
