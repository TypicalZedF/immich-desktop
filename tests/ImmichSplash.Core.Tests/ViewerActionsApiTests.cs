using ImmichSplash.Core.Api;

namespace ImmichSplash.Core.Tests;

public class ViewerActionsApiTests
{
    static ImmichClient Client(FakeImmichServer server) =>
        new(new Uri("https://immich.test/api/"), new AuthCredential(AuthKind.ApiKey, "key"), server);

    [Theory]
    [InlineData("refresh-faces")]
    [InlineData("refresh-metadata")]
    [InlineData("regenerate-thumbnail")]
    public async Task Photo_jobs_are_sent_the_way_Immich_expects(string job)
    {
        var server = new FakeImmichServer();
        using var client = Client(server);

        await client.RunAssetJobAsync(["a1"], job, TestContext.Current.CancellationToken);

        Assert.Equal(job, Assert.Single(server.AssetJobs));
    }

    [Fact]
    public async Task Similar_photos_are_searched_by_the_photo_not_by_text()
    {
        var server = new FakeImmichServer();
        using var client = Client(server);

        var found = await client.SimilarSearchAsync("a1", 50, TestContext.Current.CancellationToken);

        Assert.Empty(found);
        Assert.Equal("similar:a1", Assert.Single(server.SmartSearches));
    }

    [Fact]
    public async Task Naming_and_hiding_a_person_sends_only_what_changed()
    {
        var server = new FakeImmichServer();
        using var client = Client(server);

        await client.UpdatePersonAsync("p1", "Sam", null, TestContext.Current.CancellationToken);
        await client.UpdatePersonAsync("p2", null, true, TestContext.Current.CancellationToken);

        Assert.Equal(["/api/people/p1 {\"name\":\"Sam\"}", "/api/people/p2 {\"isHidden\":true}"], server.PeopleUpdates);
    }
}
