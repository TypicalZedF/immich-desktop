namespace ImmichDesktop.Core.Api;

public enum AuthKind
{
    AccessToken,
    ApiKey,
}

public sealed record AuthCredential(AuthKind Kind, string Secret);

public sealed record ServerVersion(int Major, int Minor, int Patch)
{
    public override string ToString() => $"v{Major}.{Minor}.{Patch}";
}

/// <summary>A reachable Immich server: the web address users know and the API base the app talks to.</summary>
public sealed record ServerInfo(Uri WebUrl, Uri ApiUrl, ServerVersion Version)
{
    public string Host => WebUrl.IsDefaultPort ? WebUrl.Host : $"{WebUrl.Host}:{WebUrl.Port}";
}

public sealed record UserInfo(string Id, string Name, string Email, long? QuotaSizeInBytes = null, long? QuotaUsageInBytes = null,
    string? ProfileImagePath = null, string? AvatarColor = null)
{
    public bool HasProfileImage => !string.IsNullOrEmpty(ProfileImagePath);
}

/// <summary>How much space is used: the user's quota when one is set, otherwise the server's disk.</summary>
public sealed record StorageInfo(long Used, long Total)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp(Used / (double)Total, 0, 1);
}

public sealed record ChecksumItem(string Id, string Checksum);

public sealed record BulkCheckResult(string Id, string Action, string? Reason, string? AssetId)
{
    public bool IsAccepted => Action == "accept";
    public bool IsDuplicate => Action == "reject" && Reason == "duplicate";
}

public sealed record UploadResult(string Id, string Status)
{
    public bool IsDuplicate => Status == "duplicate";
}

public sealed record AlbumInfo(string Id, string AlbumName, string? AlbumThumbnailAssetId = null, int AssetCount = 0, bool Shared = false);

/// <summary>Narrows the timeline to one album, one person or favorites (empty = the whole library).</summary>
public sealed record TimelineFilter(string? AlbumId = null, string? PersonId = null, bool Favorites = false)
{
    public static TimelineFilter All { get; } = new();
}

/// <summary>A recognized face, as on the web app's People page.</summary>
public sealed record PersonInfo(string Id, string Name, bool IsHidden);

/// <summary>A city with photos (and one of them, for a cover).</summary>
public sealed record PlaceInfo(string City, string? State, string? Country, string CoverAssetId);

/// <summary>Where a photo was taken.</summary>
public sealed record MapMarker(string Id, double Lat, double Lon, string? City, string? Country);

/// <summary>Details shown in the photo viewer's info panel.</summary>
public sealed record AssetDetails(
    string Id,
    string? OriginalFileName,
    string? FileCreatedAt,
    bool IsFavorite,
    bool IsImage,
    long? FileSize,
    int? Width,
    int? Height,
    string? Camera,
    string? Lens,
    string? Exposure,
    string? Place,
    string? Description,
    IReadOnlyList<string> People);

/// <summary>A public link to photos or an album.</summary>
public sealed record SharedLinkInfo(string Id, string Key);

public sealed record MediaTypesInfo(string[]? Image, string[]? Video);

/// <summary>Everything needed to upload one file.</summary>
public sealed record UploadRequest(
    string FilePath,
    string FileName,
    long Size,
    string Checksum,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc);

/// <summary>Which sign-in methods the server offers.</summary>
public sealed record LoginOptions(bool PasswordLogin, bool OAuth, bool OAuthAutoLaunch, string OAuthButtonText);

/// <summary>One month (or other period) of the library timeline.</summary>
public sealed record TimeBucket(string Key, int Count);

/// <summary>A photo or video in the timeline.</summary>
public sealed record TimelineAsset(string Id, bool IsImage, bool IsFavorite, double Ratio, long? DurationMs, string? FileCreatedAt);

/// <summary>Image sizes the server can render for an asset.</summary>
public enum AssetImageSize
{
    Thumbnail,
    Preview,

    /// <summary>Full resolution in a format any app can show (the original for JPEGs). Newer servers only.</summary>
    Fullsize,
}

// Wire DTOs
internal sealed record LoginRequestDto(string Email, string Password);
internal sealed record LoginResponseDto(string AccessToken, string UserId, string UserEmail, string Name);
internal sealed record PingResponseDto(string? Res);
internal sealed record VersionResponseDto(int Major, int Minor, int Patch);
internal sealed record UserResponseDto(string Id, string Name, string Email, long? QuotaSizeInBytes, long? QuotaUsageInBytes,
    string? ProfileImagePath, string? AvatarColor);
internal sealed record ServerStorageDto(long DiskSizeRaw, long DiskUseRaw);
internal sealed record BulkCheckRequestDto(IReadOnlyList<ChecksumItem> Assets);
internal sealed record BulkCheckResponseDto(List<BulkCheckResult> Results);
internal sealed record CreateAlbumRequestDto(string AlbumName, IReadOnlyList<string>? AssetIds);
internal sealed record BulkIdsRequestDto(IReadOnlyList<string> Ids);
internal sealed record ServerFeaturesDto(bool PasswordLogin, bool Oauth, bool OauthAutoLaunch);
internal sealed record ServerConfigDto(string? OauthButtonText);
internal sealed record OAuthAuthorizeRequestDto(string RedirectUri, string CodeChallenge, string State);
internal sealed record OAuthAuthorizeResponseDto(string Url);
internal sealed record OAuthCallbackRequestDto(string Url, string State, string CodeVerifier);
internal sealed record TimeBucketDto(string TimeBucket, int Count);
internal sealed record TimeBucketAssetsDto(
    List<string> Id,
    List<bool> IsImage,
    List<bool>? IsFavorite,
    List<double>? Ratio,
    List<System.Text.Json.JsonElement>? Duration,
    List<string>? FileCreatedAt);
internal sealed record WellKnownDto(WellKnownApiDto? Api);
internal sealed record PeopleResponseDto(List<PersonDto>? People, bool? HasNextPage);
internal sealed record PersonDto(string Id, string? Name, bool IsHidden);
internal sealed record ExifDto(
    string? Make, string? Model, string? LensModel, double? FNumber, string? ExposureTime, double? Iso, double? FocalLength,
    string? City, string? State, string? Country, int? ExifImageWidth, int? ExifImageHeight, long? FileSizeInByte, string? Description);
internal sealed record AssetPersonDto(string? Name);
internal sealed record AssetDto(
    string Id, string? Type, string? OriginalFileName, string? FileCreatedAt, bool IsFavorite, string? Duration,
    ExifDto? ExifInfo, List<AssetPersonDto>? People);
internal sealed record SearchAssetsDto(List<AssetDto> Items, string? NextPage);
internal sealed record SearchResponseDto(SearchAssetsDto Assets);
internal sealed record SmartSearchRequestDto(string Query, int Page, int Size);
internal sealed record MetadataSearchRequestDto(string? City, string? OriginalFileName, int Page, int Size, string? Order = "desc");
internal sealed record MapMarkerDto(string Id, double Lat, double Lon, string? City, string? Country);
internal sealed record FavoriteRequestDto(IReadOnlyList<string> Ids, bool IsFavorite);
internal sealed record DeleteAssetsRequestDto(IReadOnlyList<string> Ids, bool Force);
internal sealed record SharedLinkRequestDto(string Type, IReadOnlyList<string>? AssetIds, string? AlbumId, bool AllowDownload, bool ShowMetadata, string? ExpiresAt);
internal sealed record SharedLinkDto(string Id, string Key);
internal sealed record ServerExternalDomainDto(string? ExternalDomain);
internal sealed record WellKnownApiDto(string? Endpoint);
