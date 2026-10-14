using ImmichDesktop.Core.Api;
using ImmichDesktop.Core.Services;
using ImmichDesktop.Core.Settings;
using ImmichDesktop.Core.Storage;
using ImmichDesktop.Core.Sync;

namespace ImmichDesktop.Core.Tests;

public class SyncEngineTests
{
    sealed class Harness : IAsyncDisposable
    {
        public readonly TempDir Temp = new();
        public readonly FakeImmichServer Server = new();
        public readonly SettingsStore Settings;
        public readonly StateDatabase Db;
        public readonly AccountService Account;
        public readonly SyncEngine Engine;
        public readonly string Photos;

        public Harness(Action<SyncFolder>? configure = null)
        {
            Photos = Directory.CreateDirectory(Path.Combine(Temp.Path, "Photos")).FullName;
            Settings = new SettingsStore(Path.Combine(Temp.Path, "settings.json"));
            Db = new StateDatabase(Path.Combine(Temp.Path, "state.db"));
            Account = new AccountService(Settings, Db);
            var folder = new SyncFolder { Path = Photos };
            configure?.Invoke(folder);
            Settings.Update(s =>
            {
                s.ServerUrl = "https://immich.test/api/";
                s.UserId = "user-1";
                s.Folders = [folder];
            });
            Account.UseClient(new ImmichClient(new Uri("https://immich.test/api/"), new AuthCredential(AuthKind.ApiKey, "key"), Server));
            Engine = new SyncEngine(Settings, Db, Account, new ActivityLog(Db)) { FileSettleTime = TimeSpan.Zero };
        }

        public Task<TimeSpan?> RunAsync() => Engine.RunCycleAsync(CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            Account.Dispose();
            Temp.Dispose();
        }
    }

    [Fact]
    public async Task Uploads_new_media_skips_duplicates_and_other_files()
    {
        await using var h = new Harness();
        h.Temp.File("Photos/a.jpg");
        h.Temp.File("Photos/sub/b.mp4", bytes: 200_000);
        h.Temp.File("Photos/notes.txt");
        h.Server.Preload(h.Temp.File("Photos/already.jpg"));

        await h.RunAsync();

        Assert.Equal(["a.jpg", "b.mp4"], h.Server.UploadedFileNames.Order());
        Assert.Equal(SyncState.Idle, h.Engine.Status.State);
        Assert.Equal(new StatusCounts(0, 2, 1, 0, 0), h.Engine.Status.Counts);
    }

