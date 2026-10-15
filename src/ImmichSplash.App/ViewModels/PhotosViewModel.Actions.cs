using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;

namespace ImmichSplash.App.ViewModels;

public sealed partial class PhotosViewModel
{
    // ---- Actions (selection and viewer) ----

    /// <summary>A small panel over the page: sharing or adding to an album.</summary>
    [ObservableProperty]
    public partial ViewModelBase? Panel { get; set; }

    internal void ClosePanel(ViewModelBase panel)
    {
        if (ReferenceEquals(Panel, panel))
            Panel = null;
    }

    partial void OnPanelChanged(ViewModelBase? oldValue, ViewModelBase? newValue)
    {
        // A PIN prompt closed some other way (Esc) counts as cancelled.
        if (oldValue is PinPanelViewModel pin && newValue is null)
            pin.CloseCommand.Execute(null);
    }

    internal void ShowShare(IReadOnlyList<string> assetIds) =>
        Panel = new SharePanelViewModel(this, Format.Plural(assetIds.Count, "photo"), assetIds, null);

    [RelayCommand]
    void ShareAlbum()
    {
        if (Drill?.Album is { } album)
            ShareAlbumFromCard(album);
    }

    [RelayCommand]
    Task DeleteOpenAlbumAsync() => Drill?.Album is { } album ? DeleteAlbumAsync(album) : Task.CompletedTask;

    /// <summary>Saves the whole open album (originals) into a folder named after it.</summary>
    [RelayCommand]
    async Task DownloadOpenAlbumAsync()
    {
        if (Drill?.Album is not { } album || _main is null)
            return;
        var parent = await _main.PickFolderAsync(Tr.T("Save the album “{0}” to…", album.AlbumName));
        if (parent is null)
            return;
        var ids = new List<string>();
        foreach (var bucket in _buckets)
            ids.AddRange((await bucket.GetAssetsAsync(_lifetime.Token)).Where(a => a.LocalPath is null).Select(a => a.Id));
        var folder = Directory.CreateDirectory(Path.Combine(parent, string.Concat(album.AlbumName.Split(Path.GetInvalidFileNameChars())).Trim() is { Length: > 0 } name ? name : "Album")).FullName;
        await DownloadToFolderAsync(ids.Distinct().ToList(), folder);
    }

    /// <summary>
    /// Deletes an album (its photos stay). If a backed-up folder adds its photos to this album, the
    /// next backup would just create it again, so deleting it also stops that folder's album.
    /// </summary>
    internal async Task DeleteAlbumAsync(AlbumInfo album)
    {
        if (Client is not { } client || _main is null)
            return;
        // Same matching as the backup uses to find a folder's album.
        var folders = AppServices.Settings.Current.Folders
            .Where(f => !string.IsNullOrWhiteSpace(f.AlbumName) && f.AlbumName.Trim() == album.AlbumName)
            .ToList();
        var message = Tr.T("The photos and videos in it stay in your library. This can't be undone.");
        var action = Tr.T("Delete album");
        if (folders.Count > 0)
        {
            var names = string.Join(", ", folders.Select(f => $"“{Path.GetFileName(f.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}”"));
            message = Tr.T("This album is filled by backing up {0}. Deleting it also stops that folder from adding photos to an album; otherwise the next backup would just create it again. You can turn the album back on in Folders.", names)
                + "\n\n" + message;
            action = Tr.T("Delete album and stop the folder's album");
        }
        if (!await _main.ConfirmAsync(Tr.T("Delete “{0}”?", album.AlbumName), message, action, danger: true))
            return;
        try
        {
            if (folders.Count > 0)
            {
                var ids = folders.Select(f => f.Id).ToHashSet();
                AppServices.Settings.Update(s =>
                {
                    foreach (var f in s.Folders.Where(f => ids.Contains(f.Id)))
                        f.AlbumName = null;
                });
            }
            await client.DeleteAlbumAsync(album.Id, CancellationToken.None);
            ShowNotice(Tr.T("Deleted the album “{0}”", album.AlbumName));
            if (Drill?.Album?.Id == album.Id)
                Back();
            if (Page == LibraryPage.Albums)
                _ = LoadAlbumsAsync();
            else if (Page == LibraryPage.Sharing)
                _ = LoadSharingAsync();
        }
        catch (Exception ex)
        {
            ShowNotice(Tr.T("Couldn't delete the album. {0}", NetworkErrors.Describe(ex)));
        }
    }

    internal void ShowAlbumPicker(IReadOnlyList<string> assetIds) => Panel = new AlbumPickerViewModel(this, assetIds);

    internal async Task<bool> SetFavoriteAsync(IReadOnlyList<string> ids, bool favorite)
    {
        if (Client is not { } client || ids.Count == 0)
            return false;
        try
        {
            await client.SetFavoriteAsync(ids, favorite, CancellationToken.None);
            var set = ids.ToHashSet();
            foreach (var bucket in _buckets)
                foreach (var id in set)
                    bucket.Replace(id, a => a with { IsFavorite = favorite });
            foreach (var cell in VisibleCells.Where(c => c.Asset is { } a && set.Contains(a.Id)))
                cell.Asset = cell.Asset! with { IsFavorite = favorite };
            if (ids.Count > 1)
                ShowNotice(favorite ? Tr.T("Added {0} to favorites", Format.Plural(ids.Count, "photo")) : Tr.T("Removed from favorites"));
            return true;
        }
        catch (Exception ex)
        {
            ShowNotice(Tr.T("Couldn't change favorites. {0}", NetworkErrors.Describe(ex)));
            return false;
        }
    }

