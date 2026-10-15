using System.Net;
using System.Text;
using System.Text.Json;
using ImmichSplash.Core.Api;

namespace ImmichSplash.Core.Tests;

/// <summary>The library features (search, albums, people, sharing) talk to the API the way the web app does.</summary>
public class LibraryApiTests
{
    sealed class Recorder(Func<HttpRequestMessage, string, object> respond) : HttpMessageHandler
    {
        public List<(string Method, string PathAndQuery, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method.Method, request.RequestUri!.PathAndQuery, body));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(respond(request, body)), Encoding.UTF8, "application/json"),
            };
        }
    }

    static ImmichClient Client(Recorder handler) =>
        new(new Uri("https://photos.example.com/api/"), new AuthCredential(AuthKind.ApiKey, "k"), handler);

    [Fact]
    public async Task Timeline_reads_live_photos_and_stacks()
    {
        var handler = new Recorder((_, _) => new
        {
            id = new[] { "a", "b", "c" },
            isImage = new[] { true, true, true },
            livePhotoVideoId = new[] { "motion", null, null },
            stack = new[] { null, new[] { "s1", "3" }, null },
        });
        using var client = Client(handler);

        var assets = await client.GetTimeBucketAsync("2026-10-01T00:00:00.000Z", CancellationToken.None);

        Assert.Equal("motion", assets[0].LivePhotoVideoId);
        Assert.True(assets[0].IsLivePhoto);
        Assert.False(assets[0].IsStack);
        Assert.Equal(("s1", 3), (assets[1].StackId, assets[1].StackCount));
        Assert.True(assets[1].IsStack);
        Assert.False(assets[2].IsLivePhoto || assets[2].IsStack);
        Assert.Contains("withStacked=true", handler.Requests[0].PathAndQuery);
    }

    [Fact]
    public async Task Album_and_person_timelines_use_the_timeline_filters()
    {
        var handler = new Recorder((_, _) => new[] { new { timeBucket = "2026-10-01T00:00:00.000Z", count = 3 } });
        using var client = Client(handler);

        await client.GetTimeBucketsAsync(new TimelineFilter(AlbumId: "album 1"), CancellationToken.None);
        await client.GetTimeBucketsAsync(new TimelineFilter(PersonId: "p1"), CancellationToken.None);
        await client.GetTimeBucketsAsync(new TimelineFilter(Favorites: true), CancellationToken.None);

        // Albums include archived photos (no visibility filter), like the web app.
        Assert.Equal("/api/timeline/buckets?order=desc&albumId=album%201", handler.Requests[0].PathAndQuery);
        Assert.Equal("/api/timeline/buckets?visibility=timeline&order=desc&withStacked=true&personId=p1", handler.Requests[1].PathAndQuery);
        Assert.Equal("/api/timeline/buckets?visibility=timeline&order=desc&withStacked=true&isFavorite=true", handler.Requests[2].PathAndQuery);
    }

    [Fact]
    public async Task Search_follows_pages_and_maps_assets()
    {
        var handler = new Recorder((_, body) =>
        {
            var page = JsonDocument.Parse(body).RootElement.GetProperty("page").GetInt32();
            return new
            {
                assets = new
                {
                    items = new[]
                    {
                        new { id = $"a{page}", type = "IMAGE", isFavorite = page == 1, fileCreatedAt = "2026-10-01T10:00:00.000Z", duration = "0:00:00.00000",
                              exifInfo = new { exifImageWidth = 4000, exifImageHeight = 3000, city = "Lisbon" } },
                        new { id = $"v{page}", type = "VIDEO", isFavorite = false, fileCreatedAt = "2026-09-01T10:00:00.000Z", duration = "0:00:12.500",
                              exifInfo = new { exifImageWidth = 0, exifImageHeight = 0, city = "Lisbon" } },
                    },
                    nextPage = page < 2 ? "2" : null,
                },
            };
        });
        using var client = Client(handler);

        var results = await client.SearchAsync(null, "Lisbon", 1000, CancellationToken.None);

        Assert.Equal(["a1", "v1", "a2", "v2"], results.Select(r => r.Id));
        Assert.True(results[0].IsImage && results[0].IsFavorite);
        Assert.Equal(4000 / 3000.0, results[0].Ratio, 3);
        Assert.False(results[1].IsImage);
        Assert.Equal(12_500, results[1].DurationMs);
        Assert.All(handler.Requests, r => Assert.Contains("\"city\":\"Lisbon\"", r.Body));
        Assert.DoesNotContain("originalFileName", handler.Requests[0].Body); // unset fields are left out (Immich v3 is strict)
    }

    [Fact]
    public async Task Shared_links_for_photos_and_albums()
    {
        var handler = new Recorder((_, _) => new { id = "l1", key = "abc" });
        using var client = Client(handler);

        var link = await client.CreateSharedLinkAsync(["a1", "a2"], null, allowDownload: false, expiresAt: null, CancellationToken.None);
        await client.CreateSharedLinkAsync(null, "album1", allowDownload: true, expiresAt: new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);

        Assert.Equal("abc", link.Key);
        var photos = JsonDocument.Parse(handler.Requests[0].Body).RootElement;
        Assert.Equal("INDIVIDUAL", photos.GetProperty("type").GetString());
        Assert.Equal(2, photos.GetProperty("assetIds").GetArrayLength());
        Assert.False(photos.GetProperty("allowDownload").GetBoolean());
        Assert.False(photos.TryGetProperty("expiresAt", out _));
        var album = JsonDocument.Parse(handler.Requests[1].Body).RootElement;
        Assert.Equal("ALBUM", album.GetProperty("type").GetString());
        Assert.Equal("album1", album.GetProperty("albumId").GetString());
        Assert.False(album.TryGetProperty("assetIds", out _));
        Assert.Equal("2026-11-01T00:00:00.000Z", album.GetProperty("expiresAt").GetString());
    }

    [Fact]
    public async Task People_are_paged_and_hidden_people_left_out()
    {
        var handler = new Recorder((request, _) => request.RequestUri!.Query.Contains("page=1")
            ? new { people = new object[] { new { id = "p1", name = "Alex", isHidden = false }, new { id = "p2", name = "", isHidden = true } }, hasNextPage = true }
            : new { people = new object[] { new { id = "p3", name = (string?)null, isHidden = false } }, hasNextPage = false });
        using var client = Client(handler);

        var people = await client.GetPeopleAsync(CancellationToken.None);

        Assert.Equal(["p1", "p3"], people.Select(p => p.Id));
        Assert.Equal("", people[1].Name);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Duplicates_are_listed_and_resolved_like_the_web_app()
    {
        var handler = new Recorder((request, _) => request.Method == HttpMethod.Get
            ? new object[]
            {
                new { duplicateId = "d1", assets = new object[]
                {
                    new { id = "a1", type = "IMAGE", originalFileName = "IMG_1.jpg", isFavorite = false, exifInfo = new { exifImageWidth = 4000, exifImageHeight = 3000, fileSizeInByte = 5_000_000L } },
                    new { id = "a2", type = "IMAGE", originalFileName = "IMG_1 (1).jpg", isFavorite = false, exifInfo = new { exifImageWidth = 1600, exifImageHeight = 1200, fileSizeInByte = 900_000L } },
                } },
                new { duplicateId = "d2", assets = new object[] { new { id = "lonely", type = "IMAGE", isFavorite = false } } },
            }
            : new { });
        using var client = Client(handler);

        var groups = await client.GetDuplicatesAsync(CancellationToken.None);
        await client.ResolveDuplicatesAsync(["a1"], ["a2"], CancellationToken.None);
        await client.ResolveDuplicatesAsync(["a1", "a2"], [], CancellationToken.None);

        var group = Assert.Single(groups); // a "group" of one isn't a duplicate
        Assert.Equal(("IMG_1.jpg", 4000, 5_000_000L), (group.Assets[0].FileName, group.Assets[0].Width, group.Assets[0].FileSize));
        Assert.Equal(("DELETE", "/api/assets"), (handler.Requests[1].Method, handler.Requests[1].PathAndQuery));
        Assert.Contains("\"force\":false", handler.Requests[1].Body); // to the trash, not deleted for good
        Assert.Equal("PUT", handler.Requests[2].Method);
        Assert.Contains("\"duplicateId\":null", handler.Requests[2].Body); // must be sent, or nothing changes
        Assert.Equal("PUT", handler.Requests[3].Method); // "not duplicates": nothing trashed
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Map_settings_become_the_web_apps_filters()
    {
        var handler = new Recorder((_, _) => Array.Empty<object>());
        using var client = Client(handler);
        var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

        await client.GetMapMarkersAsync(null, CancellationToken.None);
        await client.GetMapMarkersAsync(ImmichSplash.Core.Settings.MapSettings.Filter(new ImmichSplash.Core.Settings.AppSettings
        {
            MapOnlyFavorites = true, MapIncludeArchived = true, MapIncludePartners = true, MapIncludeSharedAlbums = true,
            MapDateRange = ImmichSplash.Core.Settings.MapDateRange.Week,
        }, now), CancellationToken.None);
        await client.GetMapMarkersAsync(ImmichSplash.Core.Settings.MapSettings.Filter(new ImmichSplash.Core.Settings.AppSettings
        {
            MapDateRange = ImmichSplash.Core.Settings.MapDateRange.Custom, MapDateFrom = new DateTime(2025, 6, 1), MapDateTo = new DateTime(2025, 6, 30),
        }, now), CancellationToken.None);

        Assert.Equal("/api/map/markers", handler.Requests[0].PathAndQuery); // nothing unasked-for (older servers)
        Assert.Equal("/api/map/markers?isFavorite=true&isArchived=true&withPartners=true&withSharedAlbums=true&fileCreatedAfter=2026-10-06T12%3A00%3A00.000Z",
            handler.Requests[1].PathAndQuery);
        Assert.Equal("/api/map/markers?fileCreatedAfter=2025-06-01T00%3A00%3A00.000Z&fileCreatedBefore=2025-06-30T23%3A59%3A59.999Z",
            handler.Requests[2].PathAndQuery);
    }

    [Fact]
    public async Task Screen_captures_are_found_by_name_folder_and_iphone_pngs()
    {
        var handler = new Recorder((_, body) =>
        {
            var b = JsonDocument.Parse(body).RootElement;
            var items = new List<object>();
            if (b.TryGetProperty("originalFileName", out var name) && name.GetString() == "Screenshot")
                items.Add(new { id = "shot", type = "IMAGE", isFavorite = false, fileCreatedAt = "2026-10-01T10:00:00.000Z" });
            if (b.TryGetProperty("originalFileName", out var rec) && rec.GetString() == "RPReplay")
                items.Add(new { id = "rec", type = "VIDEO", isFavorite = false, fileCreatedAt = "2026-09-01T10:00:00.000Z" });
            if (b.TryGetProperty("make", out var make))
                items.Add(new { id = "iphone", type = "IMAGE", isFavorite = false, fileCreatedAt = "2026-08-01T10:00:00.000Z" });
            return new { assets = new { items, nextPage = (string?)null } };
        });
        using var client = Client(handler);

        var found = await client.FindScreenCapturesAsync(CancellationToken.None);

        Assert.Equal([("shot", CaptureKind.Screenshot), ("rec", CaptureKind.ScreenRecording), ("iphone", CaptureKind.LikelyScreenshot)],
            found.Select(f => (f.Asset.Id, f.Kind)));
        Assert.All(handler.Requests, r => Assert.Contains("\"visibility\":\"timeline\"", r.Body)); // already-archived ones aren't offered again
        var withMake = handler.Requests.Where(r => r.Body.Contains("\"make\":null")).ToList();
        Assert.Single(withMake); // only the iPhone search asks for "no camera"
        Assert.Contains("\"originalFileName\":\".png\"", withMake[0].Body);
    }

    [Fact]
    public async Task Large_files_come_biggest_first()
    {
        var handler = new Recorder((_, _) => new object[]
        {
            new { id = "small", type = "IMAGE", isFavorite = false, exifInfo = new { fileSizeInByte = 20_000_000L } },
            new { id = "big", type = "VIDEO", isFavorite = false, duration = 72_000, exifInfo = new { fileSizeInByte = 900_000_000L } }, // newer servers: milliseconds as a number
        });
        using var client = Client(handler);

        var files = await client.GetLargeFilesAsync(10_000_000, 250, CancellationToken.None);

        Assert.Equal(["big", "small"], files.Select(f => f.Id));
        Assert.Equal(900_000_000L, files[0].FileSize);
        Assert.Equal(72_000, files[0].DurationMs);
        Assert.Equal(("POST", "/api/search/large-assets?minFileSize=10000000&size=250"), (handler.Requests[0].Method, handler.Requests[0].PathAndQuery));
    }

    [Fact]
    public async Task Memories_for_today_are_read_newest_year_first()
    {
        static object Asset(string id) => new { id, type = "IMAGE", originalFileName = id + ".jpg", fileCreatedAt = "2020-10-14T10:00:00Z", isFavorite = false, duration = "0:00:00.00000" };
        var handler = new Recorder((_, _) => new object[]
        {
            new { id = "m1", type = "on_this_day", data = new { year = 2019 }, assets = new[] { Asset("a"), Asset("b") } },
            new { id = "m2", type = "on_this_day", data = new { year = 2023 }, assets = new[] { Asset("c") } },
            new { id = "m3", type = "birthday", data = new { personName = "Sam" }, assets = new[] { Asset("d") } },
            new { id = "m4", type = "on_this_day", data = new { year = 2021 }, assets = Array.Empty<object>() }, // nothing to show
        });
        using var client = Client(handler);

        var memories = await client.GetMemoriesAsync(new DateOnly(2026, 10, 14), TestContext.Current.CancellationToken);

        Assert.Equal("/api/memories?for=2026-10-14", handler.Requests.Single().PathAndQuery);
        Assert.Equal(["m3", "m2", "m1"], memories.Select(m => m.Id));
        Assert.True(memories[0].IsBirthday);
        Assert.Equal("Sam", memories[0].PersonName);
        Assert.Equal(2, memories[2].Assets.Count);
    }

}

public class SharedAlbumTests
{
    [Fact]
    public void Only_albums_with_other_people_count_as_shared()
    {
        var json = """
        [
          {"id":"a1","albumName":"Just mine, has a public link","shared":true,"ownerId":"me","albumUsers":[]},
          {"id":"a2","albumName":"Family","shared":true,"ownerId":"me","albumUsers":[{"role":"editor","user":{"id":"sam"}}]},
          {"id":"a3","albumName":"Sam's trip","shared":true,"ownerId":"sam","albumUsers":[]},
          {"id":"a4","albumName":"Mine, owner listed as a member","shared":true,"ownerId":"me","albumUsers":[{"role":"owner","user":{"id":"me"}}]},
          {"id":"a5","albumName":"Mine, owner only as an object","shared":true,"owner":{"id":"me"},"albumUsers":[{"role":"editor","user":{"id":"me"}}]},
          {"id":"a6","albumName":"Immich v3: owner first, no ownerId, link only","shared":true,"hasSharedLink":true,"albumUsers":[{"role":"editor","user":{"id":"me"}}]},
          {"id":"a7","albumName":"Immich v3: shared with Sam","shared":true,"albumUsers":[{"role":"editor","user":{"id":"me"}},{"role":"viewer","user":{"id":"sam"}}]},
          {"id":"a8","albumName":"Immich v3: Sam's album","shared":true,"albumUsers":[{"role":"editor","user":{"id":"sam"}},{"role":"viewer","user":{"id":"me"}}]}
        ]
        """;
        var albums = System.Text.Json.JsonSerializer.Deserialize<List<ImmichSplash.Core.Api.AlbumInfo>>(json,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal(["a2", "a3", "a7", "a8"], albums.Where(a => a.IsSharedWithPeople("me")).Select(a => a.Id));
    }
}
