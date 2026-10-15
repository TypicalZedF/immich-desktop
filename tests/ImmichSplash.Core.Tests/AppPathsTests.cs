namespace ImmichSplash.Core.Tests;

public class AppPathsTests
{
    [Fact]
    public void MovesTheOldFolderToTheNewName()
    {
        using var temp = new TempDir();
        var old = Path.Combine(temp.Path, "Immich Desktop");
        var current = Path.Combine(temp.Path, "Immich Splash");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "settings.json"), "{}");

        Assert.Equal(current, AppPaths.MoveLegacy(old, current));

        Assert.True(File.Exists(Path.Combine(current, "settings.json")));
        Assert.False(Directory.Exists(old));
    }

    [Fact]
    public void KeepsTheNewFolderWhenBothExist()
    {
        using var temp = new TempDir();
        var old = Path.Combine(temp.Path, "Immich Desktop");
        var current = Path.Combine(temp.Path, "Immich Splash");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(old, "settings.json"), "old");

        AppPaths.MoveLegacy(old, current);

        Assert.False(File.Exists(Path.Combine(current, "settings.json")));
        Assert.True(File.Exists(Path.Combine(old, "settings.json")));
    }
}
