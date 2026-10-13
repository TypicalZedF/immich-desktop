using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace ImmichDesktop.DevTools;

/// <summary>
/// A pretend Immich server for screenshots: a small timeline of generated "photos", a profile picture
/// and storage info. Lets the real Photos page, viewer and avatar code run without a real server.
/// </summary>
sealed class DemoServer : HttpMessageHandler
{
    static readonly (string Bucket, int Count)[] Buckets =
    [
        ("2026-10-01T00:00:00.000Z", 23),
        ("2026-09-01T00:00:00.000Z", 41),
        ("2026-08-01T00:00:00.000Z", 17),
        ("2026-06-01T00:00:00.000Z", 29),
        ("2026-03-01T00:00:00.000Z", 12),
        ("2025-12-01T00:00:00.000Z", 36),
        ("2025-08-01T00:00:00.000Z", 52),
        ("2025-07-01T00:00:00.000Z", 18),
        ("2025-04-01T00:00:00.000Z", 9),
        ("2024-12-01T00:00:00.000Z", 27),
        ("2024-07-01T00:00:00.000Z", 44),
        ("2024-02-01T00:00:00.000Z", 14),
        ("2023-09-01T00:00:00.000Z", 31),
        ("2023-05-01T00:00:00.000Z", 22),
    ];

    readonly Dictionary<string, byte[]> _images = [];
    byte[]? _avatar;
    readonly List<byte[]> _faces = [];
    byte[]? _tileLight, _tileDark;

    static readonly (string Name, string Color)[] People =
    [
        ("Alex", "#F59E0B"), ("Sam", "#10B981"), ("Jordan", "#6366F1"), ("Riley", "#EC4899"), ("Casey", "#0EA5E9"), ("", "#94A3B8"),
    ];

    static readonly (string Id, string Name, int Count, bool Shared)[] Albums =
    [
        ("a1", "Summer in Lisbon", 48, false), ("a2", "Family", 212, true), ("a3", "Hiking", 37, false), ("a4", "Garden", 15, false),
    ];

    static readonly (string City, string State, string Country, double Lat, double Lon, int Count)[] Cities =
    [
        ("Lisbon", "Lisbon", "Portugal", 38.72, -9.14, 40), ("Porto", "Porto", "Portugal", 41.15, -8.61, 12), ("Madrid", "Madrid", "Spain", 40.42, -3.70, 18),
        ("Paris", "Île-de-France", "France", 48.86, 2.35, 25), ("Amsterdam", "North Holland", "Netherlands", 52.37, 4.90, 9), ("Barcelona", "Catalonia", "Spain", 41.39, 2.17, 22),
        ("New York", "New York", "United States", 40.71, -74.0, 120), ("Chicago", "Illinois", "United States", 41.88, -87.63, 60),
        ("Austin", "Texas", "United States", 30.27, -97.74, 140), ("Denver", "Colorado", "United States", 39.74, -104.99, 45),
        ("Seattle", "Washington", "United States", 47.61, -122.33, 50), ("Los Angeles", "California", "United States", 34.05, -118.24, 80),
    ];

