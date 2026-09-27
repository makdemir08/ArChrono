using System.Text;
using ArChrono.Git.Models;
using ArChrono.Recovery.Content;
using ArChrono.Recovery.Retention;
using ArChrono.Recovery.Snapshots;
using ArChrono.Recovery.Timeline;
using ArChrono.Storage.Records;
using ArChrono.Tests.Support;

namespace ArChrono.Tests.Recovery;

public sealed class SnapshotEngineTests
{
    [Fact]
    public void Content_store_ids_match_git_and_detect_corruption()
    {
        var dir = TestGit.CreateTempDirectory("store");
        try
        {
            var store = new ContentStore(dir);
            var text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("compress me please ", 200)));
            var first = store.Put(text, "sha1", "a.txt");
            var second = store.Put(text, "sha1", "b.txt");

            Assert.True(first.IsNew);
            Assert.False(second.IsNew);
            Assert.Equal("brotli", first.Compression);
            Assert.True(first.StoredSize < text.Length / 5);
            Assert.Equal(text, store.Get(first.BlobId));

            using var repo = TestRepository.Create(withInitialCommit: false);
            File.WriteAllBytes(repo.FullPath("x.txt"), text);
            Assert.Equal(repo.Git("hash-object", "x.txt").Trim(), first.BlobId);

            var png = store.Put(text.AsSpan(0, 500), "sha1", "image.png");
            Assert.Equal("none", png.Compression);

