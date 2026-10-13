using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichDesktop.App.Services;
using ImmichDesktop.Core;
using ImmichDesktop.Core.Platform;
using ImmichDesktop.Core.Settings;
using ImmichDesktop.Core.Sync;
using Material.Icons;

namespace ImmichDesktop.App.ViewModels;

public enum ShellPage
{
    Photos,
    Explore,
    Map,
    Sharing,
    Albums,
    Favorites,
    Archive,
    Locked,
    Trash,
    Utilities,
    Overview,
    Folders,
    Activity,
    Settings,
}

public sealed class NavItem(ShellPage page, string title, MaterialIconKind icon, MaterialIconKind activeIcon, Func<ViewModelBase> create)
{
    readonly Lazy<ViewModelBase> _viewModel = new(create);

    public ShellPage Page { get; } = page;
    public string Title { get; } = title;
    public MaterialIconKind Icon { get; } = icon;
    public MaterialIconKind ActiveIcon { get; } = activeIcon;
    public ViewModelBase ViewModel => _viewModel.Value;
    public bool IsCreated => _viewModel.IsValueCreated;
}

/// <summary>A group heading in the sidebar ("LIBRARY"); an empty title is just a divider.</summary>
public sealed class NavHeader(string title)
{
    public string Title { get; } = title;
    public bool IsDivider => Title.Length == 0;
}

/// <summary>Signed-in layout, modeled on the Immich web app: top bar, sidebar, storage and server status.</summary>
public sealed partial class ShellViewModel : ViewModelBase, IDisposable
{
    readonly CancellationTokenSource _lifetime = new();

    public ShellViewModel(MainWindowViewModel main)
    {
        Main = main;
        NavItem Library(ShellPage page, string title, MaterialIconKind icon, MaterialIconKind active, LibraryPage section) =>
            new(page, title, icon, active, () => new PhotosViewModel(this, section));

        // Like the Immich web app's sidebar, with the mobile app's Library group, plus this PC's backup.
        NavEntries =
        [
            Library(ShellPage.Photos, Tr.T("Photos"), MaterialIconKind.ImageMultipleOutline, MaterialIconKind.ImageMultiple, LibraryPage.Timeline),
            Library(ShellPage.Explore, Tr.T("Explore"), MaterialIconKind.CompassOutline, MaterialIconKind.Compass, LibraryPage.Explore),
            Library(ShellPage.Map, Tr.T("Map"), MaterialIconKind.MapOutline, MaterialIconKind.Map, LibraryPage.Map),
            Library(ShellPage.Sharing, Tr.T("Sharing"), MaterialIconKind.AccountMultipleOutline, MaterialIconKind.AccountMultiple, LibraryPage.Sharing),
            Library(ShellPage.Albums, Tr.T("Albums"), MaterialIconKind.ImageAlbum, MaterialIconKind.ImageAlbum, LibraryPage.Albums),
            new NavHeader(Tr.T("LIBRARY")),
            Library(ShellPage.Favorites, Tr.T("Favorites"), MaterialIconKind.HeartOutline, MaterialIconKind.Heart, LibraryPage.Favorites),
            Library(ShellPage.Archive, Tr.T("Archive"), MaterialIconKind.ArchiveOutline, MaterialIconKind.Archive, LibraryPage.Archive),
            Library(ShellPage.Locked, Tr.T("Locked folder"), MaterialIconKind.LockOutline, MaterialIconKind.Lock, LibraryPage.Locked),
            Library(ShellPage.Utilities, Tr.T("Utilities"), MaterialIconKind.Toolbox, MaterialIconKind.Toolbox, LibraryPage.Utilities),
            Library(ShellPage.Trash, Tr.T("Trash"), MaterialIconKind.TrashCanOutline, MaterialIconKind.TrashCan, LibraryPage.Trash),
            new NavHeader(Tr.T("THIS PC")),
            new NavItem(ShellPage.Overview, Tr.T("Backup"), MaterialIconKind.CloudUploadOutline, MaterialIconKind.CloudUpload, () => new OverviewViewModel(this)),
            new NavItem(ShellPage.Folders, Tr.T("Folders"), MaterialIconKind.FolderOutline, MaterialIconKind.Folder, () => new FoldersViewModel(this)),
            new NavItem(ShellPage.Activity, Tr.T("Activity"), MaterialIconKind.History, MaterialIconKind.History, () => new ActivityViewModel(this)),
        ];
        // Settings live in the top bar (like the web and mobile apps' account menu), not the sidebar.
        NavItems = [.. NavEntries.OfType<NavItem>(),
            new NavItem(ShellPage.Settings, Tr.T("Settings"), MaterialIconKind.CogOutline, MaterialIconKind.Cog, () => new SettingsViewModel(this))];
        SelectedNav = NavItems[0];

        var settings = AppServices.Settings.Current;
        LoadAccount(settings);
        IsLockEnabled = settings.AppLock != AppLockMode.Off;
        UpdateThemeIcon();
        ApplyStatus(AppServices.Engine.Status);

        AppServices.Engine.StatusChanged += OnStatusChanged;
        AppServices.Settings.Changed += OnSettingsChanged;
        ThemeService.Changed += OnThemeChanged;
        _ = LoadStorageAsync();
    }

    public MainWindowViewModel Main { get; }

    public IReadOnlyList<NavItem> NavItems { get; }

    /// <summary>The sidebar: pages and group headings.</summary>
    public IReadOnlyList<object> NavEntries { get; }

