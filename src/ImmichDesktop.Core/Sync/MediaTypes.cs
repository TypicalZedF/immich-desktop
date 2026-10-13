using ImmichDesktop.Core.Api;

namespace ImmichDesktop.Core.Sync;

public enum MediaKind
{
    None,
    Image,
    Video,
}

/// <summary>Which file extensions count as photos and videos.</summary>
public sealed class MediaTypes
{
    // Mirrors the formats Immich accepts.
    static readonly string[] DefaultImage =
    [
        ".3fr", ".ari", ".arw", ".avif", ".bmp", ".cap", ".cin", ".cr2", ".cr3", ".crw", ".dcr", ".dng", ".erf",
        ".fff", ".gif", ".heic", ".heif", ".hif", ".iiq", ".insp", ".jp2", ".jpe", ".jpeg", ".jpg", ".jxl", ".k25",
        ".kdc", ".mrw", ".nef", ".nrw", ".orf", ".ori", ".pef", ".png", ".psd", ".raf", ".raw", ".rw2", ".rwl",
        ".sr2", ".srf", ".srw", ".svg", ".tif", ".tiff", ".webp", ".x3f",
    ];

    static readonly string[] DefaultVideo =
    [
        ".3gp", ".3gpp", ".avi", ".flv", ".insv", ".m2t", ".m2ts", ".m4v", ".mkv", ".mov", ".mp4", ".mpe", ".mpeg",
        ".mpg", ".mts", ".vob", ".webm", ".wmv",
    ];

    readonly HashSet<string> _image;
    readonly HashSet<string> _video;

    public MediaTypes(IEnumerable<string> image, IEnumerable<string> video)
    {
        _image = new HashSet<string>(image, StringComparer.OrdinalIgnoreCase);
        _video = new HashSet<string>(video, StringComparer.OrdinalIgnoreCase);
    }

    public static MediaTypes Default { get; } = new(DefaultImage, DefaultVideo);

    /// <summary>
    /// Builds the list from the server's answer when it lists extensions; otherwise (newer servers report MIME types)
    /// keeps the built-in list. The server still has the final say through the bulk upload check.
    /// </summary>
    public static MediaTypes FromServer(MediaTypesInfo? info)
    {
        var image = info?.Image?.Where(e => e.StartsWith('.')).ToArray() ?? [];
        var video = info?.Video?.Where(e => e.StartsWith('.')).ToArray() ?? [];
        return image.Length > 0 && video.Length > 0 ? new MediaTypes(image, video) : Default;
    }

    public MediaKind Classify(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Length == 0) return MediaKind.None;
        if (_image.Contains(ext)) return MediaKind.Image;
        if (_video.Contains(ext)) return MediaKind.Video;
        return MediaKind.None;
    }
}
