using ImmichSplash.Core.Api;

namespace ImmichSplash.Core.Tests;

public class MergePeopleTests
{
    const string Kept = "11111111-1111-4111-8111-111111111111";
    const string Merged = "22222222-2222-4222-8222-222222222222";

    static ImmichClient Client(FakeImmichServer server) =>
        new(new Uri("https://immich.test/api/"), new AuthCredential(AuthKind.ApiKey, "key"), server);

    [Fact]
    public async Task The_person_kept_comes_first_so_their_name_wins()
    {
        var server = new FakeImmichServer();
        using var client = Client(server);

        await client.MergePeopleAsync(Kept, [Merged], TestContext.Current.CancellationToken);

        Assert.Equal($$"""/api/people/merge {"ids":["{{Kept}}","{{Merged}}"]}""", Assert.Single(server.PeopleMerges));
    }

    [Fact]
    public async Task Older_servers_get_the_person_kept_in_the_path()
    {
        var server = new FakeImmichServer { LegacyMergeOnly = true };
        using var client = Client(server);

        await client.MergePeopleAsync(Kept, [Merged], TestContext.Current.CancellationToken);

        Assert.Equal($$"""/api/people/{{Kept}}/merge {"ids":["{{Merged}}"]}""", Assert.Single(server.PeopleMerges));
    }
}
