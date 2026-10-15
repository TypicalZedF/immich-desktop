using ImmichSplash.Core.Settings;

namespace ImmichSplash.Core.Tests;

public class FolderRulesTests
{
    [Fact]
    public void Rejects_duplicate_and_overlapping_folders()
    {
        using var temp = new TempDir();
        var pictures = Directory.CreateDirectory(Path.Combine(temp.Path, "Pictures")).FullName;
        var camera = Directory.CreateDirectory(Path.Combine(pictures, "Camera")).FullName;
        var videos = Directory.CreateDirectory(Path.Combine(temp.Path, "Videos")).FullName;
        var existing = new[] { new SyncFolder { Path = pictures } };

        Assert.NotNull(FolderRules.ValidateNewFolder(pictures, existing));
        Assert.NotNull(FolderRules.ValidateNewFolder(camera, existing));
        Assert.NotNull(FolderRules.ValidateNewFolder(temp.Path, existing));
        Assert.Null(FolderRules.ValidateNewFolder(videos, existing));
    }

    [Fact]
    public void Allows_subfolder_when_parent_excludes_subfolders()
    {
        using var temp = new TempDir();
        var camera = Directory.CreateDirectory(Path.Combine(temp.Path, "Camera")).FullName;
        var existing = new[] { new SyncFolder { Path = temp.Path, IncludeSubfolders = false } };

        Assert.Null(FolderRules.ValidateNewFolder(camera, existing));
    }

    [Fact]
    public void IsInside_does_not_match_sibling_prefixes()
    {
        var root = Path.GetTempPath();
        Assert.False(FolderRules.IsInside(Path.Combine(root, "Photos2"), Path.Combine(root, "Photos")));
        Assert.True(FolderRules.IsInside(Path.Combine(root, "Photos", "a"), Path.Combine(root, "Photos")));
    }
}
