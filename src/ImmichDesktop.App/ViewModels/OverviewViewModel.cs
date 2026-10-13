using System.Collections.ObjectModel;
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

public sealed partial class OverviewViewModel : ViewModelBase, IDisposable
{
    readonly ShellViewModel _shell;
    readonly DispatcherTimer _clock;
    SyncStatus _status;
    DateTime _lastFolderRefresh;

    public OverviewViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _status = AppServices.Engine.Status;
        Apply(_status);
        RefreshFolders();

        AppServices.Engine.StatusChanged += OnStatusChanged;
        AppServices.Settings.Changed += OnSettingsChanged;
        AppServices.Updates.UpdateReady += OnUpdateReady;
        UpdateAvailable = AppServices.Updates.ReadyVersion is not null;

        // Keeps "Checked 2 min ago" and the greeting fresh.
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => Apply(_status));
        _clock.Start();
    }

    public string Greeting
    {
        get
        {
            var hour = DateTime.Now.Hour;
            var part = hour < 5 ? "Good evening" : hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";
            var name = AppServices.Settings.Current.UserName?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return string.IsNullOrEmpty(name) ? part : $"{part}, {name}";
        }
    }

    // ---- Hero status ----

    [ObservableProperty]
    public partial StatusView Status { get; set; } = null!;

    [ObservableProperty]
    public partial bool ShowProgress { get; set; }

    [ObservableProperty]
    public partial bool IsIndeterminate { get; set; }

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = "";

    [ObservableProperty]
    public partial string SpeedText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseText), nameof(PauseIcon))]
    public partial bool IsPaused { get; set; }

    public string PauseText => IsPaused ? "Resume" : "Pause";
    public MaterialIconKind PauseIcon => IsPaused ? MaterialIconKind.Play : MaterialIconKind.Pause;

    [ObservableProperty]
    public partial bool NeedsSignIn { get; set; }

    // ---- Stats ----

    [ObservableProperty]
    public partial string BackedUpText { get; set; } = "0";

    [ObservableProperty]
    public partial string WaitingText { get; set; } = "0";

    [ObservableProperty]
    public partial string AttentionText { get; set; } = "0";

    [ObservableProperty]
    public partial bool HasAttention { get; set; }

    // ---- Uploads & folders ----

    public ObservableCollection<UploadItemViewModel> Uploads { get; } = [];

    [ObservableProperty]
    public partial bool HasUploads { get; set; }

    public ObservableCollection<FolderSummaryViewModel> Folders { get; } = [];

    [ObservableProperty]
    public partial bool HasFolders { get; set; }

    [ObservableProperty]
    public partial bool UpdateAvailable { get; set; }

    public string UpdateText => $"Version {AppServices.Updates.ReadyVersion} is ready to install.";

    /// <summary>Maps an engine snapshot onto the page. Public so the preview tool can feed sample data.</summary>
    public void Apply(SyncStatus s)
    {
        _status = s;
        var settings = AppServices.Settings.Current;
        Status = StatusPresenter.Describe(s, settings);
        IsPaused = settings.Paused;
        NeedsSignIn = s.State is SyncState.AuthError or SyncState.NotConfigured;
        OnPropertyChanged(nameof(Greeting));

        BackedUpText = Format.Count(s.Counts.BackedUp);
        WaitingText = Format.Count(s.Counts.Pending);
        AttentionText = Format.Count(s.Counts.Failed);
        HasAttention = s.Counts.Failed > 0;

        ShowProgress = s.IsBusy;
        IsIndeterminate = s.State == SyncState.Scanning || s.RunTotal == 0;
        ProgressPercent = s.RunTotal == 0 ? 0 : 100.0 * s.RunDone / s.RunTotal;
        ProgressText = s.State == SyncState.Scanning ? "Scanning folders…" : $"{Format.Count(s.RunDone)} of {Format.Plural(s.RunTotal, "file")}";
        SpeedText = s.BytesPerSecond > 1 ? Format.Speed(s.BytesPerSecond) : "";

        SyncUploads(s.Active);
    }

    void SyncUploads(IReadOnlyList<ActiveUpload> active)
    {
        var byPath = active.ToDictionary(a => a.Path);
        for (var i = Uploads.Count - 1; i >= 0; i--)
        {
            if (!byPath.ContainsKey(Uploads[i].Path))
                Uploads.RemoveAt(i);
        }
        foreach (var upload in active)
        {
            var item = Uploads.FirstOrDefault(u => u.Path == upload.Path);
            if (item is null)
                Uploads.Add(item = new UploadItemViewModel(upload.Path, upload.Size));
            item.Update(upload);
        }
        HasUploads = Uploads.Count > 0;
    }

    void RefreshFolders()
    {
        _lastFolderRefresh = DateTime.UtcNow;
        var settings = AppServices.Settings.Current;
        Dictionary<string, StatusCounts> counts;
        try
        {
            counts = AppServices.Database.GetFolderCounts();
        }
        catch
        {
            counts = [];
        }

        Folders.Clear();
        foreach (var folder in settings.Folders)
        {
            counts.TryGetValue(folder.Id, out var c);
            Folders.Add(new FolderSummaryViewModel(folder, c));
        }
        HasFolders = Folders.Count > 0;
    }

    void OnStatusChanged(SyncStatus status) => Dispatcher.UIThread.Post(() =>
    {
        Apply(status);
        if (DateTime.UtcNow - _lastFolderRefresh > TimeSpan.FromSeconds(2) || !status.IsBusy)
            RefreshFolders();
    });

    void OnSettingsChanged(AppSettings _) => Dispatcher.UIThread.Post(() =>
    {
        Apply(_status);
        RefreshFolders();
    });

    void OnUpdateReady() => Dispatcher.UIThread.Post(() =>
    {
        UpdateAvailable = true;
        OnPropertyChanged(nameof(UpdateText));
    });

    [RelayCommand]
    void TogglePause()
    {
        if (AppServices.Settings.Current.Paused)
            AppServices.Engine.Resume();
        else
            AppServices.Engine.Pause();
    }

    [RelayCommand]
    void SyncNow() => AppServices.Engine.SyncNow();

    [RelayCommand]
    void OpenWeb()
    {
        if (AppServices.Settings.Current.WebUrl is { } url)
            ShellLauncher.Open(url);
    }

    [RelayCommand]
    void SignIn() => _shell.Main.ShowOnboarding(resumeSignIn: true);

    [RelayCommand]
    void ShowProblems() => _shell.Navigate(ShellPage.Activity);

    [RelayCommand]
    void ManageFolders() => _shell.Navigate(ShellPage.Folders);

    [RelayCommand]
    void InstallUpdate() => AppServices.Updates.ApplyAndRestart(background: false);

    public void Dispose()
    {
        _clock.Stop();
        AppServices.Engine.StatusChanged -= OnStatusChanged;
        AppServices.Settings.Changed -= OnSettingsChanged;
        AppServices.Updates.UpdateReady -= OnUpdateReady;
    }
}

