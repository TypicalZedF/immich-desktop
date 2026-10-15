using Tmds.DBus.Protocol;

namespace ImmichSplash.Core.Platform;

/// <summary>
/// Talks to Linux desktop services over D-Bus. Used instead of command-line tools (nmcli, gsettings,
/// notify-send), which aren't there inside the Flatpak.
/// </summary>
public static class LinuxDBus
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static Connection System => Connection.System;
    public static Connection Session => Connection.Session;

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

    /// <summary>Calls a method; <paramref name="write"/> writes the arguments, <paramref name="read"/> reads the reply.</summary>
    public static Task<T> CallAsync<T>(Connection bus, string destination, string path, string @interface, string member, string? signature,
        Action<MessageWriter>? write, MessageValueReader<T> read)
    {
        MessageBuffer message;
        using (var writer = bus.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(destination, path, @interface, member, signature, MessageFlags.None);
            write?.Invoke(writer);
            message = writer.CreateMessage();
        }
        return bus.CallMethodAsync(message, read, null).WaitAsync(Timeout);
    }

    /// <summary>Runs async D-Bus work from synchronous code (called off the UI thread).</summary>
    public static T Wait<T>(Task<T> task) => task.WaitAsync(Timeout).GetAwaiter().GetResult();
}
