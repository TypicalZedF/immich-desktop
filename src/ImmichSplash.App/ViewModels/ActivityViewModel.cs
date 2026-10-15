using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Platform;
using ImmichSplash.Core.Sync;
using Material.Icons;

namespace ImmichSplash.App.ViewModels;

public enum ActivityFilter
{
    All,
    Uploads,
    Problems,
}

public sealed partial class ActivityViewModel : ViewModelBase, IDisposable
{
    const int MaxItems = 300;
    readonly List<ActivityItemViewModel> _all = [];
    readonly DispatcherTimer _clock;

    public ActivityViewModel(ShellViewModel shell)
    {
        _ = shell;
        foreach (var entry in AppServices.Activity.Recent(MaxItems))
            _all.Add(new ActivityItemViewModel(entry));
        RefreshFailures();
        ApplyFilter();

        AppServices.Activity.Added += OnAdded;
        AppServices.Activity.Cleared += OnCleared;
        AppServices.Engine.StatusChanged += OnStatusChanged;
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) =>
        {
            foreach (var item in Items)
                item.RefreshTime();
        });
        _clock.Start();
    }

    public ObservableCollection<ActivityItemViewModel> Items { get; } = [];
    public ObservableCollection<FailedFileViewModel> Failures { get; } = [];

    [ObservableProperty]
    public partial ActivityFilter Filter { get; set; }

    public bool ShowAll { get => Filter == ActivityFilter.All; set { if (value) Filter = ActivityFilter.All; } }
    public bool ShowUploads { get => Filter == ActivityFilter.Uploads; set { if (value) Filter = ActivityFilter.Uploads; } }
    public bool ShowProblems { get => Filter == ActivityFilter.Problems; set { if (value) Filter = ActivityFilter.Problems; } }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial bool HasFailures { get; set; }

    [ObservableProperty]
    public partial string FailuresTitle { get; set; } = "";

    partial void OnFilterChanged(ActivityFilter value)
    {
        OnPropertyChanged(nameof(ShowAll));
        OnPropertyChanged(nameof(ShowUploads));
        OnPropertyChanged(nameof(ShowProblems));
        ApplyFilter();
    }

    bool Matches(ActivityItemViewModel item) => Filter switch
    {
        ActivityFilter.Uploads => item.Kind == ActivityKind.Uploaded,
        ActivityFilter.Problems => item.Kind is ActivityKind.Error or ActivityKind.Warning,
        _ => true,
    };

    void ApplyFilter()
    {
        Items.Clear();
        foreach (var item in _all.Where(Matches))
            Items.Add(item);
        IsEmpty = Items.Count == 0;
    }

    void OnAdded(ActivityEntry entry) => Dispatcher.UIThread.Post(() =>
    {
        var item = new ActivityItemViewModel(entry);
        _all.Insert(0, item);
        if (_all.Count > MaxItems)
            _all.RemoveAt(_all.Count - 1);
        if (Matches(item))
        {
            Items.Insert(0, item);
            if (Items.Count > MaxItems)
                Items.RemoveAt(Items.Count - 1);
        }
        IsEmpty = Items.Count == 0;
    });

    void OnCleared() => Dispatcher.UIThread.Post(() =>
    {
        _all.Clear();
        ApplyFilter();
    });

    DateTime _lastFailureRefresh;

    void OnStatusChanged(SyncStatus status) => Dispatcher.UIThread.Post(() =>
    {
        if (!status.IsBusy || DateTime.UtcNow - _lastFailureRefresh > TimeSpan.FromSeconds(5))
            RefreshFailures();
    });

    void RefreshFailures()
    {
        _lastFailureRefresh = DateTime.UtcNow;
        List<FailedFile> failed;
        try
        {
            failed = AppServices.Database.GetFailed(50);
        }
        catch
        {
            return;
        }

        Failures.Clear();
        foreach (var f in failed)
            Failures.Add(new FailedFileViewModel(f));
        var total = AppServices.Engine.Status.Counts.Failed;
        HasFailures = Failures.Count > 0;
        FailuresTitle = Tr.T("{0} couldn't be uploaded", Format.Plural(Math.Max(total, Failures.Count), "file"));
    }

    [RelayCommand]
    void RetryFailed()
    {
        AppServices.Engine.RetryFailed();
        Failures.Clear();
        HasFailures = false;
    }

    [RelayCommand]
    void OpenLogs() => ShellLauncher.Open(AppPaths.LogsDir);

    [RelayCommand]
    void Clear() => AppServices.Activity.Clear();

    public void Dispose()
    {
        _clock.Stop();
        AppServices.Activity.Added -= OnAdded;
        AppServices.Activity.Cleared -= OnCleared;
        AppServices.Engine.StatusChanged -= OnStatusChanged;
    }
}

public sealed partial class ActivityItemViewModel : ViewModelBase
{
    readonly DateTimeOffset _time;

    public ActivityItemViewModel(ActivityEntry entry)
    {
        _time = entry.Time;
        Kind = entry.Kind;
        Path = entry.Path;
        Message = entry.Kind == ActivityKind.Uploaded ? Tr.T("Uploaded {0}", entry.Message) : entry.Message;
        var directory = entry.Path is null ? null : System.IO.Path.GetDirectoryName(entry.Path);
        Detail = directory is null ? null : System.IO.Path.GetFileName(directory);
        DetailTip = entry.Path;
        (Icon, ToneClass) = entry.Kind switch
        {
            ActivityKind.Uploaded => (MediaIcon(entry.Path), "busy"),
            ActivityKind.Success => (MaterialIconKind.CloudCheckOutline, "ok"),
            ActivityKind.Warning => (MaterialIconKind.AlertOutline, "warning"),
            ActivityKind.Error => (MaterialIconKind.AlertCircleOutline, "error"),
            _ => (MaterialIconKind.InformationOutline, "paused"),
        };
        RefreshTime();
    }

    static MaterialIconKind MediaIcon(string? path) =>
        path is not null && MediaTypes.Default.Classify(path) == MediaKind.Video ? MaterialIconKind.VideoOutline : MaterialIconKind.ImageOutline;

    public ActivityKind Kind { get; }
    public string Message { get; }
    public string? Detail { get; }
    public string? DetailTip { get; }
    public string? Path { get; }
    public MaterialIconKind Icon { get; }
    public string ToneClass { get; }
    public bool IsBusyTone => ToneClass == "busy";
    public bool IsWarningTone => ToneClass == "warning";
    public bool IsErrorTone => ToneClass == "error";
    public bool IsPausedTone => ToneClass == "paused";
    public bool HasDetail => Detail is not null;
    public string FullTime => _time.ToString("f");

    [ObservableProperty]
    public partial string TimeText { get; set; } = "";

    public void RefreshTime() => TimeText = Format.Relative(_time);

    [RelayCommand]
    void Reveal()
    {
        if (Path is not null)
            ShellLauncher.Reveal(Path);
    }
}

public sealed partial class FailedFileViewModel(FailedFile file) : ViewModelBase
{
    public string FileName { get; } = System.IO.Path.GetFileName(file.Path);
    public string Error { get; } = file.Error ?? Tr.T("Unknown error");
    public string Path { get; } = file.Path;

    public string RetryText { get; } = file.NextAttempt is { } next && next > DateTimeOffset.UtcNow
        ? Tr.T("Retrying in {0}", Format.Duration(next - DateTimeOffset.UtcNow))
        : Tr.T("Retrying soon");

    [RelayCommand]
    void Reveal() => ShellLauncher.Reveal(Path);
}
