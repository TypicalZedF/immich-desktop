using ImmichSplash.Core.Api;

namespace ImmichSplash.Core.Tests;

public class PinCodeTests
{
    static ImmichClient Client(FakeImmichServer server) =>
        new(new Uri("https://immich.test/api/"), new AuthCredential(AuthKind.ApiKey, "key"), server);

    [Fact]
    public async Task A_new_PIN_can_be_made_and_then_opens_the_locked_folder()
    {
        var server = new FakeImmichServer();
        using var client = Client(server);
        var ct = TestContext.Current.CancellationToken;

        Assert.False((await client.GetAuthStatusAsync(ct)).HasPin);
        await client.SetupPinAsync("246810", ct);
        await client.UnlockSessionAsync("246810", ct);

        Assert.Equal((true, true), ((await client.GetAuthStatusAsync(ct)).HasPin, (await client.GetAuthStatusAsync(ct)).IsElevated));
    }

    [Fact]
    public async Task Changing_the_PIN_needs_the_current_one()
    {
        var server = new FakeImmichServer { Pin = "111111" };
        using var client = Client(server);
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ImmichApiException>(() => client.ChangePinAsync("999999", "222222", ct));
        await client.ChangePinAsync("111111", "222222", ct);

        Assert.Equal("222222", server.Pin);
    }

    [Fact]
    public async Task Removing_the_PIN_needs_the_current_one()
    {
        var server = new FakeImmichServer { Pin = "111111" };
        using var client = Client(server);
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ImmichApiException>(() => client.RemovePinAsync("000000", ct));
        await client.RemovePinAsync("111111", ct);

        Assert.Null(server.Pin);
    }
}
