using ImmichDesktop.Core.Api;
using ImmichDesktop.Core.Settings;
using ImmichDesktop.Core.Sync;

namespace ImmichDesktop.Core.Tests;

public class FileFilterTests
{
    static readonly string Root = Path.Combine(Path.GetTempPath(), "filter-root");

    static string P(params string[] parts) => Path.Combine([Root, .. parts]);

    [Fact]
    public void Matches_photos_and_videos_only()
    {
        var filter = new FileFilter(new SyncFolder { Path = Root }, MediaTypes.Default);
        Assert.True(filter.IsMatch(P("a.JPG")));
        Assert.True(filter.IsMatch(P("sub", "clip.mp4")));
        Assert.True(filter.IsMatch(P("raw.cr3")));
        Assert.False(filter.IsMatch(P("notes.txt")));
        Assert.False(filter.IsMatch(P("noextension")));
    }

    [Fact]
    public void Respects_photo_and_video_toggles()
    {
        var filter = new FileFilter(new SyncFolder { Path = Root, IncludeVideos = false }, MediaTypes.Default);
        Assert.True(filter.IsMatch(P("a.heic")));
        Assert.False(filter.IsMatch(P("b.mov")));
    }

    [Fact]
    public void Exclude_patterns_match_at_any_depth()
    {
        var folder = new SyncFolder { Path = Root, ExcludePatterns = ["*.png", "Screenshots", "# comment", "Trips/2019/**"] };
        var filter = new FileFilter(folder, MediaTypes.Default);

        Assert.False(filter.IsMatch(P("deep", "er", "x.png")));
        Assert.False(filter.IsMatch(P("Screenshots", "s.jpg")));
        Assert.False(filter.IsMatch(P("Phone", "Screenshots", "s.jpg")));
        Assert.False(filter.IsMatch(P("Trips", "2019", "a.jpg")));
        Assert.True(filter.IsMatch(P("Trips", "2020", "a.jpg")));
        Assert.True(filter.IsMatch(P("x.jpg")));
    }

    [Fact]
    public void Server_mime_lists_fall_back_to_builtin_extensions()
    {
        var media = MediaTypes.FromServer(new MediaTypesInfo(["image/jpeg"], ["video/mp4"]));
        Assert.Equal(MediaKind.Image, media.Classify("x.jpg"));

        var fromExtensions = MediaTypes.FromServer(new MediaTypesInfo([".jpg"], [".mp4"]));
        Assert.Equal(MediaKind.None, fromExtensions.Classify("x.png"));
    }
}
