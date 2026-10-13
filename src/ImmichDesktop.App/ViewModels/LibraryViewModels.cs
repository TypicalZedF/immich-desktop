using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichDesktop.App.Services;
using ImmichDesktop.Core;
using ImmichDesktop.Core.Api;
using ImmichDesktop.Core.Platform;

namespace ImmichDesktop.App.ViewModels;

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
    public override string Subtitle => Format.Plural(Album.AssetCount, "item") + (Album.Shared ? " · Shared" : "");

    protected override Task<Bitmap?> LoadAsync(ThumbnailLoader loader, int decodeWidth, CancellationToken ct) =>
        Album.AlbumThumbnailAssetId is { } cover ? loader.LoadAsync(cover, AssetImageSize.Thumbnail, decodeWidth, ct) : Task.FromResult<Bitmap?>(null);

    protected override void Open() => Owner.OpenAlbum(Album);
}

public sealed class PersonCardViewModel(PhotosViewModel owner, PersonInfo person) : LibraryCardViewModel(owner)
{
    public PersonInfo Person { get; } = person;
    public override string Title => Person.Name.Length > 0 ? Person.Name : "Unnamed";
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
        new("Never", 0),
        new("1 hour", 60),
        new("1 day", 60 * 24),
        new("7 days", 60 * 24 * 7),
        new("30 days", 60 * 24 * 30),
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
            Error = "Your account or API key isn't allowed to create shared links.";
        }
        catch (Exception ex)
        {
            Log.Warn("Could not create a shared link", ex);
            Error = $"Couldn't create the link. {NetworkErrors.Describe(ex)}";
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
            Error = $"Couldn't load your albums. {NetworkErrors.Describe(ex)}";
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
            _owner.ShowNotice($"Added {What} to “{name}”");
            _owner.OnAlbumsChanged();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not add to album", ex);
            Error = $"Couldn't add to the album. {NetworkErrors.Describe(ex)}";
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    void Close() => _owner.ClosePanel(this);
}
