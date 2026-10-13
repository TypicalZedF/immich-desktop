using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichDesktop.App.Services;
using ImmichDesktop.Core;
using ImmichDesktop.Core.Api;
using ImmichDesktop.Core.Privacy;

namespace ImmichDesktop.App.ViewModels;

/// <summary>
/// Utilities → Suggest for the locked folder: the on-device privacy check's settings, progress, and
/// the pictures it flagged, to move to the locked folder or mark as fine. Pictures are blurred until
/// you choose to see them.
/// </summary>
public sealed partial class PrivacyReviewViewModel : ViewModelBase, IDisposable
{
    const int ThumbnailWidth = 220;

    readonly PhotosViewModel _owner;
    readonly CancellationTokenSource _lifetime = new();
    int _shownSuggestions = -1;
    bool _loading;   // applying saved settings
    bool _reloading; // reading suggestions

    public PrivacyReviewViewModel(PhotosViewModel owner)
    {
        _owner = owner;
        var s = AppServices.Settings.Current;
        _loading = true;
        IsEnabled = s.PrivacyScanEnabled;
        UseGpu = s.PrivacyScanUseGpu;
        OnlyWhenIdle = s.PrivacyScanOnlyWhenIdle;
        _loading = false;
        AppServices.PrivacyScan.StatusChanged += OnStatus;
        Apply(AppServices.PrivacyScan.Status);
        _ = LoadAsync();
    }

    public ObservableCollection<PrivacySuggestionViewModel> Items { get; } = [];

    // ---- Settings ----

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    public partial bool UseGpu { get; set; }

    [ObservableProperty]
    public partial bool OnlyWhenIdle { get; set; }

    public bool OffersGpu => OperatingSystem.IsWindows();

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_loading)
            AppServices.Settings.Update(s => s.PrivacyScanEnabled = value);
    }

    partial void OnUseGpuChanged(bool value)
    {
        if (!_loading)
            AppServices.Settings.Update(s => s.PrivacyScanUseGpu = value);
    }

    partial void OnOnlyWhenIdleChanged(bool value)
    {
        if (!_loading)
            AppServices.Settings.Update(s => s.PrivacyScanOnlyWhenIdle = value);
    }

    // ---- Progress ----

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial bool ShowProgress { get; set; }

    [ObservableProperty]
    public partial bool IsWorking { get; set; }

    void OnStatus(PrivacyScanStatus status) => Dispatcher.UIThread.Post(() =>
    {
        Apply(status);
        if (status.Suggestions != _shownSuggestions && !_reloading)
            _ = LoadAsync();
    });

    void Apply(PrivacyScanStatus status)
    {
        StatusText = Describe(status);
        ShowProgress = status.State is PrivacyScanState.Scanning or PrivacyScanState.Paused && status.Total > 0;
        Progress = status.Total > 0 ? status.Checked / (double)status.Total : 0;
        IsWorking = status.State is PrivacyScanState.Starting or PrivacyScanState.Scanning;
    }

    internal static string Describe(PrivacyScanStatus status)
    {
        var where = status.OnGpu ? Tr.T("on the graphics card") : Tr.T("on the processor");
        return status.State switch
        {
            PrivacyScanState.Off => Tr.T("Off. Turn it on to check your library on this PC."),
            PrivacyScanState.Unavailable => status.Detail ?? Tr.T("Not available on this PC."),
            PrivacyScanState.Starting => Tr.T("Starting…"),
            PrivacyScanState.Scanning => Tr.T("Checking {0} of {1} ({2})", status.Checked.ToString("N0"), status.Total.ToString("N0"), where),
            PrivacyScanState.Paused => Tr.T("{0} · {1} of {2} checked", status.Detail ?? Tr.T("Paused"), status.Checked.ToString("N0"), status.Total.ToString("N0")),
            PrivacyScanState.Done => Tr.T("All {0} photos and videos checked. New ones are checked as they arrive.", status.Total.ToString("N0")),
            PrivacyScanState.Error => Tr.T("Stopped: {0} Trying again soon.", status.Detail ?? ""),
            _ => "",
        };
    }

    // ---- Suggestions ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems), nameof(ShowEmpty))]
    public partial int Count { get; set; }

    public bool HasItems => Count > 0;
    public bool ShowEmpty => Count == 0;

    [ObservableProperty]
    public partial bool ShowPictures { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectionText))]
    public partial int SelectedCount { get; set; }

    public bool HasSelection => SelectedCount > 0;
    public string SelectionText => Tr.T("{0} selected", SelectedCount.ToString("N0"));

    async Task LoadAsync()
    {
        _reloading = true;
        try
        {
            var rows = await Task.Run(AppServices.Database.GetPrivacySuggestions);
            _shownSuggestions = rows.Count;
            var known = Items.ToDictionary(i => i.Id);
            var keep = new List<PrivacySuggestionViewModel>();
            foreach (var (id, kinds, score) in rows)
                keep.Add(known.Remove(id, out var existing) ? existing : new PrivacySuggestionViewModel(this, id, (SensitiveKind)kinds, score));
            foreach (var gone in known.Values)
                gone.Dispose();
            Items.Clear();
            foreach (var item in keep)
                Items.Add(item);
            Count = Items.Count;
            UpdateSelection();
            if (_owner.Thumbnails is { } loader)
                foreach (var item in Items.ToList())
                    _ = item.LoadThumbnailAsync(loader, ThumbnailWidth, _lifetime.Token);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not load privacy suggestions", ex);
        }
        finally
        {
            _reloading = false;
        }
    }

    internal void UpdateSelection() => SelectedCount = Items.Count(i => i.IsSelected);

    IReadOnlyList<PrivacySuggestionViewModel> Chosen => HasSelection ? Items.Where(i => i.IsSelected).ToList() : Items.ToList();

    [RelayCommand]
    void SelectAll()
    {
        var select = SelectedCount < Items.Count;
        foreach (var item in Items)
            item.IsSelected = select;
        UpdateSelection();
    }

    /// <summary>Moves the selected pictures (or all of them) to the locked folder.</summary>
    [RelayCommand]
    async Task MoveToLockedAsync()
    {
        if (_owner.Client is not { } client || Items.Count == 0)
            return;
        var chosen = Chosen;
        try
        {
            if (!(await client.GetAuthStatusAsync(CancellationToken.None)).HasPin)
            {
                _owner.ShowNotice(Tr.T("Set up a PIN code in Immich first (Account settings → PIN code), then come back here."));
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Couldn't check the PIN: {ex.Message}");
        }
        var what = Format.Plural(chosen.Count, "photo");
        if (!await _owner.ConfirmAsync(Tr.T("Move {0} to the locked folder?", what),
                Tr.T("They're hidden from your timeline, albums, search and shared links, and removed from albums. You'll see them in the locked folder after entering your PIN."),
                Tr.T("Move to locked folder"), danger: false))
            return;
        try
        {
            var ids = chosen.Select(i => i.Id).ToList();
            await client.SetVisibilityAsync(ids, "locked", CancellationToken.None);
            AppServices.Database.SetPrivacyDecision(ids, 1);
            Remove(chosen);
            _owner.ShowNotice(Tr.T("Moved {0} to the locked folder", what));
        }
        catch (Exception ex)
        {
            _owner.ShowNotice(Tr.T("Couldn't move to the locked folder. {0}", NetworkErrors.Describe(ex)));
        }
    }

    /// <summary>The selected pictures (or all of them) are fine: don't suggest them again.</summary>
    [RelayCommand]
    void NotSensitive()
    {
        var chosen = Chosen;
        if (chosen.Count == 0)
            return;
        AppServices.Database.SetPrivacyDecision(chosen.Select(i => i.Id).ToList(), 2);
        Remove(chosen);
        _owner.ShowNotice(Tr.T("Won't suggest {0} again", Format.Plural(chosen.Count, "photo")));
    }

    void Remove(IReadOnlyList<PrivacySuggestionViewModel> items)
    {
        foreach (var item in items)
        {
            Items.Remove(item);
            item.Dispose();
        }
        Count = Items.Count;
        _shownSuggestions = Count;
        UpdateSelection();
        AppServices.PrivacyScan.Refresh();
    }

    internal void Open(PrivacySuggestionViewModel item) =>
        _owner.OpenAsset(new TimelineAsset(item.Id, true, false, 1, null, null));

    public void Dispose()
    {
        _lifetime.Cancel();
        AppServices.PrivacyScan.StatusChanged -= OnStatus;
        foreach (var item in Items)
            item.Dispose();
    }
}

