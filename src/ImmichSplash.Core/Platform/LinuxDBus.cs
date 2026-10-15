using Tmds.DBus.Protocol;

namespace ImmichSplash.Core.Platform;

/// <summary>
/// Talks to Linux desktop services over D-Bus. Used instead of command-line tools (nmcli, gsettings,
/// notify-send), which aren't there inside the Flatpak.
/// </summary>
public static class LinuxDBus
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A connection of its own (not the library's shared one, which can't tell its own bus name and, once cut,
    /// stays cut for everything that uses it). The caller disposes it.
    /// </summary>
    public static async Task<Connection> OpenAsync(bool system)
    {
        var address = (system ? Address.System : Address.Session)
            ?? throw new InvalidOperationException(system ? "No system bus." : "No session bus.");
        var connection = new Connection(address);
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(Timeout);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Does <paramref name="work"/> on a connection opened for it.</summary>
    public static async Task<T> UseAsync<T>(bool system, Func<Connection, Task<T>> work)
    {
        using var connection = await OpenAsync(system);
        return await work(connection);
    }

    /// <summary>Reads a property (org.freedesktop.DBus.Properties.Get).</summary>
    public static async Task<VariantValue> GetPropertyAsync(Connection bus, string destination, string path, string @interface, string property)
    {
        MessageBuffer message;
        using (var writer = bus.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(destination, path, "org.freedesktop.DBus.Properties", "Get", "ss", MessageFlags.None);
            writer.WriteString(@interface);
            writer.WriteString(property);
            message = writer.CreateMessage();
        }
        var value = await bus.CallMethodAsync(message, static (m, _) => m.GetBodyReader().ReadVariantValue(), null).WaitAsync(Timeout);
        return value.Type == VariantValueType.Variant ? value.GetVariantValue() : value;
    }

    /// <summary>
    /// Writes a call's arguments. By reference: <see cref="MessageWriter"/> is a struct, and arguments written to a copy
    /// leave the message saying it has no body, which the bus answers by hanging up ("Connection closed by peer").
    /// </summary>
    public delegate void WriteArgs(ref MessageWriter writer);

    /// <summary>Calls a method; <paramref name="write"/> writes the arguments, <paramref name="read"/> reads the reply.</summary>
    public static Task<T> CallAsync<T>(Connection bus, string destination, string path, string @interface, string member, string? signature,
        WriteArgs? write, MessageValueReader<T> read, TimeSpan? timeout = null)
    {
        MessageBuffer message;
        var writer = bus.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination, path, @interface, member, signature, MessageFlags.None);
            write?.Invoke(ref writer);
            message = writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
        return bus.CallMethodAsync(message, read, null).WaitAsync(timeout ?? Timeout);
    }

    /// <summary>Runs async D-Bus work from synchronous code (called off the UI thread).</summary>
    public static T Wait<T>(Task<T> task) => task.WaitAsync(Timeout).GetAwaiter().GetResult();
}
