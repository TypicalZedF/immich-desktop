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
    ];

    readonly Dictionary<string, byte[]> _images = [];
    byte[]? _avatar;

    /// <summary>Must be called on the UI thread (renders the demo images).</summary>
    public void Prepare()
    {
        for (var i = 0; i < 12; i++)
        {
            _images[$"t{i}"] = Render.ToPng(Scene(i, 250, 250), 250, 250);
            _images[$"p{i}"] = Render.ToPng(Scene(i, 1200, 800), 1200, 800);
        }
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
        HttpResponseMessage response = path switch
        {
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
