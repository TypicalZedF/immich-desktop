using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ImmichDesktop.Core.Tests;

static class TestSetup
{
    [ModuleInitializer]
    internal static void Init() =>
        Environment.SetEnvironmentVariable("IMMICH_DESKTOP_DATA_DIR", Path.Combine(Path.GetTempPath(), "immich-desktop-tests", "appdata"));
}

/// <summary>In-memory stand-in for the Immich API endpoints the app uses.</summary>
sealed class FakeImmichServer : HttpMessageHandler
{
    public ConcurrentDictionary<string, string> AssetsByChecksum { get; } = new();
    public ConcurrentQueue<string> UploadedFileNames { get; } = new();
    public ConcurrentDictionary<string, (string Name, HashSet<string> Assets)> Albums { get; } = new();
    public bool RejectAuth { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var method = request.Method.Method;

        if (RejectAuth && path != "/api/server/ping")
            return Json(HttpStatusCode.Unauthorized, new { message = "Invalid user token" });

        switch (method, path)
        {
            case ("GET", "/api/server/ping"):
                return Json(HttpStatusCode.OK, new { res = "pong" });
            case ("GET", "/api/server/version"):
                return Json(HttpStatusCode.OK, new { major = 3, minor = 3, patch = 0 });
            case ("GET", "/api/server/media-types"):
                return Json(HttpStatusCode.OK, new { image = new[] { "image/jpeg" }, video = new[] { "video/mp4" }, sidecar = Array.Empty<string>() });
            case ("POST", "/api/assets/bulk-upload-check"):
            {
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var results = doc.RootElement.GetProperty("assets").EnumerateArray().Select(a =>
                {
                    var id = a.GetProperty("id").GetString()!;
                    var checksum = a.GetProperty("checksum").GetString()!;
                    return AssetsByChecksum.TryGetValue(checksum, out var existing)
                        ? (object)new { id, action = "reject", reason = "duplicate", assetId = existing }
                        : new { id, action = "accept" };
                }).ToList();
                return Json(HttpStatusCode.OK, new { results });
            }
            case ("POST", "/api/assets"):
            {
                var form = (MultipartFormDataContent)request.Content!;
                string? fileName = null;
                byte[]? data = null;
                foreach (var part in form)
                {
                    var name = part.Headers.ContentDisposition?.Name?.Trim('"');
                    if (name == "filename") fileName = await part.ReadAsStringAsync(ct);
                    if (name == "assetData") data = await part.ReadAsByteArrayAsync(ct);
                }
                var checksum = Convert.ToHexStringLower(SHA1.HashData(data!));
                Assert.Equal(checksum, request.Headers.GetValues("x-immich-checksum").Single());

                if (AssetsByChecksum.TryGetValue(checksum, out var existing))
                    return Json(HttpStatusCode.OK, new { id = existing, status = "duplicate" });

                var id = Guid.NewGuid().ToString();
                AssetsByChecksum[checksum] = id;
                UploadedFileNames.Enqueue(fileName!);
                return Json(HttpStatusCode.Created, new { id, status = "created" });
            }
            case ("GET", "/api/albums"):
                return Json(HttpStatusCode.OK, Albums.Select(a => new { id = a.Key, albumName = a.Value.Name }).ToList());
            case ("POST", "/api/albums"):
            {
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var id = Guid.NewGuid().ToString();
                var name = doc.RootElement.GetProperty("albumName").GetString()!;
                Albums[id] = (name, []);
                return Json(HttpStatusCode.Created, new { id, albumName = name });
            }
            case ("PUT", _) when path.StartsWith("/api/albums/", StringComparison.Ordinal) && path.EndsWith("/assets", StringComparison.Ordinal):
            {
                var albumId = path.Split('/')[3];
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                foreach (var id in doc.RootElement.GetProperty("ids").EnumerateArray())
                    Albums[albumId].Assets.Add(id.GetString()!);
                return Json(HttpStatusCode.OK, Array.Empty<object>());
            }
            default:
                return Json(HttpStatusCode.NotFound, new { message = $"No route for {method} {path}" });
        }
    }

    public void Preload(string filePath) =>
        AssetsByChecksum[Convert.ToHexStringLower(SHA1.HashData(File.ReadAllBytes(filePath)))] = Guid.NewGuid().ToString();

    static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
