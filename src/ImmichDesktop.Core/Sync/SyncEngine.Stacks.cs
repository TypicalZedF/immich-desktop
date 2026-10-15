using System.Net;
using ImmichDesktop.Core.Api;
using ImmichDesktop.Core.Settings;

namespace ImmichDesktop.Core.Sync;

public sealed partial class SyncEngine
{
    // ---- RAW+JPEG stacks -------------------------------------------------------------------------

    bool _stackWarned;

    /// <summary>
    /// When chosen in Settings: puts each backed-up RAW photo in a stack with its JPEG, so the timeline
    /// shows the shot once. A problem here never stops the backup.
    /// </summary>
    async Task StackRawPairsAsync(ImmichClient client, AppSettings settings, CancellationToken ct)
    {
        if (!settings.StackRawWithJpeg)
            return;
        var stacked = 0;
        try
        {
            var (pairs, notApplicable) = RawPairs.Find(_db.GetUnstackedUploads());
            _db.MarkStacked(notApplicable);
            foreach (var pair in pairs)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await client.CreateStackAsync(pair.AssetIds, ct);
                    stacked++;
                }
                catch (ImmichApiException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
                {
                    // Deleted on the server, or the server won't stack these: don't try again.
                    Log.Warn("Couldn't stack " + Path.GetFileName(pair.Paths[0]), ex);
                }
                _db.MarkStacked(pair.Paths);
            }
        }
        catch (ImmichApiException ex) when (ex.StatusCode is HttpStatusCode.Forbidden)
        {
            if (!_stackWarned)
                _activity.Add(ActivityKind.Warning, Loc.T("Couldn't stack RAW photos with their JPEGs. If you signed in with an API key, give it stack permissions."));
            _stackWarned = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !NetworkErrors.IsConnectivity(ex) && ex is not ImmichApiException { IsUnauthorized: true })
        {
            Log.Warn("Stacking RAW photos failed; will retry next time", ex);
        }
        if (stacked > 0)
            _activity.Add(ActivityKind.Info, Loc.T("Stacked {0} RAW photos with their JPEGs", stacked));
    }
}
