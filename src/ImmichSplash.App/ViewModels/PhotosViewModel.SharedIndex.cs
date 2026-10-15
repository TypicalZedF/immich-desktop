using ImmichSplash.Core;
using ImmichSplash.Core.Api;

namespace ImmichSplash.App.ViewModels;

/// <summary>
/// Which photos have a public link of their own, so the grid can mark them and the viewer can list (and
/// manage) the links. Read once when a grid loads and again whenever a link is made or deleted here.
/// </summary>
public sealed partial class PhotosViewModel
{
    Dictionary<string, List<SharedLinkSummary>> _linksByAsset = [];
    string? _shareBaseUrl;

    internal bool IsAssetShared(string assetId) => _linksByAsset.ContainsKey(assetId);

    /// <summary>The links that share this photo by itself (not as part of an album), newest first.</summary>
    internal IReadOnlyList<SharedLinkViewModel> LinksFor(string assetId) =>
        _linksByAsset.TryGetValue(assetId, out var links) && _shareBaseUrl is { } baseUrl
            ? links.Select(l => new SharedLinkViewModel(this, l, $"{baseUrl}/share/{l.Key}")).ToList()
            : [];

    internal async Task LoadShareIndexAsync()
    {
        if (Client is not { } client || IsPicker)
            return;
        try
        {
            var links = await client.GetSharedLinksAsync(_lifetime.Token);
            _shareBaseUrl ??= await client.GetShareBaseUrlAsync(AppServices.Settings.Current.WebUrl ?? client.ApiUrl.ToString(), _lifetime.Token);
            var index = new Dictionary<string, List<SharedLinkSummary>>();
            foreach (var link in links.Where(l => !l.IsAlbum && !l.IsExpired))
                foreach (var id in link.AssetIds ?? [])
                {
                    if (!index.TryGetValue(id, out var list))
                        index[id] = list = [];
                    list.Add(link);
                }
            _linksByAsset = index;
            OnShareIndexChanged();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Info($"Shared photos unavailable: {ex.Message}");
        }
    }

    /// <summary>A link was made or deleted: the badges and the viewer's list follow.</summary>
    void OnShareIndexChanged()
    {
        foreach (var cell in VisibleCells)
            cell.RefreshShared();
        Viewer?.RefreshSharing();
    }

    internal void OnLinkCreated() => _ = LoadShareIndexAsync();

    void ForgetLink(string linkId)
    {
        var changed = false;
        foreach (var (id, list) in _linksByAsset.ToList())
            if (list.RemoveAll(l => l.Id == linkId) > 0)
            {
                changed = true;
                if (list.Count == 0)
                    _linksByAsset.Remove(id);
            }
        if (changed)
            OnShareIndexChanged();
    }
}
