using System.Net;
using System.Net.Sockets;
using ImmichSplash.Core.Api;

namespace ImmichSplash.Core.Tests;

/// <summary>
/// Immich answers some image requests with a redirect (e.g. full size → the original file).
/// .NET drops the Authorization header when following a redirect, so a password/SSO session must
/// still be signed in on the redirected request.
/// </summary>
public class RedirectAuthTests
{
    [Fact]
    public async Task Signed_in_session_survives_Immich_image_redirects()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                var context = await listener.GetContextAsync();
                var path = context.Request.Url!.AbsolutePath;
                if (path.EndsWith("/thumbnail", StringComparison.Ordinal))
                {
                    // What Immich does when the full-size image is the original itself.
                    context.Response.StatusCode = 302;
                    context.Response.RedirectLocation = "/api/assets/a1/original";
                }
                else
                {
                    var signedIn = context.Request.Headers["Authorization"] == "Bearer token-1" ||
                                   context.Request.Headers["x-immich-user-token"] == "token-1";
                    context.Response.StatusCode = signedIn ? 200 : 401;
                    var body = signedIn ? [1, 2, 3] : "{\"message\":\"Authentication required\"}"u8.ToArray();
                    await context.Response.OutputStream.WriteAsync(body);
                }
                context.Response.Close();
            }
        }, TestContext.Current.CancellationToken);

        using var client = new ImmichClient(new Uri($"http://localhost:{port}/api/"), new AuthCredential(AuthKind.AccessToken, "token-1"));
        var bytes = await client.GetAssetImageAsync("a1", AssetImageSize.Fullsize, CancellationToken.None);

        Assert.Equal([1, 2, 3], bytes);
        await server;
    }

    static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }
}
