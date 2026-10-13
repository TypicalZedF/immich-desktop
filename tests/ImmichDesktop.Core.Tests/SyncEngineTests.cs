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
}
