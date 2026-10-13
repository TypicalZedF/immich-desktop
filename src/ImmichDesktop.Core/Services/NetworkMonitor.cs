using System.Net.NetworkInformation;
using ImmichDesktop.Core.Api;
using ImmichDesktop.Core.Platform;
using ImmichDesktop.Core.Settings;

namespace ImmichDesktop.Core.Services;

/// <summary>
/// Watches the network. When it changes (and every few minutes), re-checks which server address
/// works best (local at home, external elsewhere) and switches to it, and notes whether the
/// connection is metered for data saving.
/// </summary>
public sealed class NetworkMonitor : IDisposable
{
    static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(5);
    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    readonly SettingsStore _settings;
    readonly AccountService _account;
    readonly Func<Uri, CancellationToken, Task<bool>> _probe;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly Timer _timer;
    CancellationTokenSource? _debounce;

    public NetworkMonitor(SettingsStore settings, AccountService account, Func<Uri, CancellationToken, Task<bool>>? probe = null)
    {
        _settings = settings;
        _account = account;
        _probe = probe ?? ProbeAsync;
        _timer = new Timer(_ => _ = CheckAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The network last seen.</summary>
    public NetworkSnapshot Network { get; private set; } = NetworkSnapshot.Unknown;

    public bool IsMetered => Network.IsMetered;

    /// <summary>True when on the network marked as local and using its local address.</summary>
    public bool IsUsingLocal { get; private set; }

    /// <summary>Raised (on any thread) after each check.</summary>
    public event Action? Changed;

    public void Start()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        _timer.Change(TimeSpan.FromSeconds(1), RecheckInterval);
    }

    void OnNetworkChanged(object? sender, EventArgs e)
    {
        // Networks change in bursts (adapters, DHCP, VPNs); wait for it to settle.
        _debounce?.Cancel();
        var cts = _debounce = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromSeconds(3), cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
                _ = CheckAsync();
        }, TaskScheduler.Default);
    }

    /// <summary>Re-reads the network and picks the best working server address.</summary>
    public async Task CheckAsync()
    {
        if (!await _gate.WaitAsync(0))
            return;
        try
        {
            Network = await Task.Run(NetworkStatus.Current);
            var settings = _settings.Current;
            if (_account.Client is { } client && settings.IsConfigured)
            {
                var candidates = ServerEndpoints.Candidates(settings, Network.Name);
                foreach (var candidate in candidates)
                {
                    if (candidate == client.ApiUrl && candidates.Count == 1)
                        break; // nothing to choose from
                    using var cts = new CancellationTokenSource(ProbeTimeout);
                    if (await _probe(candidate, cts.Token))
                    {
                        _account.SwitchEndpoint(candidate);
                        break;
                    }
                }
                var local = ServerEndpoints.IsLocalNetwork(settings, Network.Name) ? ServerEndpoints.ToApiUrl(settings.LocalServerUrl) : null;
                IsUsingLocal = local is not null && _account.Client?.ApiUrl == local;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Network check failed", ex);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    static async Task<bool> ProbeAsync(Uri apiUrl, CancellationToken ct)
    {
        try
        {
            using var client = new ImmichClient(apiUrl, null);
            await client.PingAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _timer.Dispose();
        _debounce?.Cancel();
    }
}
