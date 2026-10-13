using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ImmichDesktop.App.Services;
using ImmichDesktop.Core.Settings;
using ImmichDesktop.Core.Sync;
using Material.Icons;

namespace ImmichDesktop.App.ViewModels;

public enum ShellPage
{
    Overview,
    Folders,
    Activity,
    Settings,
}

public sealed class NavItem(ShellPage page, string title, MaterialIconKind icon, Func<ViewModelBase> create)
{
    readonly Lazy<ViewModelBase> _viewModel = new(create);

    public ShellPage Page { get; } = page;
    public string Title { get; } = title;
    public MaterialIconKind Icon { get; } = icon;
    public ViewModelBase ViewModel => _viewModel.Value;
    public bool IsCreated => _viewModel.IsValueCreated;
}

/// <summary>Signed-in layout: sidebar navigation, account card and live status pill.</summary>
public sealed partial class ShellViewModel : ViewModelBase, IDisposable
{
    public ShellViewModel(MainWindowViewModel main)
    {
        Main = main;
        NavItems =
        [
            new NavItem(ShellPage.Overview, "Overview", MaterialIconKind.ViewDashboardOutline, () => new OverviewViewModel(this)),
            new NavItem(ShellPage.Folders, "Folders", MaterialIconKind.FolderOutline, () => new FoldersViewModel(this)),
            new NavItem(ShellPage.Activity, "Activity", MaterialIconKind.History, () => new ActivityViewModel(this)),
            new NavItem(ShellPage.Settings, "Settings", MaterialIconKind.CogOutline, () => new SettingsViewModel(this)),
        ];
        SelectedNav = NavItems[0];

        LoadAccount(AppServices.Settings.Current);
        ApplyStatus(AppServices.Engine.Status);
        AppServices.Engine.StatusChanged += OnStatusChanged;
        AppServices.Settings.Changed += OnSettingsChanged;
    }

    public MainWindowViewModel Main { get; }

    public IReadOnlyList<NavItem> NavItems { get; }

    [ObservableProperty]
    public partial NavItem SelectedNav { get; set; }

    [ObservableProperty]
    public partial ViewModelBase? CurrentPage { get; set; }

    partial void OnSelectedNavChanged(NavItem value) => CurrentPage = value?.ViewModel;

    public void Navigate(ShellPage page) => SelectedNav = NavItems.First(n => n.Page == page);

    // ---- Account card ----

    [ObservableProperty]
    public partial string UserName { get; set; } = "";

    [ObservableProperty]
    public partial string UserInitial { get; set; } = "";

    [ObservableProperty]
    public partial string ServerHost { get; set; } = "";

    void LoadAccount(AppSettings settings)
    {
        UserName = string.IsNullOrWhiteSpace(settings.UserName) ? settings.UserEmail ?? "" : settings.UserName;
        UserInitial = UserName.Length > 0 ? char.ToUpperInvariant(UserName[0]).ToString() : "?";
        ServerHost = Uri.TryCreate(settings.WebUrl, UriKind.Absolute, out var web) ? web.Host : "";
    }

    // ---- Status pill ----

    [ObservableProperty]
    public partial StatusView Status { get; set; } = null!;

    void OnStatusChanged(SyncStatus status) => Dispatcher.UIThread.Post(() => ApplyStatus(status));

    void OnSettingsChanged(AppSettings settings) => Dispatcher.UIThread.Post(() =>
    {
        LoadAccount(settings);
        ApplyStatus(AppServices.Engine.Status);
    });

    void ApplyStatus(SyncStatus status)
    {
        var view = StatusPresenter.Describe(status, AppServices.Settings.Current);
        if (view != Status)
            Status = view;
    }

    public void Dispose()
    {
        AppServices.Engine.StatusChanged -= OnStatusChanged;
        AppServices.Settings.Changed -= OnSettingsChanged;
        foreach (var item in NavItems.Where(n => n.IsCreated))
            (item.ViewModel as IDisposable)?.Dispose();
    }
}
