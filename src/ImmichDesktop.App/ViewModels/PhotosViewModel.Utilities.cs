using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichDesktop.Core;
using ImmichDesktop.Core.Api;

namespace ImmichDesktop.App.ViewModels;

/// <summary>Utilities, like the Immich web app's: review duplicates, find large files, and what on this PC isn't backed up yet.</summary>
public sealed partial class PhotosViewModel
{
    /// <summary>Files from this size up count as large (the web app's default).</summary>
    const long LargeFileBytes = 10L * 1024 * 1024;
    const int LargeFilesShown = 250;

    public bool ShowUtilities => Drill is null && Duplicates is null && Page == LibraryPage.Utilities;

    /// <summary>The duplicates review, while it's open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUtilities), nameof(ShowDuplicates), nameof(IsDrilledIn), nameof(Heading))]
    public partial DuplicatesViewModel? Duplicates { get; set; }

    public bool ShowDuplicates => Duplicates is not null;

    [ObservableProperty]
    public partial string DuplicatesText { get; set; } = "";

    [ObservableProperty]
    public partial string NotBackedUpText { get; set; } = "";

    async Task LoadUtilitiesAsync()
    {
        IsLoading = false;
        DuplicatesText = Tr.T("Checking…");
        try
        {
            NotBackedUpText = (await LocalOnlyAssetsAsync(_lifetime.Token)).Count is var n and > 0
                ? Format.Plural(n, Tr.T("file on this PC isn't in Immich yet"), Tr.T("files on this PC aren't in Immich yet"))
                : Tr.T("Everything in your backup folders is in Immich.");
        }
        catch (Exception ex)
        {
            Log.Warn("Could not count files waiting for backup", ex);
        }
        if (Client is not { } client)
            return;
        try
        {
            OnDuplicatesChanged((await client.GetDuplicatesAsync(_lifetime.Token)).Count);
        }
        catch (ImmichApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            DuplicatesText = Tr.T("Needs a newer Immich server.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DuplicatesText = Tr.T("Couldn't check. {0}", NetworkErrors.Describe(ex));
        }
    }

    internal void OnDuplicatesChanged(int groups) =>
        DuplicatesText = groups > 0
            ? Format.Plural(groups, Tr.T("group of look-alike photos to review"), Tr.T("groups of look-alike photos to review"))
            : Tr.T("No duplicates found.");

    [RelayCommand]
    void OpenDuplicates()
    {
        Duplicates?.Dispose();
        Duplicates = new DuplicatesViewModel(this);
    }

    [RelayCommand]
    void OpenLargeFiles() => OpenDrill(new GridQuery(Tr.T("Large files"), async (client, ct) =>
    {
        var files = await client.GetLargeFilesAsync(LargeFileBytes, LargeFilesShown, ct);
        return BucketState.FromAssets(files, Tr.T("Largest first"));
    }, ShowSizes: true));

    [RelayCommand]
    void OpenNotBackedUp() => OpenDrill(new GridQuery(Tr.T("Not backed up yet"), async (_, ct) => BucketState.FromAssets(await LocalOnlyAssetsAsync(ct))));

    /// <summary>Opens one photo in the viewer (e.g. from the duplicates review), without editing actions.</summary>
    internal void OpenAsset(TimelineAsset asset) =>
        Viewer = new PhotoViewerViewModel(this, BucketState.FromAssets([asset], "")[0], 0, asset, null, readOnly: true);

    internal Task<bool> ConfirmAsync(string title, string message, string action, bool danger) =>
        _main?.ConfirmAsync(title, message, action, danger: danger) ?? Task.FromResult(false);
}
