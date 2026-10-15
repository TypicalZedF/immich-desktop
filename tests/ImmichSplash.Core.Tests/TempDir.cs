namespace ImmichSplash.Core.Tests;

sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "immich-splash-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string relative, int bytes = 16, DateTime? modifiedUtc = null)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        var data = new byte[bytes];
        Random.Shared.NextBytes(data);
        System.IO.File.WriteAllBytes(full, data);
        System.IO.File.SetLastWriteTimeUtc(full, modifiedUtc ?? DateTime.UtcNow.AddMinutes(-5));
        return full;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}
