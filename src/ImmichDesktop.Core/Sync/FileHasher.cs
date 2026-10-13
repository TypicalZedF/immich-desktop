using System.Security.Cryptography;

namespace ImmichDesktop.Core.Sync;

public static class FileHasher
{
    /// <summary>SHA-1 of the file contents as lowercase hex, which is what Immich uses to detect duplicates.</summary>
    public static async Task<string> Sha1HexAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            BufferSize = 1 << 20,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });
        var hash = await SHA1.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }
}
