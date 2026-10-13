using ImmichDesktop.Core;
using ImmichDesktop.Core.Services;
using ImmichDesktop.Core.Settings;
using ImmichDesktop.Core.Storage;
using ImmichDesktop.Core.Sync;

namespace ImmichDesktop.App.Services;

/// <summary>Composition root: the long-lived services shared by the tray and the window.</summary>
public static class AppServices
{
    public static SettingsStore Settings { get; private set; } = null!;
    public static StateDatabase Database { get; private set; } = null!;
    public static ActivityLog Activity { get; private set; } = null!;
    public static AccountService Account { get; private set; } = null!;
    public static SyncEngine Engine { get; private set; } = null!;
    public static UpdateService Updates { get; private set; } = null!;
    public static ProfileService Profile { get; private set; } = null!;
    public static WallpaperService Wallpaper { get; private set; } = null!;
    public static NetworkMonitor Network { get; private set; } = null!;

    public static void Initialize()
    {
        Settings = new SettingsStore(AppPaths.SettingsFile);
        Database = new StateDatabase(AppPaths.DatabaseFile);
        Activity = new ActivityLog(Database);
        Account = new AccountService(Settings, Database);
        Account.Restore();
        WindowsNetwork.Register();
        Network = new NetworkMonitor(Settings, Account);
        Engine = new SyncEngine(Settings, Database, Account, Activity) { IsMetered = () => Network.IsMetered };
        Updates = new UpdateService();
        Profile = new ProfileService();
        Wallpaper = new WallpaperService();
        Account.SessionChanged += () =>
        {
            if (Account.IsSignedIn)
                _ = Profile.RefreshAsync();
            else
                Profile.Clear();
        };
        if (Account.IsSignedIn)
            _ = Profile.RefreshAsync();
    }

    public static async Task ShutdownAsync()
    {
        Updates?.Dispose();
        Network?.Dispose();
        Wallpaper?.Dispose();
        if (Engine is not null)
            await Engine.DisposeAsync();
        Account?.Dispose();
    }
}
