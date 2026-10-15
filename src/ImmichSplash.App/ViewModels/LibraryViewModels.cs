using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;
using ImmichSplash.Core.Platform;

namespace ImmichSplash.App.ViewModels;

/// <summary>A cover tile on the Albums, People or Places page.</summary>
public abstract partial class LibraryCardViewModel(PhotosViewModel owner) : ViewModelBase, IDisposable
{
    protected PhotosViewModel Owner { get; } = owner;

    public abstract string Title { get; }
    public abstract string Subtitle { get; }

    [ObservableProperty]
    public partial Bitmap? Cover { get; set; }

    internal async Task LoadCoverAsync(ThumbnailLoader loader, int decodeWidth, CancellationToken ct)
    {
        if (Cover is not null)
            return;
        try
        {
            var bitmap = await LoadAsync(loader, decodeWidth, ct);
            if (ct.IsCancellationRequested)
                bitmap?.Dispose();
            else
                Cover = bitmap;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Info($"Cover failed for {Title}: {ex.Message}");
        }
    }

    protected abstract Task<Bitmap?> LoadAsync(ThumbnailLoader loader, int decodeWidth, CancellationToken ct);

    [RelayCommand]
    protected abstract void Open();

    public void Dispose()
    {
        Cover?.Dispose();
        Cover = null;
    }
}

public sealed class AlbumCardViewModel(PhotosViewModel owner, AlbumInfo album) : LibraryCardViewModel(owner)
{
    public AlbumInfo Album { get; } = album;
    public override string Title => Album.AlbumName;
    public override string Subtitle => Format.Plural(Album.AssetCount, "item") +
        (Album.IsSharedWithPeople(AppServices.Settings.Current.UserId) ? Tr.T(" · Shared") : "");

    protected override Task<Bitmap?> LoadAsync(ThumbnailLoader loader, int decodeWidth, CancellationToken ct) =>
        Album.AlbumThumbnailAssetId is { } cover ? loader.LoadAsync(cover, AssetImageSize.Thumbnail, decodeWidth, ct) : Task.FromResult<Bitmap?>(null);

    protected override void Open() => Owner.OpenAlbum(Album);

    public bool CanDelete => Album.IsOwnedBy(AppServices.Settings.Current.UserId);

    public IRelayCommand DeleteCommand => _delete ??= new AsyncRelayCommand(() => Owner.DeleteAlbumAsync(Album));
    IRelayCommand? _delete;
}

public sealed class PersonCardViewModel(PhotosViewModel owner, PersonInfo person) : LibraryCardViewModel(owner)
{
    public PersonInfo Person { get; } = person;
    public override string Title => Person.Name.Length > 0 ? Person.Name : Tr.T("Unnamed");
    public override string Subtitle => "";
    public bool IsNamed => Person.Name.Length > 0;

    protected override Task<Bitmap?> LoadAsync(ThumbnailLoader loader, int decodeWidth, CancellationToken ct) =>
        loader.LoadPersonAsync(Person.Id, decodeWidth, ct);

    protected override void Open() => Owner.OpenPerson(Person);
}

public sealed class PlaceCardViewModel(PhotosViewModel owner, PlaceInfo place) : LibraryCardViewModel(owner)
{
    public PlaceInfo Place { get; } = place;
    public override string Title => Place.City;
    public override string Subtitle => string.Join(", ", new[] { Place.State, Place.Country }.Where(s => !string.IsNullOrWhiteSpace(s)));

    protected override Task<Bitmap?> LoadAsync(ThumbnailLoader loader, int decodeWidth, CancellationToken ct) =>
        loader.LoadAsync(Place.CoverAssetId, AssetImageSize.Thumbnail, decodeWidth, ct);

    protected override void Open() => Owner.OpenPlace(Place);
}

/// <summary>Creates a public link to photos or an album, like the web app's Share dialog.</summary>
public sealed partial class SharePanelViewModel : ViewModelBase
{
    readonly PhotosViewModel _owner;
    readonly IReadOnlyList<string>? _assetIds;
    readonly string? _albumId;

