namespace ImmichDesktop.Core.Settings;

public static class FolderRules
{
    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return full.Length > (root?.Length ?? 0)
            ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : full;
    }

    /// <summary>True when <paramref name="child"/> is strictly inside <paramref name="parent"/>.</summary>
    public static bool IsInside(string child, string parent)
    {
        child = Normalize(child);
        parent = Normalize(parent);
        if (child.Length <= parent.Length)
            return false;

        var prefix = parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, PathComparison);
    }

    /// <summary>
    /// Returns a user-facing reason why <paramref name="path"/> can't be added, or null if it can.
    /// Overlapping folders are rejected so every file belongs to exactly one folder.
    /// </summary>
    public static string? ValidateNewFolder(string path, IEnumerable<SyncFolder> existing)
    {
        if (!Directory.Exists(path))
            return "That folder doesn't exist.";

        var candidate = Normalize(path);
        foreach (var folder in existing)
        {
            var other = Normalize(folder.Path);
            if (string.Equals(candidate, other, PathComparison))
                return "This folder is already being backed up.";
            if (folder.IncludeSubfolders && IsInside(candidate, other))
                return $"This folder is already included in “{folder.DisplayName}”.";
            if (IsInside(other, candidate))
                return $"“{folder.DisplayName}” is inside this folder. Remove it first, then add this one.";
        }
        return null;
    }
}
