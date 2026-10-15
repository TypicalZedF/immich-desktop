using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;

namespace ImmichSplash.App.ViewModels;

public sealed partial class PhotosViewModel
{
    // ---- What the viewer's "..." menu does with one photo ----

    /// <summary>Archives, un-archives or locks away photos. Returns whether it worked.</summary>
    internal async Task<bool> MoveAssetsAsync(IReadOnlyList<string> ids, string visibility, string done)
    {
        if (Client is not { } client)
            return false;
        if (visibility == "locked" && !await EnsureUnlockedAsync())
            return false;
        try
        {
            await client.SetVisibilityAsync(ids, visibility, CancellationToken.None);
            ShowNotice(done);
            _ = LoadGridAsync();
            return true;
        }
        catch (Exception ex)
        {
            ShowNotice(Tr.T("Couldn't move the photo. {0}", NetworkErrors.Describe(ex)));
            return false;
        }
    }

    /// <summary>Asks the server to redo faces, metadata or thumbnails for a photo.</summary>
    internal async Task RunJobAsync(string assetId, string job, string started)
    {
        if (Client is not { } client)
            return;
        try
        {
            await client.RunAssetJobAsync([assetId], job, CancellationToken.None);
            ShowNotice(started);
        }
        catch (Exception ex)
        {
            ShowNotice(Tr.T("Couldn't start that on the server. {0}", NetworkErrors.Describe(ex)));
        }
    }

    /// <summary>Opens the editor (turn, flip, crop) for a photo.</summary>
    internal void OpenEditor(TimelineAsset asset)
    {
        if (_main is null)
            return;
        Viewer = null;
        _main.ShowOverlay(new PhotoEditorViewModel(this, _main, asset));
    }

    /// <summary>The server edited a photo: show the new version.</summary>
    internal void OnAssetEdited(string assetId, string message)
    {
        Thumbnails?.Forget(assetId);
        ShowNotice(message);
        _ = LoadGridAsync();
        _ = RefreshEditedAsync(assetId);
    }

    /// <summary>
    /// Immich makes the edited thumbnails in a background job, so just after saving it can still hand out the old
    /// ones: fetch that photo's pictures again a little later (twice, for a busy server).
    /// </summary>
    async Task RefreshEditedAsync(string assetId)
    {
        foreach (var wait in new[] { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10) })
        {
            try
            {
                await Task.Delay(wait, _lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            Thumbnails?.Forget(assetId);
            foreach (var cell in VisibleCells.Where(c => c.Asset?.Id == assetId && c.Thumbnail is not null).ToList())
                cell.ReloadThumbnail();
        }
    }

    /// <summary>Shows photos that look like this one.</summary>
    internal void ShowSimilar(string assetId)
    {
        OpenDrill(new GridQuery(Tr.T("Similar photos"), async (c, ct) =>
        {
            try
            {
                return BucketState.FromAssets(await c.SimilarSearchAsync(assetId, 200, ct), Tr.T("Most similar first"));
            }
            catch (ImmichApiException ex) when ((int)ex.StatusCode is >= 400 and < 500)
            {
                ShowNotice(Tr.T("Couldn't find similar photos. The server needs machine learning turned on for that."));
                return [];
            }
        }));
    }

    /// <summary>Makes a photo your Immich profile picture.</summary>
    internal async Task SetAsProfilePictureAsync(string assetId)
    {
        if (Client is not { } client)
            return;
        try
        {
            var bytes = await client.GetAssetImageAsync(assetId, AssetImageSize.Preview, CancellationToken.None);
            await client.SetProfileImageAsync(bytes, "profile.jpg", "image/jpeg", CancellationToken.None);
            await AppServices.Profile.RefreshAsync();
            ShowNotice(Tr.T("Profile picture updated"));
        }
        catch (Exception ex)
        {
            ShowNotice(Tr.T("Couldn't set the profile picture. {0}", NetworkErrors.Describe(ex)));
        }
    }

    // ---- "View in timeline": the Photos page, scrolled to when a photo was taken ----

    DateTime? _scrollToDate;

    /// <summary>Scrolls the grid to the group a photo taken at <paramref name="taken"/> belongs to (once the rows exist).</summary>
    internal void ScrollToDate(DateTime taken)
    {
        _scrollToDate = taken;
        ApplyPendingScroll();
    }

    void ApplyPendingScroll()
    {
        if (_scrollToDate is not { } taken || _buckets.Count == 0 || Rows.Count == 0)
            return;
        _scrollToDate = null;
        var group = _buckets.FindIndex(b => b.Day is { } day && day == taken.Date);
        if (group < 0)
            group = _buckets.FindIndex(b => b.Day is null && b.Month is { } m && m.Year == taken.Year && m.Month == taken.Month);
        if (group < 0)
            return;
        var seen = -1;
        for (var i = 0; i < Rows.Count; i++)
        {
            if (Rows[i] is MonthHeaderViewModel && ++seen == group)
            {
                ScrollToRowRequested?.Invoke(i);
                return;
            }
        }
    }

    /// <summary>Opens the Photos page at the day or month a photo was taken.</summary>
    internal void ShowInTimeline(TimelineAsset asset)
    {
        if (_main?.Content is not ShellViewModel shell)
            return;
        Viewer = null;
        shell.Navigate(ShellPage.Photos);
        if (shell.CurrentPage is PhotosViewModel photos
            && DateTimeOffset.TryParse(asset.FileCreatedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
            photos.ScrollToDate(date.ToLocalTime().DateTime);
    }
}
