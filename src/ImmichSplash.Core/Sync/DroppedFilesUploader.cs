using ImmichSplash.Core.Api;

namespace ImmichSplash.Core.Sync;

/// <summary>Where a one-off upload is: files checked (and uploaded or skipped) so far, of the total.</summary>
public sealed record DropProgress(int Done, int Total);

/// <summary>What a one-off upload did.</summary>
public sealed record DropResult(int Uploaded, int AlreadyThere, int Failed);

/// <summary>
/// Uploads files dropped on the app once (not added to the backup folders). The same duplicate check as
/// the backup: each file is hashed and the server is asked first, so nothing is uploaded twice. Can add
/// everything to an album (found by name, or created).
/// </summary>
public static class DroppedFilesUploader
{
    const int BatchSize = 50;

    /// <summary>The photos and videos among dropped files and folders (folders are searched; hidden files skipped).</summary>
    public static List<string> CollectMedia(IEnumerable<string> paths, MediaTypes media)
    {
        var found = new List<string>();
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                if (media.Classify(path) != MediaKind.None)
                    found.Add(path);
            }
            else if (Directory.Exists(path))
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
                found.AddRange(Directory.EnumerateFiles(path, "*", options).Where(f => media.Classify(f) != MediaKind.None));
            }
        }
        // The same file can come twice (on its own and inside a dropped folder), spelled differently.
        return found.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static async Task<DropResult> UploadAsync(ImmichClient client, IReadOnlyList<string> files, string? albumName, BandwidthLimiter limiter,
        IProgress<DropProgress>? progress, CancellationToken ct)
    {
        int uploaded = 0, alreadyThere = 0, failed = 0, done = 0;
        var assetIds = new List<string>();
        foreach (var batch in files.Chunk(BatchSize))
        {
            var checksums = new List<ChecksumItem>();
            foreach (var path in batch)
            {
                try
                {
                    checksums.Add(new ChecksumItem(path, await FileHasher.Sha1HexAsync(path, ct)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"Couldn't read {path} for upload: {ex.Message}");
                    failed++;
                    progress?.Report(new DropProgress(++done, files.Count));
                }
            }
            var checks = (await client.BulkUploadCheckAsync(checksums, ct)).ToDictionary(r => r.Id, StringComparer.Ordinal);
            foreach (var item in checksums)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (checks.TryGetValue(item.Id, out var check) && !check.IsAccepted)
                    {
                        alreadyThere++;
                        if (check.AssetId is { } existing)
                            assetIds.Add(existing);
                    }
                    else
                    {
                        var file = new FileInfo(item.Id);
                        var created = file.CreationTimeUtc < file.LastWriteTimeUtc ? file.CreationTimeUtc : file.LastWriteTimeUtc;
                        var result = await client.UploadAsync(new UploadRequest(file.FullName, file.Name, file.Length, item.Checksum, created, file.LastWriteTimeUtc),
                            limiter, null, ct);
                        if (result.IsDuplicate)
                            alreadyThere++;
                        else
                            uploaded++;
                        assetIds.Add(result.Id);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warn($"Couldn't upload {item.Id}: {ex.Message}");
                    failed++;
                }
                progress?.Report(new DropProgress(++done, files.Count));
            }
        }

        if (!string.IsNullOrWhiteSpace(albumName) && assetIds.Count > 0)
        {
            var name = albumName.Trim();
            var album = (await client.GetAlbumsAsync(ct)).FirstOrDefault(a => a.AlbumName == name)
                        ?? await client.CreateAlbumAsync(name, null, ct);
            foreach (var ids in assetIds.Distinct().Chunk(500))
                await client.AddAssetsToAlbumAsync(album.Id, ids, ct);
        }
        return new DropResult(uploaded, alreadyThere, failed);
    }
}
