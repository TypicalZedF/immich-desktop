using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ImmichDesktop.Core.Platform;
using ImmichDesktop.Core.Services;
using ImmichDesktop.Core.Updates;

namespace ImmichDesktop.Core.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Verifies_only_the_right_password_and_never_stores_it()
    {
        var hash = PasswordHasher.Hash("correct horse");

        Assert.DoesNotContain("correct horse", hash);
        Assert.True(PasswordHasher.Verify("correct horse", hash));
        Assert.False(PasswordHasher.Verify("Correct horse", hash));
        Assert.False(PasswordHasher.Verify("", hash));
        Assert.False(PasswordHasher.Verify("correct horse", null));
        Assert.False(PasswordHasher.Verify("correct horse", "garbage"));
    }

    [Fact]
    public void Same_password_gets_a_different_salt_each_time() =>
        Assert.NotEqual(PasswordHasher.Hash("pw"), PasswordHasher.Hash("pw"));
}

public class OAuthSessionTests
{
    [Fact]
    public void Uses_pkce_s256_challenge()
    {
        var session = OAuthSession.Create();
        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(session.CodeVerifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, session.CodeChallenge);
        Assert.True(session.CodeVerifier.Length >= 43);
    }

    [Fact]
    public void Accepts_only_callbacks_with_its_own_state()
    {
        var session = OAuthSession.Create();
        Assert.True(session.Matches($"app.immich:///oauth-callback?code=abc&state={Uri.EscapeDataString(session.State)}"));
        Assert.False(session.Matches("app.immich:///oauth-callback?code=abc&state=forged"));
        Assert.False(session.Matches("app.immich:///oauth-callback?code=abc"));
        Assert.True(OAuthSession.IsCallback("APP.IMMICH:///oauth-callback?x=1"));
        Assert.False(OAuthSession.IsCallback("https://evil.example/app.immich"));
    }
}

public class UpdateCheckerTests
{
    sealed class FakeGitHub(string tag, byte[] installer, string? digest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                var body = JsonSerializer.Serialize(new
                {
                    tag_name = tag,
                    name = $"Immich Desktop {tag}",
                    body = "notes",
                    html_url = "https://github.com/x/y/releases/tag/" + tag,
                    assets = new[]
                    {
                        new { name = "ImmichDesktop-Portable.zip", size = 1L, browser_download_url = "https://dl/zip", digest = (string?)null },
                        new { name = "ImmichDesktop-Setup.exe", size = (long)installer.Length, browser_download_url = "https://dl/setup", digest },
                    },
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(installer) });
        }
    }

    [Fact]
    public async Task Finds_a_newer_release_and_its_installer()
    {
        using var checker = new GitHubUpdateChecker("x/y", new FakeGitHub("v99.0.0", [1, 2, 3], null));
        var release = await checker.CheckAsync(CancellationToken.None);

        Assert.NotNull(release);
        Assert.Equal(new Version(99, 0, 0), release.Version);
        Assert.Equal("ImmichDesktop-Setup.exe", release.Installer?.Name);
    }

    [Fact]
    public async Task Ignores_releases_that_are_not_newer()
    {
        using var checker = new GitHubUpdateChecker("x/y", new FakeGitHub("v0.0.1", [1], null));
        Assert.Null(await checker.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_downloads_with_the_wrong_checksum()
    {
        using var temp = new TempDir();
        var data = new byte[] { 1, 2, 3, 4 };
        var good = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(data));

        using (var checker = new GitHubUpdateChecker("x/y", new FakeGitHub("v99.0.0", data, good)))
        {
            var release = (await checker.CheckAsync(CancellationToken.None))!;
            var path = Path.Combine(temp.Path, "ok.exe");
            await checker.DownloadAsync(release.Installer!, path, null, CancellationToken.None);
            Assert.Equal(data, File.ReadAllBytes(path));
        }

        using (var checker = new GitHubUpdateChecker("x/y", new FakeGitHub("v99.0.0", data, "sha256:" + new string('0', 64))))
        {
            var release = (await checker.CheckAsync(CancellationToken.None))!;
            var path = Path.Combine(temp.Path, "bad.exe");
            await Assert.ThrowsAsync<UpdateException>(() => checker.DownloadAsync(release.Installer!, path, null, CancellationToken.None));
            Assert.False(File.Exists(path));
        }
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("v2.0.0-beta.1", "2.0.0")]
    public void Parses_tags(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), GitHubUpdateChecker.ParseVersion(tag));
}
