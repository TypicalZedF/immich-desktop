using ImmichSplash.Core;
using ImmichSplash.Core.Platform;
using ImmichSplash.Core.Sync;

namespace ImmichSplash.App.Services;

/// <summary>
/// Shows backup notices as desktop notifications: Windows' own notifications, or the desktop's
/// notification service on Linux (over D-Bus). Clicking one raises <see cref="Clicked"/>.
/// </summary>
public static class DesktopNotifications
{
    /// <summary>Registered once per user so Windows shows the app's name and logo on its notifications.</summary>
    public const string WindowsAppId = "TypicalZedF.ImmichSplash";

    /// <summary>Raised (on a background thread) when a notification is clicked.</summary>
    public static event Action<NoticeKind>? Clicked;

    static string IconPath => Path.Combine(AppContext.BaseDirectory, "notification-icon.png");

    public static void Show(Notice notice)
    {
        Log.Info($"Notification: {notice.Kind} \"{notice.Title}\"");
        try
        {
            if (OperatingSystem.IsWindows())
                ShowOnWindows(notice);
            else if (OperatingSystem.IsLinux())
                _ = ShowOnLinuxAsync(notice);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not show a notification", ex);
        }
    }

    static void ShowOnWindows(Notice notice)
    {
#if WINDOWS
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{WindowsAppId}"))
        {
            key.SetValue("DisplayName", AppInfo.Name);
            key.SetValue("IconUri", IconPath);
        }
        static string Text(string s) => System.Security.SecurityElement.Escape(s) ?? "";
        var xml = new Windows.Data.Xml.Dom.XmlDocument();
        // The logo is also in the notification itself: Windows caches the registered icon.
        var logo = new Uri(IconPath).AbsoluteUri;
        xml.LoadXml($"""<toast launch="{notice.Kind}"><visual><binding template="ToastGeneric"><text>{Text(notice.Title)}</text><text>{Text(notice.Body)}</text><image placement="appLogoOverride" src="{Text(logo)}"/></binding></visual></toast>""");
        var toast = new Windows.UI.Notifications.ToastNotification(xml);
        toast.Activated += (_, _) => Clicked?.Invoke(notice.Kind);
        Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(WindowsAppId).Show(toast);
#endif
    }

    /// <summary>
    /// Through the desktop's notification service over D-Bus (org.freedesktop.Notifications; KDE, GNOME and others,
    /// also from the Flatpak). The default action makes the whole notification clickable.
    /// </summary>
    static async Task ShowOnLinuxAsync(Notice notice)
    {
        // A connection that was cut (the desktop restarted, say) is replaced once.
        for (var attempt = 0; ; attempt++)
        {
            Tmds.DBus.Protocol.Connection bus;
            try
            {
                bus = await GetBusAsync();
            }
            catch (Exception ex)
            {
                Log.Warn("Could not reach the desktop's notification service", ex);
                return;
            }
            try
            {
                await NotifyAsync(bus, notice);
                return;
            }
            catch (Tmds.DBus.Protocol.DisconnectedException) when (attempt == 0)
            {
                DropBus(bus);
            }
            catch (Exception ex)
            {
                Log.Warn("Could not show a desktop notification", ex);
                return;
            }
        }
    }

    static async Task NotifyAsync(Tmds.DBus.Protocol.Connection bus, Notice notice)
    {
        var icon = Core.Platform.Flatpak.AppId ?? IconPath; // the sandbox's files aren't visible to the desktop; its icon is
        var id = await LinuxDBus.CallAsync(bus, "org.freedesktop.Notifications", "/org/freedesktop/Notifications",
            "org.freedesktop.Notifications", "Notify", "susssasa{sv}i", w =>
            {
                w.WriteString(AppInfo.Name);
                w.WriteUInt32(0);
                w.WriteString(icon);
                w.WriteString(notice.Title);
                w.WriteString(notice.Body);
                w.WriteArray(new[] { "default", Tr.T("Open") });
                var hints = w.WriteDictionaryStart();
                if (Core.Platform.Flatpak.AppId is { } appId)
                {
                    w.WriteDictionaryEntryStart();
                    w.WriteString("desktop-entry"); // groups it under the app in the desktop's notification settings
                    w.WriteVariantString(appId);
                }
                w.WriteDictionaryEnd(hints);
                w.WriteInt32(-1);
            }, static (m, _) => m.GetBodyReader().ReadUInt32());
        lock (Shown)
        {
            Shown[id] = notice.Kind;
            if (Shown.Count > 50)
                Shown.Remove(Shown.Keys.Min());
        }
    }

    static readonly Dictionary<uint, NoticeKind> Shown = [];
    static Task<Tmds.DBus.Protocol.Connection>? _bus;

    /// <summary>The app's own session-bus connection for notifications, kept open to hear clicks.</summary>
    static Task<Tmds.DBus.Protocol.Connection> GetBusAsync()
    {
        lock (Shown)
        {
            if (_bus is { IsFaulted: true } or { IsCanceled: true })
                _bus = null;
            return _bus ??= Open();
        }

        static async Task<Tmds.DBus.Protocol.Connection> Open()
        {
            var bus = await LinuxDBus.OpenAsync(system: false);
            await ListenForClicksAsync(bus);
            return bus;
        }
    }

    static void DropBus(Tmds.DBus.Protocol.Connection bus)
    {
        lock (Shown)
        {
            if (_bus is { IsCompletedSuccessfully: true } current && current.Result == bus)
                _bus = null;
        }
        bus.Dispose();
    }

    static async Task ListenForClicksAsync(Tmds.DBus.Protocol.Connection bus)
    {
        var rule = new Tmds.DBus.Protocol.MatchRule
        {
            Type = Tmds.DBus.Protocol.MessageType.Signal,
            Interface = "org.freedesktop.Notifications",
            Member = "ActionInvoked",
            Path = "/org/freedesktop/Notifications",
        };
        try
        {
            await bus.AddMatchAsync(rule, static (m, _) =>
            {
                var reader = m.GetBodyReader();
                return (Id: reader.ReadUInt32(), Action: reader.ReadString());
            }, static (ex, signal, _, _) =>
            {
                if (ex is not null || signal.Action != "default")
                    return;
                NoticeKind kind;
                lock (Shown)
                    if (!Shown.Remove(signal.Id, out kind))
                        return; // another app's notification
                Clicked?.Invoke(kind);
            }, Tmds.DBus.Protocol.ObserverFlags.None, null, null, false);
        }
        catch (Exception ex)
        {
            Log.Info($"Notification clicks won't open the app: {ex.Message}"); // still shows them
        }
    }

    /// <summary>Uninstall: forget the Windows registration.</summary>
    public static void Unregister()
    {
        if (OperatingSystem.IsWindows())
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\AppUserModelId\{WindowsAppId}", throwOnMissingSubKey: false);
            // Windows adds its own per-app notification settings the first time a toast is shown.
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\{WindowsAppId}", throwOnMissingSubKey: false);
        }
    }
}
