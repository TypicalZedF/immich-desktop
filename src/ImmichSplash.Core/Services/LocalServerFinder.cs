using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace ImmichSplash.Core.Services;

/// <summary>
/// Looks for Immich servers on the local network during setup. Immich doesn't announce itself, so this
/// knocks on its default port (2283) across the home network (the /24 around each of this computer's
/// private addresses) and keeps the addresses that answer Immich's ping. Takes a few seconds.
/// </summary>
public static class LocalServerFinder
{
    public const int DefaultPort = 2283;
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(600);
    static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(2);

    /// <summary>For the screenshot tool: answers instead of looking.</summary>
    public static Func<CancellationToken, Task<IReadOnlyList<Uri>>>? Override { get; set; }

    /// <summary>Web addresses (http://host:2283) of the Immich servers found, nearest first.</summary>
    public static async Task<IReadOnlyList<Uri>> FindAsync(CancellationToken ct)
    {
        if (Override is { } fake)
            return await fake(ct);
        var hosts = CandidateHosts(LocalNetworks()).ToList();
        var found = new System.Collections.Concurrent.ConcurrentBag<(int Order, Uri Url)>();
        using var http = new HttpClient { Timeout = PingTimeout };
        await Parallel.ForEachAsync(hosts.Select((host, i) => (host, i)), new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = ct },
            async (item, token) =>
            {
                if (await IsOpenAsync(item.host, token) && await IsImmichAsync(http, item.host, token) is { } url)
                    found.Add((item.i, url));
            });
        var urls = found.OrderBy(f => f.Order).Select(f => f.Url).ToList();
        // The same server by name and by address: keep the name.
        foreach (var named in urls.Where(u => u.HostNameType == UriHostNameType.Dns && u.Host != "localhost").ToList())
        {
            try
            {
                var addresses = (await Dns.GetHostAddressesAsync(named.Host, ct)).Select(a => a.ToString()).ToHashSet();
                urls.RemoveAll(u => addresses.Contains(u.Host));
            }
            catch (SocketException)
            {
            }
        }
        return urls;
    }

    /// <summary>This computer's private IPv4 addresses with their prefix lengths (home and office networks).</summary>
    static IEnumerable<(IPAddress Address, int Prefix)> LocalNetworks()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    yield return (unicast.Address, unicast.PrefixLength);
            }
        }
    }

    /// <summary>
    /// Where to look: this computer itself, then the usual "immich" names, then every address in the /24 around
    /// each private address (a bigger network is cut to its /24, to stay quick). At most three networks.
    /// </summary>
    public static IEnumerable<string> CandidateHosts(IEnumerable<(IPAddress Address, int Prefix)> networks)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "localhost", "immich.local", "immich" })
            if (seen.Add(name))
                yield return name;
        var subnets = networks
            .Where(n => IsPrivate(n.Address))
            .Select(n => (Bytes: n.Address.GetAddressBytes(), n.Address))
            .DistinctBy(n => (n.Bytes[0], n.Bytes[1], n.Bytes[2]))
            .Take(3);
        foreach (var (bytes, own) in subnets)
        {
            for (var last = 1; last <= 254; last++)
            {
                var address = new IPAddress([bytes[0], bytes[1], bytes[2], (byte)last]);
                if (!address.Equals(own) && seen.Add(address.ToString()))
                    yield return address.ToString();
            }
        }
    }

    static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    static async Task<bool> IsOpenAsync(string host, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ConnectTimeout);
        try
        {
            using var socket = new TcpClient();
            await socket.ConnectAsync(host, DefaultPort, cts.Token);
            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false; // nothing there, or a name that doesn't resolve
        }
    }

    static async Task<Uri?> IsImmichAsync(HttpClient http, string host, CancellationToken ct)
    {
        var url = new Uri($"http://{host}:{DefaultPort}/");
        try
        {
            using var response = await http.GetAsync(new Uri(url, "api/server/ping"), ct);
            if (!response.IsSuccessStatusCode)
                return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("res", out var res) && res.GetString() == "pong"
                ? url
                : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
