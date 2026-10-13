using System.IO.Pipes;
using ImmichDesktop.Core;

namespace ImmichDesktop.App.Services;

/// <summary>
/// Ensures one instance per user. A second launch (e.g. from the Start menu) signals the running
/// instance over a named pipe so it can show its window, then exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    static string Suffix => $"{AppInfo.Id}-{Environment.UserName}";
    static string MutexName => $@"Local\{Suffix}";
    static string PipeName => $"{Suffix}-activate";

    readonly Mutex _mutex;
    readonly CancellationTokenSource _cts = new();

    SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>Raised on a background thread when another launch asks us to show the window.</summary>
    public event Action? ActivationRequested;

    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew)
            return new SingleInstance(mutex);

        mutex.Dispose();
        return null;
    }

    public static void SignalExisting()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000); // connecting is the signal
        }
        catch (Exception ex)
        {
            Log.Warn("Could not signal the running instance", ex);
        }
    }

    public void StartListening()
    {
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(ct);
                    ActivationRequested?.Invoke();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warn("Activation pipe error", ex);
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
            }
        }, ct);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _mutex.ReleaseMutex(); } catch { /* not owned on this thread */ }
        _mutex.Dispose();
    }
}
