using ImmichSplash.Core.Api;
using ImmichSplash.Core.Sync;

namespace ImmichSplash.Core.Tests;

public class DroppedFilesTests
{
    [Fact]
    public void Folders_are_searched_for_photos_and_videos_only()
    {
        using var temp = new TempDir();
        var photo = temp.File("drop/a.jpg");
        temp.File("drop/sub/b.mp4");
        temp.File("drop/notes.txt");
        var loose = temp.File("c.png");

        var found = DroppedFilesUploader.CollectMedia([Path.Combine(temp.Path, "drop"), loose, photo], MediaTypes.Default);

        Assert.Equal(["a.jpg", "b.mp4", "c.png"], found.Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task Dropped_files_upload_once_and_land_in_the_album()
    {
        using var temp = new TempDir();
        var server = new FakeImmichServer();
        using var client = new ImmichClient(new Uri("https://immich.test/api/"), new AuthCredential(AuthKind.ApiKey, "key"), server);
        var files = new[] { temp.File("a.jpg"), temp.File("b.jpg"), temp.File("already.jpg") };
        server.Preload(files[2]);
        var reports = new List<DropProgress>();

        var result = await DroppedFilesUploader.UploadAsync(client, files, "Trip", new BandwidthLimiter(),
            new SyncProgress<DropProgress>(reports.Add), TestContext.Current.CancellationToken);

        Assert.Equal(new DropResult(2, 1, 0), result);
        Assert.Equal(["a.jpg", "b.jpg"], server.UploadedFileNames.Order());
        var album = Assert.Single(server.Albums.Values);
        Assert.Equal("Trip", album.Name);
        Assert.Equal(3, album.Assets.Count);
        Assert.Equal(3, reports[^1].Done);
    }

    /// <summary>Reports straight away (Progress&lt;T&gt; would post to a thread pool and race the asserts).</summary>
    sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
