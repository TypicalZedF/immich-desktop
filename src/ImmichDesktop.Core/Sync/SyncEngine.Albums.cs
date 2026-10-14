using System.Net;
using ImmichDesktop.Core.Api;

namespace ImmichDesktop.Core.Sync;

public sealed partial class SyncEngine
{
    // ---- Albums -----------------------------------------------------------------------------------

    /// <summary>
    /// Adds uploaded (or already-on-server) files to their folder's album. Runs after every upload batch,
    /// not just at the end, so albums fill up while a big backup is still running, and always uses the
    /// folder settings as they are right now (an album set mid-upload applies immediately).
    /// </summary>
    async Task SyncAlbumsAsync(ImmichClient client, CancellationToken ct)
    {
        foreach (var folder in _folderSnapshot.Where(f => f.Enabled && !string.IsNullOrWhiteSpace(f.AlbumName)))
        {
            var name = folder.AlbumName!.Trim();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var work = _db.GetAlbumWork(folder.Id, AlbumBatchSize);
                if (work.Count == 0)
                    break;

                try
                {
                    var albumId = await GetOrCreateAlbumAsync(client, name, ct);
                    await client.AddAssetsToAlbumAsync(albumId, work.Select(w => w.AssetId).Distinct().ToList(), ct);
                    _db.MarkAlbumDone(work.Select(w => w.Path));
                }
                catch (ImmichApiException ex) when (ex.StatusCode is HttpStatusCode.Forbidden)
                {
                    if (_albumWarnings.Add(name))
                        _activity.Add(ActivityKind.Warning, Loc.T("Couldn't add photos to the album “{0}”. If you signed in with an API key, give it album permissions.", name));
                    break;
                }
                catch (ImmichApiException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
                {
                    // The album was probably deleted on the server; look it up again next cycle.
                    Log.Warn($"Album \"{name}\" update failed", ex);
                    _albumIds.Remove(name);
                    break;
                }
            }
        }
    }

    /// <summary>Album step between upload batches: a hiccup here must never stop the backup itself.</summary>
    async Task SyncAlbumsDuringUploadAsync(ImmichClient client, CancellationToken ct)
    {
        try
        {
            await SyncAlbumsAsync(client, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !NetworkErrors.IsConnectivity(ex) && ex is not ImmichApiException { IsUnauthorized: true })
        {
            Log.Warn("Album update during upload failed; will retry after the batch", ex);
        }
    }

    async Task<string> GetOrCreateAlbumAsync(ImmichClient client, string name, CancellationToken ct)
    {
        if (_albumIds.TryGetValue(name, out var cached))
            return cached;

        var albums = await client.GetAlbumsAsync(ct);
        var album = albums.FirstOrDefault(a => a.AlbumName == name)
                    ?? await client.CreateAlbumAsync(name, null, ct);
        if (!albums.Contains(album))
            _activity.Add(ActivityKind.Info, Loc.T("Created album “{0}”", name));
        _albumIds[name] = album.Id;
        return album.Id;
    }
}
