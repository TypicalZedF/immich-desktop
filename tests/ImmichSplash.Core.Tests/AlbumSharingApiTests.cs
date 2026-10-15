using ImmichSplash.Core.Api;

namespace ImmichSplash.Core.Tests;

public class AlbumSharingApiTests
{
    static ImmichClient Client(FakeImmichServer server) =>
        new(new Uri("https://immich.test/api/"), new AuthCredential(AuthKind.ApiKey, "key"), server);

    [Fact]
    public async Task Members_can_be_added_changed_and_removed()
    {
        var server = new FakeImmichServer();
        using var client = Client(server);
        var ct = TestContext.Current.CancellationToken;

        await client.AddAlbumUsersAsync("a1", ["u2", "u3"], "viewer", ct);
        await client.SetAlbumUserRoleAsync("a1", "u2", "editor", ct);
        await client.RemoveAlbumUserAsync("a1", "u3", ct);
        var sharing = await client.GetAlbumSharingAsync("a1", ct);

        Assert.Equal("Alex", sharing.Owner?.Name);
        var member = Assert.Single(sharing.Members);
        Assert.Equal(("u2", "editor"), (member.UserId, member.Role));
    }

    [Fact]
    public async Task People_you_can_share_with_are_listed()
    {
        var server = new FakeImmichServer();
        using var client = Client(server);

        var users = await client.GetUsersAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Alex", "Sam", "Riley"], users.Select(u => u.Name));
    }

    [Fact]
    public async Task A_link_can_be_set_to_allow_or_block_downloads()
    {
        var server = new FakeImmichServer();
        using var client = Client(server);

        await client.UpdateSharedLinkAsync("l1", false, TestContext.Current.CancellationToken);

        Assert.Equal("/api/shared-links/l1 {\"allowDownload\":false}", Assert.Single(server.SharedLinkPatches));
    }
}
