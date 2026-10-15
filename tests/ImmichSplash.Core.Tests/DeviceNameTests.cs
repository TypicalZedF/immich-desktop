using System.Net;
using ImmichSplash.Core.Api;

namespace ImmichSplash.Core.Tests;

public class DeviceNameTests
{
    sealed class Recorder : HttpMessageHandler
    {
        public HttpRequestMessage? Last;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"res\":\"pong\"}") });
        }
    }

    [Fact]
    public async Task Immich_is_told_which_app_and_system_this_is()
    {
        // Without these Immich lists the sign-in as an unknown device (it doesn't recognise our user agent).
        var recorder = new Recorder();
        using var client = new ImmichClient(new Uri("https://immich.test/api/"), new AuthCredential(AuthKind.ApiKey, "key"), recorder);

        await client.PingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AppInfo.Name, Assert.Single(recorder.Last!.Headers.GetValues("deviceModel")));
        Assert.Equal(AppInfo.OsName, Assert.Single(recorder.Last.Headers.GetValues("deviceType")));
    }
}
