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
    public async Task Naming_a_person_sends_only_the_name()
    {
        var server = new FakeImmichServer();
        using var client = Client(server);

        await client.UpdatePersonAsync("p1", "Sam", TestContext.Current.CancellationToken);

        Assert.Equal("/api/people/p1 {\"name\":\"Sam\"}", Assert.Single(server.PeopleUpdates));
    }

    [Fact]
    public async Task A_discarded_face_is_deleted_on_the_server()
    {
        var server = new FakeImmichServer();
        using var client = Client(server);

        await client.DeletePersonAsync("p9", TestContext.Current.CancellationToken);

        Assert.Equal("DELETE /api/people/p9", Assert.Single(server.PeopleUpdates));
    }
}
