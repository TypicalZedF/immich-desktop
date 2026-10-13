using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using ImmichDesktop.Core;

namespace ImmichDesktop.Core.Api;

public sealed class ImmichApiException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public bool IsUnauthorized => StatusCode == HttpStatusCode.Unauthorized;

    /// <summary>Server-side or rate-limit errors that are worth retrying later.</summary>
    public bool IsTransient =>
        (int)StatusCode >= 500 || StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
}

/// <summary>Thrown when an address can't be resolved to a working Immich server.</summary>
public sealed class ServerDiscoveryException(string message, Exception? inner = null) : Exception(message, inner);

public static class NetworkErrors
{
    /// <summary>True for failures caused by the network or the server being unavailable (not by a bad file).</summary>
    public static bool IsConnectivity(Exception ex) => ex switch
    {
        HttpRequestException => true,
        TimeoutException => true,
        SocketException => true,
        ImmichApiException api => api.IsTransient,
        AggregateException agg => agg.InnerExceptions.Any(IsConnectivity),
        _ => ex.InnerException is not null && IsConnectivity(ex.InnerException),
    };

    public static string Describe(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case AuthenticationException:
                    return Loc.T("The server's security certificate isn't trusted.");
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData }:
                    return Loc.T("Couldn't find that server. Check the address.");
                case SocketException { SocketErrorCode: SocketError.ConnectionRefused }:
                    return Loc.T("The server refused the connection. Is Immich running?");
                case TimeoutException:
                    return Loc.T("The server took too long to respond.");
                case ImmichApiException api:
                    return api.Message;
            }
        }
        return ex is HttpRequestException
            ? Loc.T("Couldn't connect to the server. Check the address and your internet connection.")
            : ex.Message;
    }
}
