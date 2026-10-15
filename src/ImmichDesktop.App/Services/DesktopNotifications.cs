using System.Diagnostics;
using ImmichDesktop.Core;
using ImmichDesktop.Core.Sync;

namespace ImmichDesktop.App.Services;

/// <summary>
/// Shows backup notices as desktop notifications: Windows' own notifications, or the desktop's
/// notification service on Linux (through notify-send). Clicking one raises <see cref="Clicked"/>.
/// </summary>
public static class DesktopNotifications
{
    /// <summary>Registered once per user so Windows shows the app's name and logo on its notifications.</summary>
    public const string WindowsAppId = "TypicalZedF.ImmichDesktop";

    /// <summary>Raised (on a background thread) when a notification is clicked.</summary>
    public static event Action<NoticeKind>? Clicked;

    static string IconPath => Path.Combine(AppContext.BaseDirectory, "notification-icon.png");
    static bool _linuxUnavailable;

    public static void Show(Notice notice)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                ShowOnWindows(notice);
            else if (OperatingSystem.IsLinux() && !_linuxUnavailable)
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

    static async Task ShowOnLinuxAsync(Notice notice)
    {
        try
        {
            // With an "Open" action notify-send waits and prints the action when the notification is clicked.
            var (code, output) = await NotifySendAsync(notice, withAction: true);
            if (code != 0) // older notify-send without actions
                (code, _) = await NotifySendAsync(notice, withAction: false);
            else if (output.Trim() == "default")
                Clicked?.Invoke(notice.Kind);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            _linuxUnavailable = true;
            Log.Info("Desktop notifications need notify-send (libnotify), which isn't installed");
        }
    }

    static async Task<(int Code, string Output)> NotifySendAsync(Notice notice, bool withAction)
    {
        var start = new ProcessStartInfo("notify-send") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--app-name=" + AppInfo.Name);
        start.ArgumentList.Add("--icon=" + IconPath);
        if (withAction)
        {
            start.ArgumentList.Add("--action=default=" + Tr.T("Open"));
            start.ArgumentList.Add("--wait");
        }
        start.ArgumentList.Add(notice.Title);
        start.ArgumentList.Add(notice.Body);
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output);
    }

    /// <summary>Uninstall: forget the Windows registration.</summary>
    public static void Unregister()
    {
        if (OperatingSystem.IsWindows())
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\AppUserModelId\{WindowsAppId}", throwOnMissingSubKey: false);
    }
}
