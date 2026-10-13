using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ImmichDesktop.Core.Api;

/// <summary>Typed client for the subset of the Immich REST API this app needs.</summary>
public sealed class ImmichClient : IDisposable
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    readonly HttpClient _http;

    public ImmichClient(Uri apiUrl, AuthCredential? credential, HttpMessageHandler? handler = null)
    {
        ApiUrl = WithTrailingSlash(apiUrl);
        Credential = credential;
        _http = CreateHttpClient(handler);
        _http.BaseAddress = ApiUrl;

        if (credential is { Kind: AuthKind.AccessToken })
            _http.DefaultRequestHeaders.Authorization = new("Bearer", credential.Secret);
        else if (credential is { Kind: AuthKind.ApiKey })
            _http.DefaultRequestHeaders.Add("x-api-key", credential.Secret);
    }

    public Uri ApiUrl { get; }
    public AuthCredential? Credential { get; }

    /// <summary>Filled in on first contact; used to stay compatible with older servers.</summary>
    public ServerVersion? ServerVersion { get; set; }

    /// <summary>Identifies this computer to servers older than v2, which require a device id on upload.</summary>
    public string DeviceId { get; set; } = Environment.MachineName;

    // ---- Discovery -------------------------------------------------------------------------

    /// <summary>
    /// Turns whatever the user typed ("photos.example.com", "http://192.168.1.5:2283", ".../api") into a verified server.
    /// </summary>
    public static async Task<ServerInfo> DiscoverAsync(string input, CancellationToken ct, HttpMessageHandler? handler = null)
    {
        input = input.Trim();
        if (input.Length == 0)
            throw new ServerDiscoveryException("Enter the address of your Immich server.");

        var candidates = input.Contains("://", StringComparison.Ordinal)
            ? new[] { input }
            : new[] { "https://" + input, "http://" + input };

        using var http = CreateHttpClient(handler);
        Exception? last = null;
        foreach (var candidate in candidates)
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
            {
                last = new ServerDiscoveryException("That doesn't look like a valid server address.");
                continue;
            }

            try
            {
                return await ProbeAsync(http, uri, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw last as ServerDiscoveryException ?? new ServerDiscoveryException(NetworkErrors.Describe(last!), last);
    }

    static async Task<ServerInfo> ProbeAsync(HttpClient http, Uri uri, CancellationToken ct)
    {
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];
        var web = new UriBuilder(uri.Scheme, uri.Host, uri.Port, path + "/").Uri;
        var api = new Uri(web, "api/");

        // Servers can advertise a custom API location.
        try
        {
            var wellKnown = await GetJsonAsync<WellKnownDto>(http, new Uri(web, ".well-known/immich"), TimeSpan.FromSeconds(10), ct);
            if (wellKnown?.Api?.Endpoint is { Length: > 0 } endpoint)
                api = WithTrailingSlash(new Uri(web, endpoint));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Optional endpoint; fall back to /api.
        }

        PingResponseDto? ping;
        try
        {
            ping = await GetJsonAsync<PingResponseDto>(http, new Uri(api, "server/ping"), TimeSpan.FromSeconds(15), ct);
        }
        catch (Exception ex) when (ex is JsonException or ImmichApiException { StatusCode: HttpStatusCode.NotFound })
        {
            throw new ServerDiscoveryException("This doesn't look like an Immich server.", ex);
        }

        if (!string.Equals(ping?.Res, "pong", StringComparison.OrdinalIgnoreCase))
            throw new ServerDiscoveryException("This doesn't look like an Immich server.");

        var version = await GetJsonAsync<VersionResponseDto>(http, new Uri(api, "server/version"), TimeSpan.FromSeconds(15), ct);
        return new ServerInfo(web, api, new ServerVersion(version!.Major, version.Minor, version.Patch));
    }

    // ---- Auth & account ------------------------------------------------------------------------

    public async Task<string> LoginAsync(string email, string password, CancellationToken ct)
    {
        var response = await SendAsync<LoginResponseDto>(HttpMethod.Post, "auth/login", new LoginRequestDto(email.Trim(), password), ct);
        return response.AccessToken;
    }

    public Task LogoutAsync(CancellationToken ct) => SendAsync(HttpMethod.Post, "auth/logout", null, ct);

    public async Task<UserInfo> GetMeAsync(CancellationToken ct)
    {
        var me = await SendAsync<UserResponseDto>(HttpMethod.Get, "users/me", null, ct);
        return new UserInfo(me.Id, me.Name, me.Email);
    }

    // ---- Server ------------------------------------------------------------------------------

    public async Task PingAsync(CancellationToken ct)
    {
        var ping = await SendAsync<PingResponseDto>(HttpMethod.Get, "server/ping", null, ct, TimeSpan.FromSeconds(15));
        if (!string.Equals(ping.Res, "pong", StringComparison.OrdinalIgnoreCase))
            throw new ImmichApiException(HttpStatusCode.ServiceUnavailable, "The server isn't responding normally.");
    }

    public async Task<ServerVersion> GetVersionAsync(CancellationToken ct)
    {
        var v = await SendAsync<VersionResponseDto>(HttpMethod.Get, "server/version", null, ct);
        return new ServerVersion(v.Major, v.Minor, v.Patch);
    }

    public Task<MediaTypesInfo> GetMediaTypesAsync(CancellationToken ct) =>
        SendAsync<MediaTypesInfo>(HttpMethod.Get, "server/media-types", null, ct);

    // ---- Assets ------------------------------------------------------------------------------

    public async Task<List<BulkCheckResult>> BulkUploadCheckAsync(IReadOnlyList<ChecksumItem> items, CancellationToken ct)
    {
        var response = await SendAsync<BulkCheckResponseDto>(HttpMethod.Post, "assets/bulk-upload-check", new BulkCheckRequestDto(items), ct, TimeSpan.FromSeconds(60));
        return response.Results;
    }

    public async Task<UploadResult> UploadAsync(UploadRequest request, BandwidthLimiter limiter, Action<long>? progress, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        if (ServerVersion is { Major: < 2 })
        {
            // Required by servers older than v2.
            form.Add(new StringContent($"{request.FileName}-{request.Size}"), "deviceAssetId");
            form.Add(new StringContent(DeviceId), "deviceId");
        }
        form.Add(new StringContent(IsoDate(request.CreatedAtUtc)), "fileCreatedAt");
        form.Add(new StringContent(IsoDate(request.ModifiedAtUtc)), "fileModifiedAt");
        form.Add(new StringContent(request.FileName), "filename");
        form.Add(new StringContent("false"), "isFavorite");
        form.Add(new FileUploadContent(request.FilePath, request.Size, limiter, progress), "assetData", request.FileName);

        using var message = new HttpRequestMessage(HttpMethod.Post, "assets") { Content = form };
        message.Headers.Add("x-immich-checksum", request.Checksum);

        // Generous timeout that scales with size, so a stalled connection doesn't hang forever.
        var timeout = TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(request.Size / (32 * 1024.0));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            await EnsureSuccessAsync(response, cts.Token);
            return (await response.Content.ReadFromJsonAsync<UploadResult>(Json, cts.Token))!;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The upload timed out.");
        }
    }

    // ---- Albums ------------------------------------------------------------------------------

    public Task<List<AlbumInfo>> GetAlbumsAsync(CancellationToken ct) =>
        SendAsync<List<AlbumInfo>>(HttpMethod.Get, "albums", null, ct);

    public Task<AlbumInfo> CreateAlbumAsync(string name, IReadOnlyList<string>? assetIds, CancellationToken ct) =>
        SendAsync<AlbumInfo>(HttpMethod.Post, "albums", new CreateAlbumRequestDto(name, assetIds), ct);

    public Task AddAssetsToAlbumAsync(string albumId, IReadOnlyList<string> assetIds, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"albums/{Uri.EscapeDataString(albumId)}/assets", new BulkIdsRequestDto(assetIds), ct);

    // ---- Plumbing ----------------------------------------------------------------------------

    internal static HttpClient CreateHttpClient(HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.All,
        };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
        return http;
    }

    async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? DefaultTimeout);
        try
        {
            using var request = CreateRequest(method, path, body);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            await EnsureSuccessAsync(response, cts.Token);
            return (await response.Content.ReadFromJsonAsync<T>(Json, cts.Token))!;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The server took too long to respond.");
        }
    }

    async Task SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DefaultTimeout);
        try
        {
            using var request = CreateRequest(method, path, body);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            await EnsureSuccessAsync(response, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The server took too long to respond.");
        }
    }

    static HttpRequestMessage CreateRequest(HttpMethod method, string path, object? body)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        return request;
    }

    static async Task<T?> GetJsonAsync<T>(HttpClient http, Uri url, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            await EnsureSuccessAsync(response, cts.Token);
            return await response.Content.ReadFromJsonAsync<T>(Json, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The server took too long to respond.");
        }
    }

    static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        string? detail = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            detail = ParseErrorMessage(body);
        }
        catch
        {
            // Body is optional.
        }

        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => detail ?? "Your session has expired. Please sign in again.",
            HttpStatusCode.Forbidden => detail ?? "You don't have permission to do that.",
            HttpStatusCode.NotFound => detail ?? "The server doesn't support this request.",
            HttpStatusCode.RequestEntityTooLarge => "The file is too large for the server (check your reverse proxy upload limit).",
            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout => "The server is temporarily unavailable.",
            _ => detail ?? $"The server returned an error ({(int)response.StatusCode}).",
        };
        throw new ImmichApiException(response.StatusCode, message);
    }

    internal static string? ParseErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("message", out var message))
                return null;

            var text = message.ValueKind switch
            {
                JsonValueKind.String => message.GetString(),
                JsonValueKind.Array => string.Join("; ", message.EnumerateArray().Select(m => m.ToString())),
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(text))
                return null;
            return char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string IsoDate(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    static Uri WithTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");

    public void Dispose() => _http.Dispose();
}
