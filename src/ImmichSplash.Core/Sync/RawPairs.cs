namespace ImmichSplash.Core.Sync;

/// <summary>
/// Finds photos a camera saved twice, as RAW and as JPEG (or HEIC): same folder, same name.
/// The JPEG leads the stack, since it's what every viewer can show.
/// </summary>
public static class RawPairs
{
    static readonly HashSet<string> Raw = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3fr", ".ari", ".arw", ".cap", ".cr2", ".cr3", ".crw", ".dcr", ".dng", ".erf", ".fff", ".iiq", ".k25", ".kdc",
        ".mrw", ".nef", ".nrw", ".orf", ".ori", ".pef", ".raf", ".raw", ".rw2", ".rwl", ".sr2", ".srf", ".srw", ".x3f",
    };

    static readonly HashSet<string> Viewable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".heic", ".heif", ".hif",
    };

    public static bool IsRaw(string path) => Raw.Contains(Path.GetExtension(path));

    /// <summary>A stack to make: the asset ids (main photo first) and the files they came from.</summary>
    public sealed record Pair(IReadOnlyList<string> AssetIds, IReadOnlyList<string> Paths);

    /// <summary>
    /// Groups backed-up files into RAW+JPEG stacks. Also returns files that can never be part of one
    /// (not a RAW or JPEG), so they aren't looked at again.
    /// </summary>
    public static (List<Pair> Pairs, List<string> NotApplicable) Find(IEnumerable<(string Path, string AssetId)> files)
    {
        var pairs = new List<Pair>();
        var skip = new List<string>();
        var groups = new Dictionary<string, List<(string Path, string AssetId)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var ext = Path.GetExtension(file.Path);
            if (!Raw.Contains(ext) && !Viewable.Contains(ext))
            {
                skip.Add(file.Path);
                continue;
            }
            var key = Path.Combine(Path.GetDirectoryName(file.Path) ?? "", Path.GetFileNameWithoutExtension(file.Path));
            if (!groups.TryGetValue(key, out var list))
                groups[key] = list = [];
            list.Add(file);
        }
        foreach (var group in groups.Values)
        {
            var main = group.Where(f => Viewable.Contains(Path.GetExtension(f.Path))).ToList();
            var raws = group.Where(f => Raw.Contains(Path.GetExtension(f.Path))).ToList();
            // Wait until both halves are backed up; a JPEG and a HEIC of one name is ambiguous, so leave it.
            if (main.Count != 1 || raws.Count == 0)
                continue;
            var ids = new[] { main[0].AssetId }.Concat(raws.Select(r => r.AssetId)).Distinct().ToList();
            if (ids.Count > 1)
                pairs.Add(new Pair(ids, group.Select(f => f.Path).ToList()));
        }
        return (pairs, skip);
    }
}
