using ArChrono.Storage;
using ArChrono.Storage.Records;
using ArChrono.Storage.Stores;
using ArChrono.Tests.Support;

namespace ArChrono.Tests.Storage;

public sealed class StorageTests : IDisposable
{
    private readonly string _directory = TestGit.CreateTempDirectory("db");
    private readonly ArChronoStorage _storage;

    public StorageTests()
    {
        _storage = ArChronoStorage.OpenAndMigrate(Path.Combine(_directory, "archrono.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestGit.DeleteDirectory(_directory);
    }

    private RepositoryRecord AddRepository(string root = "/work/app") =>
        _storage.Repositories.Upsert(root, root + "/.git", root + "/.git", Path.GetFileName(root), "sha1");

    [Fact]
    public void Migration_sets_schema_version_and_is_idempotent()
    {
        Assert.Equal(ArChronoDatabase.LatestSchemaVersion, _storage.Database.SchemaVersion);
        _storage.Database.Migrate();
        Assert.True(ArChronoDatabase.LatestSchemaVersion >= 1);
    }

    [Fact]
    public void Repository_upsert_keeps_id_and_orders_recent()
    {
        var first = AddRepository("/work/a");
        var second = AddRepository("/work/b");
        var again = AddRepository("/work/a");
        Assert.Equal(first.Id, again.Id);

        _storage.Repositories.SetFavorite(second.Id, true);
        var recent = _storage.Repositories.GetRecent();
        Assert.Equal(second.Id, recent[0].Id);
        Assert.True(recent[0].IsFavorite);
    }

    [Fact]
    public void Snapshot_roundtrip_and_blob_reference_counting()
    {
        var repo = AddRepository();
        var now = DateTimeOffset.Now;
        var blobs = new[]
        {
            new ContentBlobRecord("aaa", 10, 8, "brotli", now),
            new ContentBlobRecord("bbb", 20, 20, "none", now),
        };
        var snapshot = _storage.Snapshots.Insert(
            new SnapshotRecord(0, repo.Id, now, SnapshotTrigger.Timer, null, "refs/heads/main", "head1", "tree1", "pin1", "fp1", 0, 30, 28, null),
            [
                new SnapshotEntryRecord("src/a.cs", SnapshotEntryKind.Modified, "aaa", 0x81A4, 10),
                new SnapshotEntryRecord("notes.md", SnapshotEntryKind.Untracked, "bbb", 0x81A4, 20),
                new SnapshotEntryRecord("old.cs", SnapshotEntryKind.Deleted, null, null, null),
            ],
            blobs, pinCommonDir: "/work/app/.git");

        Assert.True(snapshot.Id > 0);
        Assert.Equal(3, _storage.Snapshots.GetEntries(snapshot.Id).Count);
        Assert.Equal("pin1", _storage.Snapshots.FindPin(repo.Id, "head1", "tree1"));
        Assert.Null(_storage.Snapshots.FindPin(repo.Id, "other", "tree1"));
        Assert.Equal(28, _storage.Blobs.GetTotalStoredBytes());
        Assert.Empty(_storage.Blobs.GetUnreferenced());
        Assert.Equal(snapshot.Id, _storage.Snapshots.FindAtOrBefore(repo.Id, now.AddSeconds(1))!.Id);
        Assert.Null(_storage.Snapshots.FindAtOrBefore(repo.Id, now.AddMinutes(-1)));
        Assert.Single(_storage.Snapshots.GetPathEntries(repo.Id, "src/a.cs"));

        Assert.Empty(_storage.Pins.GetUnreferenced("/work/app/.git"));
        _storage.Snapshots.Delete([snapshot.Id]);
        Assert.Equal(2, _storage.Blobs.GetUnreferenced().Count);
        Assert.Equal(["pin1"], _storage.Pins.GetUnreferenced("/work/app/.git"));
    }

    [Fact]
    public void Recovery_point_with_refs_and_stashes_cascades_with_repository()
    {
        var repo = AddRepository();
        var point = _storage.RecoveryPoints.Insert(
            new RecoveryPointRecord(0, repo.Id, DateTimeOffset.Now, RecoveryPointKind.BeforeOperation, "Before reset --hard", null,
                "refs/heads/main", "abc", "main", "clean", null, 3, false, null),
            [new RecoveryRefRecord("refs/heads/main", "abc"), new RecoveryRefRecord("refs/heads/feature", "def")],
            [new RecoveryStashRecord(0, "st1", "WIP on main")]);

        Assert.Equal(2, _storage.RecoveryPoints.GetRefs(point.Id).Count);
        Assert.Single(_storage.RecoveryPoints.GetStashes(point.Id));
        Assert.Equal(RecoveryPointKind.BeforeOperation, _storage.RecoveryPoints.Get(point.Id)!.Kind);

        _storage.Repositories.Remove(repo.Id);
        Assert.Null(_storage.RecoveryPoints.Get(point.Id));
        Assert.Empty(_storage.RecoveryPoints.GetRefs(point.Id));
    }

    [Fact]
    public void Operations_track_undo_and_redo_chain()
    {
        var repo = AddRepository();
        var point = _storage.RecoveryPoints.Insert(
            new RecoveryPointRecord(0, repo.Id, DateTimeOffset.Now, RecoveryPointKind.BeforeOperation, "p", null, null, null, null, "clean", null, 0, false, null), [], []);

        var reset = _storage.Operations.Start(repo.Id, "reset", "Reset", "git reset --hard", OperationRisk.Destructive);
        _storage.Operations.SetBeforePoint(reset.Id, point.Id);
        _storage.Operations.Finish(reset.Id, OperationStatus.Succeeded, null);
        Assert.Equal(reset.Id, _storage.Operations.GetLastUndoable(repo.Id)!.Id);
        Assert.Null(_storage.Operations.GetLastRedoable(repo.Id));

        var undo = _storage.Operations.Start(repo.Id, "undo", "Undo reset", "", OperationRisk.Reversible, reset.Id);
        _storage.Operations.SetBeforePoint(undo.Id, point.Id);
        _storage.Operations.Finish(undo.Id, OperationStatus.Succeeded, null);
        Assert.Null(_storage.Operations.GetLastUndoable(repo.Id));
        Assert.Equal(undo.Id, _storage.Operations.GetLastRedoable(repo.Id)!.Id);

        var redo = _storage.Operations.Start(repo.Id, "redo", "Redo reset", "", OperationRisk.Reversible, undo.Id);
        _storage.Operations.SetBeforePoint(redo.Id, point.Id);
        _storage.Operations.Finish(redo.Id, OperationStatus.Succeeded, null);
        Assert.Equal(reset.Id, _storage.Operations.GetLastUndoable(repo.Id)!.Id);
        Assert.Null(_storage.Operations.GetLastRedoable(repo.Id));
    }

    [Fact]
    public void Settings_roundtrip_json()
    {
        _storage.Settings.Set("sample", new Dictionary<string, int> { ["a"] = 1 });
        Assert.Equal(1, _storage.Settings.Get<Dictionary<string, int>>("sample")!["a"]);
        Assert.Null(_storage.Settings.Get<string>("missing"));
    }
}
