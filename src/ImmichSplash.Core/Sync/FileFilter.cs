using ImmichSplash.Core.Settings;
using Microsoft.Extensions.FileSystemGlobbing;

namespace ImmichSplash.Core.Sync;

/// <summary>Decides whether a file inside a sync folder should be backed up.</summary>
public sealed class FileFilter
{
    readonly SyncFolder _folder;
    readonly MediaTypes _media;
    readonly Matcher? _excludes;

    public FileFilter(SyncFolder folder, MediaTypes media)
    {
        _folder = folder;
        _media = media;

        var patterns = folder.ExcludePatterns
            .Select(p => p.Trim().Replace('\\', '/').Trim('/'))
            .Where(p => p.Length > 0 && !p.StartsWith('#'))
            .ToList();
        if (patterns.Count == 0)
            return;

        _excludes = new Matcher(FolderRules.PathComparison);
        foreach (var pattern in patterns)
        {
            // A bare name like "*.tmp" or "Screenshots" should match at any depth, as people expect.
            var anyDepth = pattern.Contains('/') ? pattern : "**/" + pattern;
            _excludes.AddInclude(anyDepth);
            _excludes.AddInclude(anyDepth + "/**");
        }
    }

    public bool IsMatch(string fullPath)
    {
        var kind = _media.Classify(fullPath);
        if (kind == MediaKind.None)
            return false;
        if (kind == MediaKind.Image && !_folder.IncludePhotos)
            return false;
        if (kind == MediaKind.Video && !_folder.IncludeVideos)
            return false;

        if (_excludes is not null)
        {
            var relative = Path.GetRelativePath(_folder.Path, fullPath).Replace('\\', '/');
            if (_excludes.Match(relative).HasMatches)
                return false;
        }
        return true;
    }
}