    public SharePanelViewModel(PhotosViewModel owner, string what, IReadOnlyList<string>? assetIds, string? albumId)
    {
        _owner = owner;
        _assetIds = assetIds;
        _albumId = albumId;
        What = what;
        SelectedExpiry = Expiries[0];
    }

    /// <summary>"1 photo", "12 photos" or the album's name.</summary>
    public string What { get; }

    public IReadOnlyList<Option<int>> Expiries { get; } =
    [
        new(Tr.T("Never"), 0),
        new(Tr.T("1 hour"), 60),
        new(Tr.T("1 day"), 60 * 24),
        new(Tr.T("7 days"), 60 * 24 * 7),
        new(Tr.T("30 days"), 60 * 24 * 30),
    ];

    [ObservableProperty]
    public partial Option<int> SelectedExpiry { get; set; }

    [ObservableProperty]
    public partial bool AllowDownload { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLink))]
    public partial string? Link { get; set; }

    public bool HasLink => Link is not null;

    [ObservableProperty]
    public partial bool IsWorking { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial bool Copied { get; set; }

    [RelayCommand]
    async Task CreateAsync()
    {
        if (AppServices.Account.Client is not { } client || IsWorking)
            return;
        IsWorking = true;
        Error = null;
        try
        {
            DateTimeOffset? expires = SelectedExpiry.Value > 0 ? DateTimeOffset.UtcNow.AddMinutes(SelectedExpiry.Value) : null;
            var link = await client.CreateSharedLinkAsync(_assetIds, _albumId, AllowDownload, expires, CancellationToken.None);
            var web = AppServices.Settings.Current.WebUrl ?? client.ApiUrl.ToString();
            var baseUrl = await client.GetShareBaseUrlAsync(web, CancellationToken.None);
            Link = $"{baseUrl}/share/{link.Key}";
            await CopyAsync();
        }
        catch (ImmichApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            Error = Tr.T("Your account or API key isn't allowed to create shared links.");
        }
        catch (Exception ex)
        {
            Log.Warn("Could not create a shared link", ex);
            Error = Tr.T("Couldn't create the link. {0}", NetworkErrors.Describe(ex));
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    async Task CopyAsync()
    {
        if (Link is null)
            return;
        await _owner.CopyTextAsync(Link);
        Copied = true;
    }

    [RelayCommand]
    void OpenLink()
    {
        if (Link is not null)
            ShellLauncher.Open(Link);
    }

    [RelayCommand]
    void Close() => _owner.ClosePanel(this);
}

/// <summary>Adds photos to an existing album or a new one.</summary>
public sealed partial class AlbumPickerViewModel : ViewModelBase
{
    readonly PhotosViewModel _owner;
    readonly IReadOnlyList<string> _assetIds;

    public AlbumPickerViewModel(PhotosViewModel owner, IReadOnlyList<string> assetIds)
    {
        _owner = owner;
        _assetIds = assetIds;
        _ = LoadAsync();
    }

    public string What => Format.Plural(_assetIds.Count, "photo");

    public ObservableCollection<AlbumInfo> Albums { get; } = [];

    [ObservableProperty]
    public partial string NewAlbumName { get; set; } = "";

    [ObservableProperty]
    public partial bool IsWorking { get; set; } = true;

    [ObservableProperty]
    public partial string? Error { get; set; }

    async Task LoadAsync()
    {
        try
        {
            if (AppServices.Account.Client is { } client)
                foreach (var album in (await client.GetAlbumsAsync(CancellationToken.None)).OrderBy(a => a.AlbumName, StringComparer.CurrentCultureIgnoreCase))
                    Albums.Add(album);
        }
        catch (Exception ex)
        {
            Error = Tr.T("Couldn't load your albums. {0}", NetworkErrors.Describe(ex));
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    Task AddTo(AlbumInfo album) => RunAsync(async client =>
    {
        await client.AddAssetsToAlbumAsync(album.Id, _assetIds, CancellationToken.None);
        return album.AlbumName;
    });

    [RelayCommand]
    Task Create()
    {
        var name = NewAlbumName.Trim();
        if (name.Length == 0)
            return Task.CompletedTask;
        return RunAsync(async client =>
        {
            await client.CreateAlbumAsync(name, _assetIds, CancellationToken.None);
            return name;
        });
    }

    async Task RunAsync(Func<ImmichClient, Task<string>> action)
    {
        if (AppServices.Account.Client is not { } client || IsWorking)
            return;
        IsWorking = true;
        Error = null;
        try
        {
            var name = await action(client);
            _owner.ClosePanel(this);
            _owner.ShowNotice(Tr.T("Added {0} to “{1}”", What, name));
            _owner.OnAlbumsChanged();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not add to album", ex);
            Error = Tr.T("Couldn't add to the album. {0}", NetworkErrors.Describe(ex));
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    void Close() => _owner.ClosePanel(this);
}

/// <summary>One of your shared links on the Sharing page.</summary>
public sealed partial class SharedLinkViewModel(PhotosViewModel owner, SharedLinkSummary link, string url) : ViewModelBase, IDisposable
{
    public SharedLinkSummary Link { get; } = link;
    public string Url { get; } = url;

    public string Title => Link.Title.Length > 0 ? Link.Title
        : Link.IsAlbum ? Tr.T("Shared album") : Format.Plural(Link.AssetCount, "photo");

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (Link.IsAlbum)
                parts.Add(Format.Plural(Link.AssetCount, "item"));
            if (Link.CreatedAt is { } created)
                parts.Add(Tr.T("Created {0}", Format.Relative(created)));
            parts.Add(Link.ExpiresAt is not { } expires ? Tr.T("Never expires")
                : Link.IsExpired ? Tr.T("Expired")
                : Tr.T("Expires {0}", expires.ToLocalTime().ToString("d", System.Globalization.CultureInfo.CurrentCulture)));
            return string.Join(" · ", parts);
        }
    }

    public bool IsExpired => Link.IsExpired;

    [ObservableProperty]
    public partial Bitmap? Cover { get; set; }

    internal async Task LoadCoverAsync(ThumbnailLoader loader, CancellationToken ct)
    {
        if (Link.CoverAssetId is not { } id)
            return;
        try
        {
            Cover = await loader.LoadAsync(id, AssetImageSize.Thumbnail, 160, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Info($"Shared link cover failed: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    async Task CopyAsync()
    {
        await owner.CopyTextAsync(Url);
        owner.ShowNotice(Tr.T("Link copied"));
    }

    [RelayCommand]
    void Open() => ShellLauncher.Open(Url);

    [RelayCommand]
    Task DeleteAsync() => owner.DeleteSharedLinkAsync(this);

    public void Dispose()
    {
        Cover?.Dispose();
        Cover = null;
    }
}

/// <summary>Someone sharing their library with you; opens their photos.</summary>
public sealed partial class PartnerViewModel(PhotosViewModel owner, PartnerInfo partner) : ViewModelBase
{
    public PartnerInfo Partner { get; } = partner;
    public string Initial => Partner.Name.Length > 0 ? char.ToUpperInvariant(Partner.Name[0]).ToString() : "?";

    [RelayCommand]
    void Open() => owner.OpenPartner(Partner);
}

/// <summary>Asks for the account's PIN to unlock the locked folder (e.g. before moving photos into it).</summary>
public sealed partial class PinPanelViewModel(PhotosViewModel owner) : ViewModelBase
{
    readonly TaskCompletionSource<bool> _result = new();

    public Task<bool> Result => _result.Task;

    [ObservableProperty]
    public partial string Pin { get; set; } = "";

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial bool IsWorking { get; set; }

    [RelayCommand]
    async Task UnlockAsync()
    {
        if (AppServices.Account.Client is not { } client || Pin.Length == 0 || IsWorking)
            return;
        IsWorking = true;
        Error = null;
        try
        {
            await client.UnlockSessionAsync(Pin, CancellationToken.None);
            owner.ClosePanel(this);
            _result.TrySetResult(true);
        }
        catch (ImmichApiException ex) when ((int)ex.StatusCode is >= 400 and < 500)
        {
            Error = Tr.T("That PIN isn't right.");
        }
        catch (Exception ex)
        {
            Error = Tr.T("Couldn't unlock. {0}", NetworkErrors.Describe(ex));
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    void Close()
    {
        owner.ClosePanel(this);
        _result.TrySetResult(false);
    }
}
