using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace ImmichDesktop.Core.Updates;

public sealed record ReleaseAsset(string Name, long Size, string DownloadUrl, string? Sha256);

public sealed record ReleaseInfo(ReleaseVersion Version, string Tag, string Title, string? Notes, string PageUrl, ReleaseAsset? Installer);

/// <summary>Finds new versions on GitHub Releases and downloads the installer, verifying its SHA-256 digest.</summary>
public sealed class GitHubUpdateChecker(string repository, HttpMessageHandler? handler = null) : IDisposable
{
    /// <summary>Installer asset name produced by build/installer.iss.</summary>
    public const string InstallerSuffix = "-Setup.exe";

    readonly HttpClient _http = CreateHttp(handler);

    static HttpClient CreateHttp(HttpMessageHandler? handler)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public static ReleaseVersion CurrentVersion => ReleaseVersion.Parse(AppInfo.Version) ?? new ReleaseVersion(new Version(0, 0, 0));

    /// <summary>Returns the latest stable release if it is newer than this build, otherwise null.</summary>
    public Task<ReleaseInfo?> CheckAsync(CancellationToken ct) => CheckAsync(includeBetas: false, ct);

    /// <summary>
    /// Returns the newest release if it is newer than this build, otherwise null. With
    /// <paramref name="includeBetas"/>, betas (GitHub pre-releases) count too. Never offers an older
    /// version, so leaving the beta channel waits for the next stable release.
    /// </summary>
    public async Task<ReleaseInfo?> CheckAsync(bool includeBetas, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        // "latest" is the newest stable release (GitHub leaves out drafts and pre-releases).
        var url = includeBetas
            ? $"https://api.github.com/repos/{repository}/releases?per_page=30"
            : $"https://api.github.com/repos/{repository}/releases/latest";
        using var response = await _http.GetAsync(url, cts.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new UpdateException("No published releases were found. (If the GitHub repository is private, installed apps can't see its releases.)");
        if (!response.IsSuccessStatusCode)
            throw new UpdateException($"GitHub returned an error ({(int)response.StatusCode}). Try again later.");

        ReleaseDto? release;
        ReleaseVersion? version;
        if (includeBetas)
        {
            var releases = await response.Content.ReadFromJsonAsync<List<ReleaseDto>>(cts.Token)
                           ?? throw new UpdateException("GitHub returned an unexpected response.");
            (release, version) = releases
                .Where(r => !r.Draft)
                .Select(r => (Release: r, Version: ReleaseVersion.Parse(r.TagName)))
                .Where(r => r.Version is not null)
                .OrderByDescending(r => r.Version)
                .FirstOrDefault();
            if (release is null)
                throw new UpdateException("No published releases were found. (If the GitHub repository is private, installed apps can't see its releases.)");
        }
        else
        {
            release = await response.Content.ReadFromJsonAsync<ReleaseDto>(cts.Token)
                      ?? throw new UpdateException("GitHub returned an unexpected response.");
            version = ReleaseVersion.Parse(release.TagName);
        }
        if (version is null || version <= CurrentVersion)
            return null;

        var installer = release.Assets?
            .Where(a => a.Name.EndsWith(InstallerSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(a => new ReleaseAsset(a.Name, a.Size, a.BrowserDownloadUrl,
                a.Digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? a.Digest[7..] : null))
            .FirstOrDefault();

        return new ReleaseInfo(version, release.TagName, string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name,
            release.Body, release.HtmlUrl, installer);
    }

    /// <summary>Downloads the installer to <paramref name="destination"/>; rejects it if the digest doesn't match.</summary>
    public async Task DownloadAsync(ReleaseAsset asset, string destination, Action<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + ".part";

        using (var response = await _http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? asset.Size;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(partial);
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                if (total > 0)
                    progress?.Invoke(received / (double)total);
            }
        }

        if (asset.Sha256 is { } expected)
        {
            string actual;
            await using (var stream = File.OpenRead(partial))
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new UpdateException("The downloaded update didn't pass its integrity check, so it was discarded.");
            }
        }

        File.Move(partial, destination, overwrite: true);
    }

    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var clean = text.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(clean, out var v) ? new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build)) : null;
    }

    public void Dispose() => _http.Dispose();

    sealed record ReleaseDto(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("assets")] List<AssetDto>? Assets,
        [property: JsonPropertyName("draft")] bool Draft = false,
        [property: JsonPropertyName("prerelease")] bool Prerelease = false);

    sealed record AssetDto(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
        [property: JsonPropertyName("digest")] string? Digest);
}

public sealed class UpdateException(string message) : Exception(message);