    [Fact]
    public async Task Second_run_uploads_nothing_and_changed_files_are_uploaded_again()
    {
        await using var h = new Harness();
        var a = h.Temp.File("Photos/a.jpg");
        await h.RunAsync();
        Assert.Single(h.Server.UploadedFileNames);

        await h.RunAsync();
        Assert.Single(h.Server.UploadedFileNames);

        File.WriteAllBytes(a, [1, 2, 3, 4, 5]);
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddMinutes(-1));
        h.Engine.SyncNow();
        await h.RunAsync();
        Assert.Equal(2, h.Server.UploadedFileNames.Count);
    }

    [Fact]
    public async Task Files_still_being_written_are_deferred()
    {
        await using var h = new Harness();
        h.Engine.FileSettleTime = TimeSpan.FromMinutes(1);
        h.Temp.File("Photos/new.jpg", modifiedUtc: DateTime.UtcNow);

        var retry = await h.RunAsync();

        Assert.Empty(h.Server.UploadedFileNames);
        Assert.Equal(1, h.Engine.Status.Counts.Pending);
        Assert.NotNull(retry);
    }

    [Fact]
    public async Task Adds_uploads_and_duplicates_to_the_folder_album()
    {
        await using var h = new Harness(f => f.AlbumName = "Desktop");
        h.Temp.File("Photos/a.jpg");
        h.Server.Preload(h.Temp.File("Photos/b.jpg"));

        await h.RunAsync();

        var album = Assert.Single(h.Server.Albums.Values);
        Assert.Equal("Desktop", album.Name);
        Assert.Equal(2, album.Assets.Count);
    }

    [Fact]
    public async Task Setting_an_album_later_adds_already_uploaded_files_without_reuploading()
    {
        await using var h = new Harness();
        h.Temp.File("Photos/a.jpg");
        h.Temp.File("Photos/b.jpg");
        await h.RunAsync();
        Assert.Equal(2, h.Server.UploadedFileNames.Count);
        Assert.Empty(h.Server.Albums);

        // Oops, forgot the album: set it afterwards.
        h.Settings.Update(s => s.Folders[0].AlbumName = "Trip");
        h.Engine.OnSettingsChanged(h.Settings.Current);
        await h.RunAsync();

        var album = Assert.Single(h.Server.Albums.Values);
        Assert.Equal("Trip", album.Name);
        Assert.Equal(2, album.Assets.Count);
        Assert.Equal(2, h.Server.UploadedFileNames.Count); // nothing uploaded twice
    }

    [Fact]
    public async Task Album_set_during_a_big_upload_fills_while_uploading_not_only_at_the_end()
    {
        await using var h = new Harness();
        for (var i = 0; i < 120; i++)
            h.Temp.File($"Photos/IMG_{i:000}.jpg");

        // The user types an album name a few photos into a long backup.
        h.Server.AfterUpload = count =>
        {
            if (count != 5)
                return;
            h.Settings.Update(s => s.Folders[0].AlbumName = "Trip");
            h.Engine.OnSettingsChanged(h.Settings.Current);
        };

        await h.RunAsync();

        var events = h.Server.Events.ToList();
        var firstAlbum = events.IndexOf("album");
        var lastUpload = events.LastIndexOf("upload");
        Assert.True(firstAlbum >= 0, "album was never filled");
        Assert.True(firstAlbum < lastUpload, "album only filled after every upload finished");
        Assert.Equal(120, Assert.Single(h.Server.Albums.Values).Assets.Count);
        Assert.Equal(120, h.Server.UploadedFileNames.Count);
    }

    [Fact]
    public async Task Exclusions_and_disabled_folders_are_respected()
    {
        await using var h = new Harness(f => f.ExcludePatterns = ["Screenshots"]);
        h.Temp.File("Photos/keep.jpg");
        h.Temp.File("Photos/Screenshots/skip.jpg");

        await h.RunAsync();
        Assert.Equal(["keep.jpg"], h.Server.UploadedFileNames);

        h.Settings.Update(s => s.Folders[0].Enabled = false);
        h.Temp.File("Photos/later.jpg");
        await h.RunAsync();
        Assert.Single(h.Server.UploadedFileNames);
    }

    [Fact]
    public async Task Excluding_files_drops_them_from_the_queue_without_a_full_rescan()
    {
        await using var h = new Harness();
        h.Temp.File("Photos/keep.jpg", modifiedUtc: DateTime.UtcNow);
        h.Temp.File("Photos/Screenshots/Screenshot_1.png", modifiedUtc: DateTime.UtcNow);
        h.Temp.File("Photos/Screenshot 2026-10-12.png", modifiedUtc: DateTime.UtcNow);

        // First pass queues everything (files are "still being written", so nothing uploads yet).
        h.Engine.FileSettleTime = TimeSpan.FromMinutes(1);
        await h.RunAsync();
        Assert.Equal(3, h.Engine.Status.Counts.Pending);

        // The user ticks "Skip screenshots" while files are queued.
        h.Settings.Update(s => s.Folders[0].ExcludePatterns = ["Screenshot*"]);
        h.Engine.OnSettingsChanged(h.Settings.Current);

        h.Engine.FileSettleTime = TimeSpan.Zero;
        h.Db.RetryFailed(); // clear the "still being written" deferral
        await h.RunAsync();

        Assert.Equal(["keep.jpg"], h.Server.UploadedFileNames);
        Assert.Equal(new StatusCounts(0, 1, 0, 0, 0), h.Engine.Status.Counts);
    }

    [Fact]
    public async Task Turning_a_folder_off_stops_its_queued_uploads()
    {
        await using var h = new Harness();
        h.Temp.File("Photos/a.jpg", modifiedUtc: DateTime.UtcNow);
        h.Engine.FileSettleTime = TimeSpan.FromMinutes(1);
        await h.RunAsync();

        h.Settings.Update(s => s.Folders[0].Enabled = false);
        h.Engine.OnSettingsChanged(h.Settings.Current);
        h.Engine.FileSettleTime = TimeSpan.Zero;
        h.Db.RetryFailed();
        await h.RunAsync();

        Assert.Empty(h.Server.UploadedFileNames);
    }

    [Fact]
    public async Task Rejected_credentials_put_the_engine_in_auth_error()
    {
        await using var h = new Harness();
        h.Temp.File("Photos/a.jpg");
        h.Server.RejectAuth = true;

        await h.RunAsync();

        Assert.Equal(SyncState.AuthError, h.Engine.Status.State);
        Assert.Empty(h.Server.UploadedFileNames);
    }

    [Fact]
    public async Task Paused_engine_does_nothing()
    {
        await using var h = new Harness();
        h.Temp.File("Photos/a.jpg");
        h.Engine.Pause();

        await h.RunAsync();

        Assert.Equal(SyncState.Paused, h.Engine.Status.State);
        Assert.Empty(h.Server.UploadedFileNames);
    }

    [Fact]
    public async Task Data_saving_holds_videos_or_everything_on_a_metered_connection()
    {
        await using var h = new Harness();
        h.Temp.File("Photos/a.jpg");
        h.Temp.File("Photos/b.mp4", bytes: 200_000);
        var metered = true;
        h.Engine.IsMetered = () => metered;

        // Photos only (the default): the video waits.
        await h.RunAsync();
        Assert.Equal(["a.jpg"], h.Server.UploadedFileNames);

        // Pause: nothing is uploaded at all.
        h.Settings.Update(s => s.MeteredBackup = MeteredBackup.Pause);
        h.Temp.File("Photos/c.jpg");
        h.Engine.SyncNow();
        await h.RunAsync();
        Assert.Equal(SyncState.WaitingForNetwork, h.Engine.Status.State);
        Assert.Single(h.Server.UploadedFileNames);

        // Back on an unmetered connection everything catches up, the held video included.
        metered = false;
        h.Engine.SyncNow();
        await h.RunAsync();
        Assert.Equal(["a.jpg", "b.mp4", "c.jpg"], h.Server.UploadedFileNames.Order());
    }

    [Fact]
    public async Task Pause_on_battery_holds_uploads_until_plugged_in()
    {
        await using var h = new Harness();
        h.Temp.File("Photos/a.jpg");
        var onBattery = true;
        h.Engine.IsOnBattery = () => onBattery;
        h.Settings.Update(s => s.PauseOnBattery = true);

        await h.RunAsync();
        Assert.Equal(SyncState.WaitingForPower, h.Engine.Status.State);
        Assert.Empty(h.Server.UploadedFileNames);

        onBattery = false;
        h.Engine.SyncNow();
        await h.RunAsync();
        Assert.Equal(["a.jpg"], h.Server.UploadedFileNames);
    }

    [Fact]
    public async Task Battery_is_ignored_when_the_setting_is_off()
    {
        await using var h = new Harness();
        h.Temp.File("Photos/a.jpg");
        h.Engine.IsOnBattery = () => true;
        h.Settings.Update(s => s.PauseOnBattery = false);

        await h.RunAsync();
        Assert.Equal(["a.jpg"], h.Server.UploadedFileNames);
    }
}