    /// <summary>Must be called on the UI thread (renders the demo images).</summary>
    public void Prepare()
    {
        for (var i = 0; i < 12; i++)
        {
            _images[$"t{i}"] = Render.ToPng(Scene(i, 250, 250), 250, 250);
            _images[$"p{i}"] = Render.ToPng(Scene(i, 1200, 800), 1200, 800);
        }
        foreach (var (name, color) in People)
        {
            _faces.Add(Render.ToPng(new Border
            {
                Width = 160,
                Height = 160,
                Background = new SolidColorBrush(Color.Parse(color)),
                Child = new TextBlock
                {
                    Text = name.Length > 0 ? name[..1] : "?",
                    FontSize = 72,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                },
            }, 160, 160));
        }
        _tileLight = Render.ToPng(Tile("#ECEEF1", "#DDE3EA", "#FFFFFF"), 512, 512);
        _tileDark = Render.ToPng(Tile("#1B1F24", "#232A33", "#2D333B"), 512, 512);
        _avatar = Render.ToPng(new Border
        {
            Width = 160,
            Height = 160,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#F59E0B"), 0), new GradientStop(Color.Parse("#DB2777"), 1) },
            },
            Child = new Ellipse { Width = 70, Height = 70, Fill = new SolidColorBrush(Color.Parse("#FDE68A")), Margin = new Thickness(0, 0, 0, 30) },
        }, 160, 160);
    }

    /// <summary>A plain map tile: land, a pond and a few streets (real tiles come from the internet).</summary>
    static Control Tile(string land, string water, string road) => new Canvas
    {
        Width = 512,
        Height = 512,
        Background = new SolidColorBrush(Color.Parse(land)),
        Children =
        {
            new Ellipse { Width = 220, Height = 150, Fill = new SolidColorBrush(Color.Parse(water)), [Canvas.LeftProperty] = 250.0, [Canvas.TopProperty] = 300.0 },
            new Line { StartPoint = new Point(0, 120), EndPoint = new Point(512, 180), Stroke = new SolidColorBrush(Color.Parse(road)), StrokeThickness = 10 },
            new Line { StartPoint = new Point(160, 0), EndPoint = new Point(120, 512), Stroke = new SolidColorBrush(Color.Parse(road)), StrokeThickness = 6 },
            new Line { StartPoint = new Point(0, 420), EndPoint = new Point(512, 380), Stroke = new SolidColorBrush(Color.Parse(road)), StrokeThickness = 4 },
        },
    };

    static object AssetJson(string id, int i, string? city = null) => new
    {
        id,
        type = i % 7 == 3 ? "VIDEO" : "IMAGE",
        originalFileName = $"IMG_{4400 + i}.HEIC",
        fileCreatedAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(-i * 3).ToString("o"),
        isFavorite = i % 9 == 2,
        duration = i % 7 == 3 ? "0:00:21.000" : "0:00:00.00000",
        exifInfo = new
        {
            make = "Google", model = "Pixel 9", lensModel = "Main camera", fNumber = 1.7, exposureTime = "1/320", iso = 50.0, focalLength = 6.9,
            city = city ?? "Lisbon", state = "Lisbon", country = "Portugal", exifImageWidth = 4080, exifImageHeight = 3072, fileSizeInByte = 2_480_000L,
            description = (string?)null,
        },
        people = new[] { new { name = "Alex" }, new { name = "Sam" } },
    };

    static HttpResponseMessage Search(int count) =>
        Json(new { assets = new { total = count, count, items = Enumerable.Range(0, count).Select(i => AssetJson($"202609-{i % 41}", i)), nextPage = (string?)null } });

    /// <summary>A simple landscape: sky gradient, sun and hills, with a different palette per index.</summary>
    static Control Scene(int i, int w, int h)
    {
        string[][] palettes =
        [
            ["#FDBA74", "#F472B6", "#7C3AED", "#312E81"], ["#BAE6FD", "#38BDF8", "#15803D", "#14532D"],
            ["#FDE68A", "#FB923C", "#B45309", "#78350F"], ["#C7D2FE", "#818CF8", "#334155", "#0F172A"],
            ["#A7F3D0", "#34D399", "#0F766E", "#134E4A"], ["#FBCFE8", "#F9A8D4", "#9D174D", "#500724"],
        ];
        var p = palettes[i % palettes.Length];
        var flip = i % 2 == 1;
        return new Canvas
        {
            Width = w,
            Height = h,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse(p[0]), 0), new GradientStop(Color.Parse(p[1]), 1) },
            },
            Children =
            {
                new Ellipse { Width = h * 0.28, Height = h * 0.28, Fill = Brushes.White, Opacity = 0.85, [Canvas.LeftProperty] = flip ? w * 0.18 : w * 0.62, [Canvas.TopProperty] = h * 0.18 },
                new Polygon { Fill = new SolidColorBrush(Color.Parse(p[2])), Points = [new(0, h), new(0, h * 0.62), new(w * 0.35, h * 0.42), new(w * 0.7, h * 0.66), new(w, h * 0.5), new(w, h)] },
                new Polygon { Fill = new SolidColorBrush(Color.Parse(p[3])), Points = [new(0, h), new(0, h * 0.8), new(w * 0.5, h * 0.7), new(w, h * 0.84), new(w, h)] },
            },
        };
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = request.RequestUri.Query;
        if (request.RequestUri.Host.EndsWith("cartocdn.com", StringComparison.Ordinal))
            return Task.FromResult(Bytes(path.Contains("dark", StringComparison.Ordinal) ? _tileDark! : _tileLight!));
        HttpResponseMessage response = path switch
        {
            "/api/albums" => Json(Albums.Select((a, i) => new
            {
                id = a.Id, albumName = a.Name, albumThumbnailAssetId = $"202609-{i * 5 + 1}", assetCount = a.Count, shared = true, ownerId = "u1",
                albumUsers = a.Shared ? new object[] { new { role = "editor", user = new { id = "u2", name = "Sam" } } } : Array.Empty<object>(),
            })),
            "/api/people" => Json(new { people = People.Select((p, i) => new { id = $"p{i}", name = p.Name, isHidden = false }), hasNextPage = false }),
            _ when path.StartsWith("/api/people/", StringComparison.Ordinal) => Bytes(_faces[int.Parse(path.Split('/')[3][1..]) % _faces.Count]),
            "/api/search/cities" => Json(Cities.Select((c, i) => new { id = $"202608-{i * 2}", exifInfo = new { city = c.City, state = c.State, country = c.Country } })),
            "/api/map/markers" => Json(Cities.SelectMany((c, ci) => Enumerable.Range(0, c.Count).Select(i => new
            {
                id = $"202609-{(ci * 7 + i) % 41}",
                lat = c.Lat + Math.Sin(i * 1.7) * 0.05 * (1 + i % 3),
                lon = c.Lon + Math.Cos(i * 2.3) * 0.07 * (1 + i % 3),
                city = c.City,
                country = c.Country,
            }))),
            "/api/search/smart" or "/api/search/metadata" => Search(17),
            _ when path.StartsWith("/api/shared-links/", StringComparison.Ordinal) && request.Method == HttpMethod.Delete => DeleteLink(path),
            "/api/shared-links" when request.Method == HttpMethod.Post => Json(new { id = "l1", key = "Xq7cT2pLm9vR4sWz" }),
            "/api/shared-links" => Json(new object[]
            {
                new { id = "l1", key = "Xq7cT2pLm9vR4sWz", type = "ALBUM", description = (string?)null, album = new { albumName = "Summer in Lisbon", albumThumbnailAssetId = "202609-1", assetCount = 48 },
                      assets = Array.Empty<object>(), createdAt = DateTimeOffset.Now.AddDays(-3), expiresAt = (DateTimeOffset?)null, allowDownload = true },
                new { id = "l2", key = "Kd82nQpZ1sYb7Lmv", type = "INDIVIDUAL", description = (string?)null, album = (object?)null,
                      assets = new[] { new { id = "202609-4" }, new { id = "202609-5" }, new { id = "202609-6" } }, createdAt = DateTimeOffset.Now.AddDays(-20),
                      expiresAt = (DateTimeOffset?)DateTimeOffset.Now.AddDays(10), allowDownload = false },
            }),
            "/api/duplicates" => Json(Enumerable.Range(0, 3).Select(g => new
            {
                duplicateId = $"d{g}",
                assets = Enumerable.Range(0, 2 + g % 2).Select(k => new
                {
                    id = $"202609-{g * 5 + k}",
                    type = "IMAGE",
                    originalFileName = k == 0 ? $"IMG_{4410 + g}.HEIC" : $"IMG_{4410 + g} ({k}).jpg",
                    fileCreatedAt = new DateTime(2026, 9, 20 - g, 15, 0, 0, DateTimeKind.Utc).ToString("o"),
                    isFavorite = false,
                    exifInfo = new { exifImageWidth = k == 0 ? 4080 : 1920, exifImageHeight = k == 0 ? 3072 : 1440, fileSizeInByte = k == 0 ? 3_800_000L : 640_000L / k },
                }),
            })),
            "/api/search/large-assets" => Json(Enumerable.Range(0, 14).Select(i => new
            {
                id = $"202608-{i}",
                type = i % 3 == 0 ? "VIDEO" : "IMAGE",
                isFavorite = false,
                fileCreatedAt = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc).AddDays(-i).ToString("o"),
                duration = i % 3 == 0 ? "0:01:12.000" : "0:00:00.00000",
                exifInfo = new { fileSizeInByte = 1_900_000_000L / (i + 1) },
            })),
            "/api/partners" => Json(new[] { new { id = "pt1", name = "Sam Morgan", email = "sam@example.com" } }),
            "/api/auth/status" => Json(new { pinCode = true, isElevated = false, password = true }),
            "/api/server/config" => Json(new { externalDomain = "" }),
            _ when path.StartsWith("/api/assets/", StringComparison.Ordinal) && path.Count(c => c == '/') == 3 => Json(AssetJson(path.Split('/')[3], 4)),
            "/api/server/ping" => Json(new { res = "pong" }),
            "/api/server/version" => Json(new { major = 3, minor = 3, patch = 1 }),
            "/api/users/me" => Json(new { id = "u1", name = "Alex Morgan", email = "alex@example.com", profileImagePath = "upload/profile/u1.jpg", avatarColor = "pink", quotaSizeInBytes = (long?)null, quotaUsageInBytes = (long?)null }),
            "/api/users/u1/profile-image" => Bytes(_avatar!),
            "/api/server/storage" => Json(new { diskSizeRaw = 1_980_000_000_000L, diskUseRaw = 742_000_000_000L }),
            "/api/timeline/buckets" => Json(Buckets.Select(b => new { timeBucket = b.Bucket, count = b.Count })),
            "/api/timeline/bucket" => BucketResponse(Uri.UnescapeDataString(query.Split("timeBucket=")[1].Split('&')[0])),
            _ when path.EndsWith("/thumbnail", StringComparison.Ordinal) => Thumbnail(path, query),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        return Task.FromResult(response);
    }

    /// <summary>Shared links deleted through the app (for checking the delete flow).</summary>
    public List<string> Deleted { get; } = [];

    HttpResponseMessage DeleteLink(string path)
    {
        Deleted.Add(path.Split('/')[3]);
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    static HttpResponseMessage BucketResponse(string bucket)
    {
        var (key, count) = Buckets.First(b => b.Bucket == bucket);
        var month = DateTime.Parse(key, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();
        var ids = Enumerable.Range(0, count).Select(i => $"{month:yyyyMM}-{i}").ToList();
        return Json(new
        {
            id = ids,
            isImage = ids.Select((_, i) => i % 7 != 3).ToList(),
            isFavorite = ids.Select((_, i) => i % 9 == 2).ToList(),
            ratio = ids.Select(_ => 1.5).ToList(),
            duration = ids.Select((_, i) => i % 7 == 3 ? (long?)(12_000 + i * 3_100) : null).ToList(),
            fileCreatedAt = ids.Select((_, i) => month.AddDays(27 - i % 27).AddHours(-i).ToString("o")).ToList(),
        });
    }

    HttpResponseMessage Thumbnail(string path, string query)
    {
        var id = path.Split('/')[3];
        var index = Math.Abs(id.GetHashCode()) % 12;
        return Bytes(_images[(query.Contains("preview") ? "p" : "t") + index]);
    }

    static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") };

    static HttpResponseMessage Bytes(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
}
