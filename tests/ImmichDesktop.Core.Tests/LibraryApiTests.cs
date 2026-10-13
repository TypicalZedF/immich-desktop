using System.Net;
using System.Text;
using System.Text.Json;
using ImmichDesktop.Core.Api;

namespace ImmichDesktop.Core.Tests;

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
    public async Task Album_and_person_timelines_use_the_timeline_filters()
    {
        var handler = new Recorder((_, _) => new[] { new { timeBucket = "2026-10-01T00:00:00.000Z", count = 3 } });
        using var client = Client(handler);

        await client.GetTimeBucketsAsync(new TimelineFilter(AlbumId: "album 1"), CancellationToken.None);
        await client.GetTimeBucketsAsync(new TimelineFilter(PersonId: "p1"), CancellationToken.None);
        await client.GetTimeBucketsAsync(new TimelineFilter(Favorites: true), CancellationToken.None);

        // Albums include archived photos (no visibility filter), like the web app.
        Assert.Equal("/api/timeline/buckets?order=desc&albumId=album%201", handler.Requests[0].PathAndQuery);
        Assert.Equal("/api/timeline/buckets?visibility=timeline&order=desc&personId=p1", handler.Requests[1].PathAndQuery);
        Assert.Equal("/api/timeline/buckets?visibility=timeline&order=desc&isFavorite=true", handler.Requests[2].PathAndQuery);
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
}