            var path = store.PathFor(first.BlobId);
            var bytes = File.ReadAllBytes(path);
            bytes[^1] ^= 0xFF;
            File.WriteAllBytes(path, bytes);
            Assert.ThrowsAny<Exception>(() => store.Get(first.BlobId));
        }
        finally
        {
            TestGit.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task Capture_is_incremental_deduplicated_and_skips_unchanged_state()
    {
        using var f = await RecoveryFixture.CreateAsync();
        var clean = await f.Engine.Snapshots.CaptureAsync(f.Repository, f.RepositoryId, SnapshotTrigger.Timer);
        Assert.False(clean.Unchanged);
        Assert.Equal(0, clean.Snapshot.EntryCount);
        Assert.NotNull(clean.Snapshot.PinCommit);

        var again = await f.Engine.Snapshots.CaptureAsync(f.Repository, f.RepositoryId, SnapshotTrigger.Timer);
        Assert.True(again.Unchanged);
        Assert.Equal(clean.Snapshot.Id, again.Snapshot.Id);

        f.Test.WriteFile("big.txt", string.Concat(Enumerable.Repeat("line of text\n", 5000)));
        f.Test.WriteFile("copy.txt", string.Concat(Enumerable.Repeat("line of text\n", 5000)));
        var dirty = await f.Engine.Snapshots.CaptureAsync(f.Repository, f.RepositoryId, SnapshotTrigger.Timer);
        Assert.Equal(2, dirty.Snapshot.EntryCount);
        Assert.Equal(1, f.Storage.Blobs.Count());          // aynı içerik tek kez
        Assert.Equal(clean.Snapshot.PinCommit, dirty.Snapshot.PinCommit); // HEAD/index aynı → pin yeniden kullanıldı

        f.Test.WriteFile("small.txt", "x\n");
        var third = await f.Engine.Snapshots.CaptureAsync(f.Repository, f.RepositoryId, SnapshotTrigger.Timer);
        Assert.Equal(3, third.Snapshot.EntryCount);
        Assert.True(third.Snapshot.NewBytes < 100);           // yalnızca yeni dosya yazıldı
    }

    [Fact]
    public async Task Large_files_and_ignored_files_are_not_captured()
    {
        using var f = await RecoveryFixture.CreateAsync();
        f.Engine.Options = f.Engine.Options with { Snapshots = new SnapshotOptions { MaxFileSizeBytes = 1024 } };
        f.Test.WriteFile(".gitignore", ".env\n");
        f.Test.CommitAll("ignore");
        f.Test.WriteFile(".env", "SECRET=1\n");
        f.Test.WriteFile("huge.bin", new string('x', 5000));

        var result = await f.Engine.Snapshots.CaptureAsync(f.Repository, f.RepositoryId, SnapshotTrigger.Manual);
        Assert.Equal(0, result.Snapshot.EntryCount);
        Assert.Contains(result.Skipped, s => s.Path == "huge.bin");
        Assert.DoesNotContain(f.Storage.Snapshots.GetEntries(result.Snapshot.Id), e => e.Path == ".env");
    }

    [Fact]
    public async Task Snapshot_limits_protect_the_store_from_huge_untracked_trees()
    {
        using var f = await RecoveryFixture.CreateAsync();
        f.Engine.Options = f.Engine.Options with { Snapshots = new SnapshotOptions { MaxOverlayFiles = 3 } };
        for (var i = 0; i < 10; i++) f.Test.WriteFile($"generated/file{i}.txt", $"content {i}\n");

        var result = await f.Engine.Snapshots.CaptureAsync(f.Repository, f.RepositoryId, SnapshotTrigger.Timer);
        Assert.Equal(3, result.Snapshot.EntryCount);
        Assert.Equal(7, result.Skipped.Count(s => s.Reason == "snapshot size limit reached"));
    }

    [Fact]
    public async Task Orphan_store_files_from_interrupted_captures_are_removed()
    {
        using var f = await RecoveryFixture.CreateAsync();
        var orphan = f.Engine.ContentStore.Put(System.Text.Encoding.UTF8.GetBytes("written but never recorded"), "sha1");
        File.SetLastWriteTimeUtc(f.Engine.ContentStore.PathFor(orphan.BlobId), DateTime.UtcNow.AddHours(-2));
        f.Test.WriteFile("kept.txt", "kept\n");
        await f.Engine.Snapshots.CaptureAsync(f.Repository, f.RepositoryId, SnapshotTrigger.Manual);

        var removed = await f.Engine.Retention.SweepOrphanFilesAsync(TimeSpan.FromHours(1));
        Assert.Equal(1, removed);
        Assert.False(f.Engine.ContentStore.Contains(orphan.BlobId));
        Assert.Equal(1, f.Storage.Blobs.Count());
    }

    [Fact]
    public async Task Time_machine_reads_file_at_time_compares_and_restores_single_file()
    {
        using var f = await RecoveryFixture.CreateAsync();
        f.Test.WriteFile("notes.md", "version 1\n");
        var s1 = (await f.Engine.Snapshots.CaptureAsync(f.Repository, f.RepositoryId, SnapshotTrigger.Timer)).Snapshot;
        f.Test.WriteFile("notes.md", "version 2\n");
        f.Test.WriteFile("README.md", "# Changed\n");
        var s2 = (await f.Engine.Snapshots.CaptureAsync(f.Repository, f.RepositoryId, SnapshotTrigger.Timer)).Snapshot;
        f.Test.WriteFile("notes.md", "version 3\n");

        Assert.Equal("version 1\n", Encoding.UTF8.GetString((await f.Engine.SnapshotReader.ReadFileAsync(f.Repository, s1, "notes.md"))!));
        Assert.Equal("# Test\n", Encoding.UTF8.GetString((await f.Engine.SnapshotReader.ReadFileAsync(f.Repository, s1, "README.md"))!));
        Assert.Null(await f.Engine.SnapshotReader.ReadFileAsync(f.Repository, s1, "missing.txt"));

        var changes = await f.Engine.SnapshotReader.CompareAsync(f.Repository, s1, s2);
        Assert.Equal(["README.md", "notes.md"], changes.Select(c => c.Path));
        var diff = await f.Engine.SnapshotReader.DiffAsync(f.Repository, changes.Single(c => c.Path == "notes.md"));
        Assert.Contains(diff.Hunks.SelectMany(h => h.Lines), l => l.Kind == DiffLineKind.Added && l.Text == "version 2");

        var vsCurrent = await f.Engine.SnapshotReader.CompareAsync(f.Repository, s2, null);
        Assert.Equal("notes.md", Assert.Single(vsCurrent).Path);

        var timeline = await f.Engine.SnapshotReader.GetFileTimelineAsync(f.Repository, f.RepositoryId, "notes.md");
        Assert.Equal(2, timeline.Count);

        await f.Engine.Restorer.RestoreAsync(f.Repository, s1, new RestoreOptions { OnlyPaths = ["notes.md"] });
        Assert.Equal("version 1\n", f.Test.ReadFile("notes.md"));
        Assert.Equal("# Changed\n", f.Test.ReadFile("README.md")); // tek dosya geri yükleme diğerlerine dokunmaz
    }

    [Fact]
    public async Task Full_restore_brings_back_index_worktree_executable_bit_and_symlink()
    {
        using var f = await RecoveryFixture.CreateAsync();
        f.Test.WriteFile("run.sh", "#!/bin/sh\necho hi\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(f.Test.FullPath("run.sh"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.CreateSymbolicLink(f.Test.FullPath("link-to-readme"), "README.md");
        }
        f.Test.WriteFile("staged.txt", "staged\n");
        f.Test.Git("add", "staged.txt");
        var snapshot = (await f.Engine.Snapshots.CaptureAsync(f.Repository, f.RepositoryId, SnapshotTrigger.Manual)).Snapshot;
        var original = f.Capture();

        f.Test.Git("reset", "-q", "--hard");
        f.Test.Git("clean", "-fdq");
        f.Test.WriteFile("created-later.txt", "later\n");

        var report = await f.Engine.Restorer.RestoreAsync(f.Repository, snapshot);
        Assert.Empty(report.Failures);
        Assert.True(f.Test.FileExists("created-later.txt"));
        f.Test.DeleteFile("created-later.txt");
        original.AssertEqual(f.Capture());

        if (!OperatingSystem.IsWindows())
        {
            Assert.True((File.GetUnixFileMode(f.Test.FullPath("run.sh")) & UnixFileMode.UserExecute) != 0);
            Assert.Equal("README.md", new FileInfo(f.Test.FullPath("link-to-readme")).LinkTarget);
        }
    }

    [Fact]
    public async Task Snapshot_during_conflict_captures_conflicted_files()
    {
        using var f = await RecoveryFixture.CreateAsync();
        f.Test.WriteFile("f.txt", "base\n");
        f.Test.CommitAll("base");
        f.Test.Git("checkout", "-q", "-b", "b");
        f.Test.WriteFile("f.txt", "b\n");
        f.Test.CommitAll("b");
        f.Test.Git("checkout", "-q", "main");
        f.Test.WriteFile("f.txt", "main\n");
        f.Test.CommitAll("main");
        await f.Repository.Integration.MergeAsync("b");
        f.Test.WriteFile("f.txt", "my half-done resolution\n");

        var result = await f.Engine.Snapshots.CaptureAsync(f.Repository, f.RepositoryId, SnapshotTrigger.Manual);
        var entry = Assert.Single(f.Storage.Snapshots.GetEntries(result.Snapshot.Id), e => e.Path == "f.txt");
        Assert.Equal(SnapshotEntryKind.Conflicted, entry.Kind);
        Assert.Equal("my half-done resolution\n", Encoding.UTF8.GetString((await f.Engine.SnapshotReader.ReadFileAsync(f.Repository, result.Snapshot, "f.txt"))!));
    }

    [Fact]
    public async Task Retention_deletes_old_snapshots_sweeps_blobs_and_releases_pins()
    {
        using var f = await RecoveryFixture.CreateAsync();
        f.Test.WriteFile("a.txt", "a\n");
        f.Test.Git("add", "a.txt");
        var snapshot = (await f.Engine.Snapshots.CaptureAsync(f.Repository, f.RepositoryId, SnapshotTrigger.Timer)).Snapshot;
        f.Test.WriteFile("a.txt", "a modified\n");
        await f.Engine.Snapshots.CaptureAsync(f.Repository, f.RepositoryId, SnapshotTrigger.Timer);
        Assert.True(f.Storage.Blobs.Count() > 0);
        Assert.Contains(snapshot.PinCommit!, f.Test.Git("for-each-ref", "refs/archrono/pins/"));

        var report = await f.Engine.Retention.RunAsync(new RetentionPolicy { MaxAge = TimeSpan.Zero - TimeSpan.FromSeconds(1) },
            _ => Task.FromResult<ArChrono.Git.GitRepository?>(f.Repository));

        Assert.Equal(2, report.DeletedSnapshots);
        Assert.Equal(0, f.Storage.Blobs.Count());
        Assert.Empty(Directory.EnumerateFiles(f.Engine.ContentStore.ObjectsDirectory, "*", SearchOption.AllDirectories));
        Assert.Equal(1, report.RemovedPins);
        Assert.Equal(string.Empty, f.Test.Git("for-each-ref", "refs/archrono/pins/").Trim());
    }

    [Fact]
    public async Task Timeline_merges_points_snapshots_and_outside_reflog_entries()
    {
        using var f = await RecoveryFixture.CreateAsync();
        f.Test.WriteFile("x.txt", "x\n");
        f.Test.Git("add", "x.txt");
        await f.RunAsync(new ArChrono.Recovery.Operations.CommitOperation("inside app", amend: false));
        await Task.Delay(1100);
        f.Test.WriteFile("y.txt", "y\n");
        f.Test.CommitAll("outside app");
        await f.Engine.Snapshots.CaptureAsync(f.Repository, f.RepositoryId, SnapshotTrigger.Timer);

        var timeline = await f.Engine.Timeline.GetTimelineAsync(f.Repository, f.RepositoryId);
        Assert.Contains(timeline, e => e.Kind == TimelineEntryKind.RecoveryPoint && e.Title.Contains("inside app"));
        Assert.Contains(timeline, e => e.Kind == TimelineEntryKind.Reflog && e.Title.Contains("outside app"));
        Assert.Contains(timeline, e => e.Kind == TimelineEntryKind.Snapshot);
        Assert.DoesNotContain(timeline, e => e.Kind == TimelineEntryKind.Reflog && e.Title.Contains("inside app"));
    }

    [Fact]
    public async Task Deep_scan_finds_lost_commits_but_not_internal_pins()
    {
        using var f = await RecoveryFixture.CreateAsync();
        f.Test.WriteFile("lost.txt", "lost\n");
        var lost = f.Test.CommitAll("work that got lost");
        f.Test.Git("reset", "-q", "--hard", "HEAD~1");
        f.Test.Git("reflog", "expire", "--expire=now", "--all");
        await f.Engine.Snapshots.CaptureAsync(f.Repository, f.RepositoryId, SnapshotTrigger.Manual);

        var found = await f.Engine.Timeline.FindLostWorkAsync(f.Repository);
        var entry = Assert.Single(found);
        Assert.Equal(lost, entry.Sha);
        Assert.Equal(TimelineEntryKind.LostCommit, entry.Kind);
    }
}