public sealed partial class UploadItemViewModel(string path, long size) : ViewModelBase
{
    public string Path { get; } = path;
    public string FileName { get; } = System.IO.Path.GetFileName(path);
    public string Folder { get; } = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? "";
    public MaterialIconKind Icon { get; } =
        MediaTypes.Default.Classify(path) == MediaKind.Video ? MaterialIconKind.VideoOutline : MaterialIconKind.ImageOutline;

    [ObservableProperty]
    public partial double Percent { get; set; }

    [ObservableProperty]
    public partial string SizeText { get; set; } = Format.Bytes(size);

    public void Update(ActiveUpload upload)
    {
        Percent = upload.Fraction * 100;
        SizeText = upload.Sent > 0 && upload.Sent < upload.Size
            ? $"{Format.Bytes(upload.Sent)} of {Format.Bytes(upload.Size)}"
            : Format.Bytes(upload.Size);
    }
}

public sealed class FolderSummaryViewModel(SyncFolder folder, StatusCounts counts)
{
    public string Name { get; } = folder.DisplayName;
    public string Path { get; } = folder.Path;
    public bool Enabled { get; } = folder.Enabled;
    public double Percent { get; } = counts.Total == 0 ? 0 : 100.0 * counts.BackedUp / counts.Total;
    public bool IsComplete { get; } = counts.Total > 0 && counts.BackedUp == counts.Total;

    public string StatsText { get; } = !folder.Enabled
        ? "Paused for this folder"
        : counts.Total == 0
            ? "No photos or videos found yet"
            : $"{Format.Count(counts.BackedUp)} of {Format.Count(counts.Total)} backed up";
}
