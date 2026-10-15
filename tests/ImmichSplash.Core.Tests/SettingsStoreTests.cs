using ImmichSplash.Core.Settings;

namespace ImmichSplash.Core.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void Settings_survive_a_restart()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "settings.json");
        new SettingsStore(path).Update(s => s.MaxConcurrentUploads = 4);

        Assert.Equal(4, new SettingsStore(path).Current.MaxConcurrentUploads);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void A_corrupt_file_starts_fresh_and_keeps_a_copy()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "{ not json");

        var store = new SettingsStore(path);

        Assert.Equal(new AppSettings().MaxConcurrentUploads, store.Current.MaxConcurrentUploads);
        Assert.Equal("{ not json", File.ReadAllText(path + ".broken"));
    }

    [Fact]
    public void Out_of_range_values_are_clamped()
    {
        using var temp = new TempDir();
        var store = new SettingsStore(Path.Combine(temp.Path, "settings.json"));

        store.Update(s =>
        {
            s.MaxConcurrentUploads = 50;
            s.UploadLimitKBps = -10;
            s.RescanIntervalMinutes = 1;
        });

        var current = store.Current;
        Assert.Equal(8, current.MaxConcurrentUploads);
        Assert.Equal(0, current.UploadLimitKBps);
        Assert.Equal(5, current.RescanIntervalMinutes);
    }

    [Fact]
    public void Snapshots_are_copies()
    {
        using var temp = new TempDir();
        var store = new SettingsStore(Path.Combine(temp.Path, "settings.json"));

        store.Current.MaxConcurrentUploads = 7;

        Assert.NotEqual(7, store.Current.MaxConcurrentUploads);
    }
}
