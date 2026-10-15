using ImmichSplash.Core.Storage;
using ImmichSplash.Core.Sync;

namespace ImmichSplash.Core.Tests;

public class StateDatabaseTests
{
    [Fact]
    public void Upsert_keeps_state_for_unchanged_files_and_resets_changed_ones()
    {
        using var temp = new TempDir();
        var db = new StateDatabase(Path.Combine(temp.Path, "state.db"));
        var a = Path.Combine(temp.Path, "a.jpg");
        var b = Path.Combine(temp.Path, "b.jpg");

        db.Upsert([new ScannedFile(a, "f1", 10, 100), new ScannedFile(b, "f1", 20, 200)]);
        db.MarkDone(a, FileStatus.Uploaded, "asset-a");
        db.MarkDone(b, FileStatus.Uploaded, "asset-b");

        db.Upsert([new ScannedFile(a, "f1", 10, 100), new ScannedFile(b, "f1", 21, 201)]);

        var counts = db.GetCounts(["f1"]);
        Assert.Equal(1, counts.Uploaded);
        Assert.Equal(1, counts.Pending);
        Assert.Equal(b, Assert.Single(db.GetCandidates(["f1"], 10)).Path);
    }

    [Fact]
    public void Failed_files_wait_for_their_retry_time()
    {
        using var temp = new TempDir();
        var db = new StateDatabase(Path.Combine(temp.Path, "state.db"));
        var a = Path.Combine(temp.Path, "a.jpg");
        db.Upsert([new ScannedFile(a, "f1", 10, 100)]);

        db.MarkFailed(a, "boom", DateTime.UtcNow.AddHours(1));
        Assert.Empty(db.GetCandidates(["f1"], 10));
        Assert.Equal(1, db.GetCounts(["f1"]).Failed);

        Assert.Equal(1, db.RetryFailed());
        Assert.Single(db.GetCandidates(["f1"], 10));
    }

    [Fact]
    public void DeleteUnder_removes_a_directory_but_not_siblings()
    {
        using var temp = new TempDir();
        var db = new StateDatabase(Path.Combine(temp.Path, "state.db"));
        var dir = Path.Combine(temp.Path, "Trip");
        db.Upsert([
            new ScannedFile(Path.Combine(dir, "1.jpg"), "f", 1, 1),
            new ScannedFile(Path.Combine(dir, "sub", "2.jpg"), "f", 1, 1),
            new ScannedFile(Path.Combine(temp.Path, "Trip2", "3.jpg"), "f", 1, 1),
        ]);

        db.DeleteUnder(dir);

        Assert.Equal(1, db.GetCounts(["f"]).Total);
    }

    [Fact]
    public void DeleteFoldersExcept_drops_removed_folders()
    {
        using var temp = new TempDir();
        var db = new StateDatabase(Path.Combine(temp.Path, "state.db"));
        db.Upsert([new ScannedFile("x1", "keep", 1, 1), new ScannedFile("x2", "drop", 1, 1)]);

        db.DeleteFoldersExcept(["keep"]);

        Assert.Equal(1, db.GetCounts(["keep"]).Total);
        Assert.Equal(0, db.GetCounts(["drop"]).Total);
    }
}