public sealed partial class PrivacySuggestionViewModel(PrivacyReviewViewModel owner, string id, SensitiveKind kinds, double score) : ViewModelBase, IDisposable
{
    public string Id { get; } = id;
    public SensitiveKind Kinds { get; } = kinds;
    public double Score { get; } = score;

    /// <summary>Why it was flagged, e.g. "Payment card · Text".</summary>
    public string Reason => string.Join(" · ", Reasons(Kinds));

    internal static IEnumerable<string> Reasons(SensitiveKind kinds)
    {
        if (kinds.HasFlag(SensitiveKind.Explicit)) yield return Tr.T("Nudity");
        if (kinds.HasFlag(SensitiveKind.Suggestive)) yield return Tr.T("Revealing");
        if (kinds.HasFlag(SensitiveKind.PaymentCard)) yield return Tr.T("Payment card");
        if (kinds.HasFlag(SensitiveKind.IdDocument)) yield return Tr.T("ID document");
        if (kinds.HasFlag(SensitiveKind.Financial)) yield return Tr.T("Bank or tax details");
        if (kinds.HasFlag(SensitiveKind.Credentials)) yield return Tr.T("Passwords or codes");
        if (kinds.HasFlag(SensitiveKind.Medical)) yield return Tr.T("Medical");
    }

    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; set; }

    /// <summary>A tiny version, stretched: blurry enough to hide what's in it.</summary>
    [ObservableProperty]
    public partial Bitmap? Blurred { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => owner.UpdateSelection();

    [RelayCommand]
    void ToggleSelect() => IsSelected = !IsSelected;

    [RelayCommand]
    void Open() => owner.Open(this);

    internal async Task LoadThumbnailAsync(ThumbnailLoader loader, int width, CancellationToken ct)
    {
        if (Thumbnail is not null)
            return;
        try
        {
            var blurred = await loader.LoadAsync(Id, AssetImageSize.Thumbnail, 12, ct);
            var bitmap = await loader.LoadAsync(Id, AssetImageSize.Thumbnail, width, ct);
            if (ct.IsCancellationRequested)
            {
                blurred?.Dispose();
                bitmap?.Dispose();
                return;
            }
            Blurred = blurred;
            Thumbnail = bitmap;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Info($"Thumbnail failed for {Id}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Thumbnail?.Dispose();
        Thumbnail = null;
        Blurred?.Dispose();
        Blurred = null;
    }
}