    internal async Task DownloadAsync(IReadOnlyList<string> ids)
    {
        if (Client is not { } client || _main is null || ids.Count == 0)
            return;
        try
        {
            if (ids.Count == 1)
            {
                var details = await client.GetAssetAsync(ids[0], CancellationToken.None);
                var path = await _main.PickSaveFileAsync(Tr.T("Save photo"), details.OriginalFileName ?? $"{ids[0]}.jpg");
                if (path is null)
                    return;
                await using (var file = File.Create(path))
                    await client.DownloadOriginalAsync(ids[0], file, CancellationToken.None);
                ShowNotice(Tr.T("Saved {0}", Path.GetFileName(path)));
                return;
            }

            var folder = await _main.PickFolderAsync(Tr.T("Save {0} photos to…", ids.Count));
            if (folder is null)
                return;
            await DownloadToFolderAsync(ids, folder);
        }
        catch (Exception ex)
        {
            Log.Warn("Download failed", ex);
            ShowNotice(Tr.T("Couldn't download. {0}", NetworkErrors.Describe(ex)));
        }
    }

    /// <summary>Saves originals into a folder (names kept; a number is added if one is taken).</summary>
    async Task DownloadToFolderAsync(IReadOnlyList<string> ids, string folder)
    {
        if (Client is not { } client || ids.Count == 0)
            return;
        var done = 0;
        try
        {
            foreach (var id in ids)
            {
                ShowNotice(Tr.T("Downloading {0} of {1}…", done + 1, ids.Count), sticky: true);
                var details = await client.GetAssetAsync(id, _lifetime.Token);
                var path = UniquePath(folder, details.OriginalFileName ?? $"{id}.jpg");
                await using (var file = File.Create(path))
                    await client.DownloadOriginalAsync(id, file, _lifetime.Token);
                done++;
            }
            ShowNotice(Tr.T("Saved {0} to {1}", Format.Plural(done, "photo"), Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar))));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("Download failed", ex);
            ShowNotice(Tr.T("Couldn't download ({0} of {1} saved). {2}", done, ids.Count, NetworkErrors.Describe(ex)));
        }
    }

    static string UniquePath(string folder, string name)
    {
        name = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(folder, name);
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
        return path;
    }

    internal async Task<bool> TrashAsync(IReadOnlyList<string> ids)
    {
        if (Client is not { } client || _main is null || ids.Count == 0)
            return false;
        var what = Format.Plural(ids.Count, "photo");
        if (!await _main.ConfirmAsync(Tr.T("Move {0} to the trash?", what),
                Tr.T("They're removed from your library on every device. You can restore them from the trash in Immich for a while."),
                Tr.T("Move to trash"), danger: true))
            return false;
        try
        {
            await client.TrashAssetsAsync(ids, CancellationToken.None);
            ShowNotice(Tr.T("Moved {0} to the trash", what));
            _ = LoadGridAsync();
            return true;
        }
        catch (Exception ex)
        {
            ShowNotice(Tr.T("Couldn't move to the trash. {0}", NetworkErrors.Describe(ex)));
            return false;
        }
    }

    /// <summary>Files only on this PC use "local:" plus their path as their id.</summary>
    internal static string? LocalPathOf(string assetId) => assetId.StartsWith("local:", StringComparison.Ordinal) ? assetId[6..] : null;

    internal Task CopyTextAsync(string text) => _main?.CopyTextAsync(text) ?? Task.CompletedTask;

    /// <summary>Copies a photo to the clipboard as a picture (full size, capped so it stays pasteable).</summary>
    internal async Task CopyImageAsync(string assetId)
    {
        if (Client is not { } client)
            return;
        ShowNotice(Tr.T("Copying…"), sticky: true);
        try
        {
            var data = LocalPathOf(assetId) is { } path ? await File.ReadAllBytesAsync(path) : await DownloadForCopyAsync(client, assetId);
            var window = _main?.GetWindowHandle?.Invoke() ?? IntPtr.Zero;
            await Task.Run(() =>
            {
                using var bitmap = Images.DecodeUpright(data, 4096);
                ImageClipboard.Copy(bitmap, window);
            });
            ShowNotice(Tr.T("Copied to clipboard"));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not copy the picture", ex);
            ShowNotice(Tr.T("Couldn't copy the picture. {0}", NetworkErrors.Describe(ex)));
        }
    }

    static async Task<byte[]> DownloadForCopyAsync(ImmichClient client, string assetId)
    {
        try
        {
            var size = AppServices.Network.IsMetered ? AssetImageSize.Preview : AssetImageSize.Fullsize;
            return await client.GetAssetImageAsync(assetId, size, CancellationToken.None);
        }
        catch (ImmichApiException)
        {
            return await client.GetAssetImageAsync(assetId, AssetImageSize.Preview, CancellationToken.None);
        }
    }
}
