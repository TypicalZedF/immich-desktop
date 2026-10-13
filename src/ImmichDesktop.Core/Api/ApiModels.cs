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

public sealed record UserInfo(string Id, string Name, string Email);

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

public sealed record AlbumInfo(string Id, string AlbumName);

public sealed record MediaTypesInfo(string[]? Image, string[]? Video);

/// <summary>Everything needed to upload one file.</summary>
public sealed record UploadRequest(
    string FilePath,
    string FileName,
    long Size,
    string Checksum,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc);

// Wire DTOs
internal sealed record LoginRequestDto(string Email, string Password);
internal sealed record LoginResponseDto(string AccessToken, string UserId, string UserEmail, string Name);
internal sealed record PingResponseDto(string? Res);
internal sealed record VersionResponseDto(int Major, int Minor, int Patch);
internal sealed record UserResponseDto(string Id, string Name, string Email);
internal sealed record BulkCheckRequestDto(IReadOnlyList<ChecksumItem> Assets);
internal sealed record BulkCheckResponseDto(List<BulkCheckResult> Results);
internal sealed record CreateAlbumRequestDto(string AlbumName, IReadOnlyList<string>? AssetIds);
internal sealed record BulkIdsRequestDto(IReadOnlyList<string> Ids);
internal sealed record WellKnownDto(WellKnownApiDto? Api);
internal sealed record WellKnownApiDto(string? Endpoint);