    [ObservableProperty]
    public partial NavItem SelectedNav { get; set; }

    [ObservableProperty]
    public partial ViewModelBase? CurrentPage { get; set; }

    partial void OnSelectedNavChanged(NavItem oldValue, NavItem newValue)
    {
        // Leaving the locked folder locks it again, like the Immich apps.
        if (oldValue is { IsCreated: true, ViewModel: PhotosViewModel { IsLockedFolder: true } locked })
            _ = locked.LockAsync();
        CurrentPage = newValue?.ViewModel;
    }

    // ---- Search (top bar, like the web app) ----

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [RelayCommand]
    void Search()
    {
        var text = SearchText.Trim();
        if (text.Length == 0)
            return;
        Navigate(ShellPage.Photos);
        if (SelectedNav.ViewModel is PhotosViewModel photos)
            photos.SearchFor(text);
    }

    [RelayCommand]
    void ShowBackup() => Navigate(ShellPage.Overview);

    public void Navigate(ShellPage page) => SelectedNav = NavItems.First(n => n.Page == page);

    // ---- Top bar ----

    [ObservableProperty]
    public partial string UserName { get; set; } = "";

    [ObservableProperty]
    public partial string UserEmail { get; set; } = "";

    [ObservableProperty]
    public partial string UserInitial { get; set; } = "";

    [ObservableProperty]
    public partial string ServerHost { get; set; } = "";

    [ObservableProperty]
    public partial string ServerVersion { get; set; } = "";

    [ObservableProperty]
    public partial MaterialIconKind ThemeIcon { get; set; }

    [ObservableProperty]
    public partial string ThemeTip { get; set; } = "";

    [ObservableProperty]
    public partial bool IsLockEnabled { get; set; }

    void LoadAccount(AppSettings settings)
    {
        UserName = string.IsNullOrWhiteSpace(settings.UserName) ? settings.UserEmail ?? "" : settings.UserName;
        UserEmail = settings.UserEmail ?? "";
        UserInitial = UserName.Length > 0 ? char.ToUpperInvariant(UserName[0]).ToString() : "?";
        ServerHost = Uri.TryCreate(settings.WebUrl, UriKind.Absolute, out var web) ? web.Host : "";
        ServerVersion = settings.ServerVersion ?? "";
    }

    void UpdateThemeIcon()
    {
        var dark = ThemeService.IsDark;
        ThemeIcon = dark ? MaterialIconKind.WeatherSunny : MaterialIconKind.WeatherNight;
        ThemeTip = dark ? Tr.T("Switch to light theme") : Tr.T("Switch to dark theme");
    }

    [RelayCommand]
    void ToggleTheme() => ThemeService.Toggle();

    [RelayCommand]
    void LockNow() => Main.Lock();

    [RelayCommand]
    void OpenWeb()
    {
        if (AppServices.Settings.Current.WebUrl is { } url)
            ShellLauncher.Open(url);
    }

    [RelayCommand]
    void OpenAccount() => Navigate(ShellPage.Settings);

    [RelayCommand]
    void OpenSettings() => Navigate(ShellPage.Settings);

    void OnThemeChanged(AppTheme _) => UpdateThemeIcon();

    // ---- Sidebar footer: storage + server status (as in Immich) ----

    [ObservableProperty]
    public partial bool HasStorage { get; set; }

    [ObservableProperty]
    public partial double StoragePercent { get; set; }

    [ObservableProperty]
    public partial string StorageText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsServerOnline { get; set; } = true;

    [ObservableProperty]
    public partial string ServerStatusText { get; set; } = Tr.T("Online");

    async Task LoadStorageAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            if (AppServices.Account.Client is { } client)
            {
                try
                {
                    var storage = await client.GetStorageAsync(_lifetime.Token);
                    StoragePercent = storage.Fraction * 100;
                    StorageText = Tr.T("{0} of {1} used", Format.Bytes(storage.Used), Format.Bytes(storage.Total));
                    HasStorage = storage.Total > 0;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // e.g. an API key without the server.storage permission: just hide the meter.
                    Log.Info($"Storage info unavailable: {ex.Message}");
                    HasStorage = false;
                }
            }
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(10), _lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // ---- Status ----

    [ObservableProperty]
    public partial StatusView Status { get; set; } = null!;

    void OnStatusChanged(SyncStatus status) => Dispatcher.UIThread.Post(() => ApplyStatus(status));

    void OnSettingsChanged(AppSettings settings) => Dispatcher.UIThread.Post(() =>
    {
        LoadAccount(settings);
        IsLockEnabled = settings.AppLock != AppLockMode.Off;
        ApplyStatus(AppServices.Engine.Status);
    });

    void ApplyStatus(SyncStatus status)
    {
        var view = StatusPresenter.Describe(status, AppServices.Settings.Current);
        if (view != Status)
            Status = view;

        IsServerOnline = status.State is not (SyncState.Offline or SyncState.AuthError);
        ServerStatusText = status.State switch
        {
            SyncState.Offline => Tr.T("Offline"),
            SyncState.AuthError => Tr.T("Signed out"),
            _ => Tr.T("Online"),
        };
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        AppServices.Engine.StatusChanged -= OnStatusChanged;
        AppServices.Settings.Changed -= OnSettingsChanged;
        ThemeService.Changed -= OnThemeChanged;
        foreach (var item in NavItems.Where(n => n.IsCreated))
            (item.ViewModel as IDisposable)?.Dispose();
    }
}
