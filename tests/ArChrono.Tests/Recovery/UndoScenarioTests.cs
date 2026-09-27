using ArChrono.Git.Models;
using ArChrono.Git.Services;
using ArChrono.Recovery.Operations;
using ArChrono.Recovery.Restore;
using ArChrono.Storage.Records;
using ArChrono.Tests.Support;

namespace ArChrono.Tests.Recovery;

/// <summary>
/// Ürünün kabul kriteri (recovery-model.md §7):
/// Developer makes mistake → recovery point → operation → UNDO → original state restored.
/// </summary>
public sealed class UndoScenarioTests
{
    private static void MakeMessyWorkingTree(TestRepository test)
    {
        test.WriteFile("src/app.cs", "class App { /* committed */ }\n");
        test.WriteFile("src/old.cs", "// will be deleted in working tree\n");
        test.CommitAll("app");
        test.WriteFile("src/app.cs", "class App { /* uncommitted edit */ }\n");   // modified
        test.WriteFile("src/staged.cs", "// staged new file\n");
        test.Git("add", "src/staged.cs");                                          // staged
        test.WriteFile("notes/todo.md", "- untracked note\n");                     // untracked
        test.DeleteFile("src/old.cs");                                             // deleted
    }

    [Fact]
    public async Task Scenario01_reset_hard_with_uncommitted_changes_is_fully_undone()
    {
        using var f = await RecoveryFixture.CreateAsync();
        f.Test.WriteFile("history.txt", "v1\n");
        var target = f.Test.CommitAll("older");
        MakeMessyWorkingTree(f.Test);
        var original = f.Capture();

        var outcome = await f.RunAsync(new ResetOperation(target, ResetMode.Hard, "main"));
        Assert.True(outcome.Succeeded);
        Assert.NotNull(outcome.Before);
        Assert.Equal(target, f.Test.Head());
        Assert.False(f.Test.FileExists("src/staged.cs"));

        await f.UndoAsync();
        original.AssertEqual(f.Capture());
    }

    [Fact]
    public async Task Scenario02_deleted_unmerged_branch_is_recreated_and_protected_from_gc()
    {
        using var f = await RecoveryFixture.CreateAsync();
        f.Test.Git("checkout", "-q", "-b", "feature/auth");
        f.Test.WriteFile("auth.cs", "auth\n");
        var tip = f.Test.CommitAll("auth work that exists nowhere else");
        f.Test.Git("checkout", "-q", "main");
        var original = f.Capture();

        var outcome = await f.RunAsync(new DeleteBranchOperation("feature/auth", force: true));
        Assert.True(outcome.Succeeded);
        Assert.Throws<InvalidOperationException>(() => f.Test.RevParse("refs/heads/feature/auth"));
        Assert.Equal(tip, f.Test.RevParse("refs/archrono/pins/" + tip));

        await f.UndoAsync();
        original.AssertEqual(f.Capture());
    }

    [Fact]
    public async Task Scenario03_wrong_rebase_is_undone()
    {
        using var f = await RecoveryFixture.CreateAsync();
        f.Test.Git("checkout", "-q", "-b", "feature");
        f.Test.WriteFile("feature.txt", "feature\n");
        f.Test.CommitAll("feature 1");
        f.Test.WriteFile("feature2.txt", "feature 2\n");
        f.Test.CommitAll("feature 2");
        f.Test.Git("checkout", "-q", "main");
        f.Test.WriteFile("main.txt", "main\n");
        f.Test.CommitAll("main moves");
        f.Test.Git("checkout", "-q", "feature");
        var original = f.Capture();

        var outcome = await f.RunAsync(new RebaseOperation("main", autostash: false));
        Assert.True(outcome.Succeeded);
        Assert.NotEqual(original.Head, f.Test.Head());

        await f.UndoAsync();
        original.AssertEqual(f.Capture());
    }

    [Fact]
    public async Task Scenario04_commit_on_wrong_branch_is_undone_and_changes_stay_staged()
    {
        using var f = await RecoveryFixture.CreateAsync();
        f.Test.WriteFile("fix.cs", "fix\n");
        f.Test.Git("add", "fix.cs");
        var original = f.Capture();

        var outcome = await f.RunAsync(new CommitOperation("Fix meant for another branch", amend: false));
        Assert.True(outcome.Succeeded);

        await f.UndoAsync();
        original.AssertEqual(f.Capture());
        Assert.Contains("fix.cs", f.Test.Git("diff", "--cached", "--name-only"));
    }

    [Fact]
    public async Task Scenario05_amend_is_undone()
    {
        using var f = await RecoveryFixture.CreateAsync();
        f.Test.WriteFile("a.txt", "a\n");
        f.Test.CommitAll("Important message");
        f.Test.WriteFile("b.txt", "b\n");
        f.Test.Git("add", "b.txt");
        var original = f.Capture();

        var outcome = await f.RunAsync(new CommitOperation("Oops, replaced the message", amend: true));
        Assert.True(outcome.Succeeded);
        Assert.Equal("Oops, replaced the message", f.Test.Git("log", "-1", "--format=%s").Trim());

        await f.UndoAsync();
        original.AssertEqual(f.Capture());
        Assert.Equal("Important message", f.Test.Git("log", "-1", "--format=%s").Trim());
    }

    [Fact]
    public async Task Scenario06_stash_drop_is_undone()
    {
        using var f = await RecoveryFixture.CreateAsync();
        f.Test.WriteFile("README.md", "precious work\n");
        f.Test.Git("stash", "push", "-m", "precious");
        var stash = Assert.Single(await f.Repository.Stash.ListAsync());
        var original = f.Capture();

        var outcome = await f.RunAsync(new StashDropOperation(stash));
        Assert.True(outcome.Succeeded);
        Assert.Empty(await f.Repository.Stash.ListAsync());

        await f.UndoAsync();
        original.AssertEqual(f.Capture());
    }

    [Fact]
    public async Task Scenario07_discarded_changes_and_deleted_untracked_files_come_back()
    {
        using var f = await RecoveryFixture.CreateAsync();
        MakeMessyWorkingTree(f.Test);
        var original = f.Capture();

        var outcome = await f.RunAsync(new DiscardChangesOperation(["src/app.cs", "src/old.cs"], ["notes/todo.md"], includeStaged: false));
        Assert.True(outcome.Succeeded);
        Assert.False(f.Test.FileExists("notes/todo.md"));
        Assert.Equal("class App { /* committed */ }\n", f.Test.ReadFile("src/app.cs"));

        await f.UndoAsync();
        original.AssertEqual(f.Capture());
    }

    [Fact]
    public async Task Scenario08_checkout_is_undone_with_uncommitted_changes()
    {
        using var f = await RecoveryFixture.CreateAsync();
        f.Test.Git("branch", "other");
        f.Test.Git("checkout", "-q", "other");
        f.Test.WriteFile("only-on-other.txt", "other\n");
        f.Test.CommitAll("other work");
        f.Test.Git("checkout", "-q", "main");
        f.Test.WriteFile("notes.txt", "uncommitted on main\n");
        var original = f.Capture();

        var outcome = await f.RunAsync(CheckoutOperation.Branch("other"));
        Assert.True(outcome.Succeeded);
        Assert.Equal("other", f.Test.CurrentBranch());

        await f.UndoAsync();
        original.AssertEqual(f.Capture());
        Assert.False(f.Test.FileExists("only-on-other.txt"));
    }

    [Fact]
    public async Task Scenario09_undo_can_be_redone_and_undone_again()
    {
        using var f = await RecoveryFixture.CreateAsync();
        MakeMessyWorkingTree(f.Test);
        var before = f.Capture();

        Assert.True((await f.RunAsync(new ResetOperation("HEAD~1", ResetMode.Hard, "main"))).Succeeded);
        var afterReset = f.Capture();

        await f.UndoAsync();
        before.AssertEqual(f.Capture());
        Assert.Null(f.Engine.GetUndoable(f.RepositoryId));

        var redo = await f.Engine.RedoAsync(f.Context);
        Assert.True(redo!.Succeeded);
        afterReset.AssertEqual(f.Capture());

        await f.UndoAsync();
        before.AssertEqual(f.Capture());
    }

    [Fact]
    public async Task Scenario10_conflicted_merge_undo_aborts_and_restores()
    {
        using var f = await RecoveryFixture.CreateAsync();
        f.Test.WriteFile("f.txt", "base\n");
        f.Test.CommitAll("base");
        f.Test.Git("checkout", "-q", "-b", "incoming");
        f.Test.WriteFile("f.txt", "incoming\n");
        f.Test.CommitAll("incoming");
        f.Test.Git("checkout", "-q", "main");
        f.Test.WriteFile("f.txt", "current\n");
        f.Test.CommitAll("current");
        f.Test.WriteFile("scratch.txt", "untracked scratch\n");
        var original = f.Capture();

        var outcome = await f.RunAsync(new MergeOperation("incoming"));
        Assert.Equal(OperationStatus.Conflicted, outcome.Status);
        Assert.Equal(RepositoryState.Merging, f.Repository.Status.GetState());

        await f.UndoAsync();
        Assert.Equal(RepositoryState.Clean, f.Repository.Status.GetState());
        original.AssertEqual(f.Capture());
    }

    [Fact]
    public async Task Branch_rename_is_undone_by_renaming_back()
    {
        using var f = await RecoveryFixture.CreateAsync();
        f.Test.Git("branch", "feature/old-name");
        f.Test.Git("config", "branch.feature/old-name.description", "keep me");
        var original = f.Capture();

        Assert.True((await f.RunAsync(new RenameBranchOperation("feature/old-name", "feature/new-name"))).Succeeded);
        var plan = await f.Engine.PlanUndoAsync(f.Context, f.Engine.GetUndoable(f.RepositoryId)!);
        Assert.Contains(plan.Actions, a => a is RenameBranchAction);

        await f.UndoAsync();
        original.AssertEqual(f.Capture());
        Assert.Equal("keep me", f.Test.Git("config", "branch.feature/old-name.description").Trim());
    }

    [Fact]
    public async Task Interactive_rebase_and_tag_delete_are_undone()
    {
        using var f = await RecoveryFixture.CreateAsync();
        var baseSha = f.Test.Head();
        f.Test.WriteFile("1.txt", "1\n");
        var c1 = f.Test.CommitAll("one");
        f.Test.WriteFile("2.txt", "2\n");
        var c2 = f.Test.CommitAll("two");
        f.Test.Git("tag", "v1", c2);
        var original = f.Capture();

        Assert.True((await f.RunAsync(new InteractiveRebaseOperation(baseSha,
            [new RebaseTodoItem(c1, RebaseAction.Pick), new RebaseTodoItem(c2, RebaseAction.Squash, "one and two")], autostash: false))).Succeeded);
        Assert.Equal("one and two", f.Test.Git("log", "-1", "--format=%s").Trim());
        Assert.True((await f.RunAsync(new TagDeleteOperation("v1"))).Succeeded);

        await f.UndoAsync(); // tag
        await f.UndoAsync(); // rebase
        original.AssertEqual(f.Capture());
    }

    [Fact]
    public async Task Undo_warns_when_branch_moved_after_the_operation()
    {
        using var f = await RecoveryFixture.CreateAsync();
        f.Test.WriteFile("a.txt", "a\n");
        f.Test.Git("add", "a.txt");
        Assert.True((await f.RunAsync(new CommitOperation("first", amend: false))).Succeeded);
        f.Test.WriteFile("b.txt", "b\n");
        f.Test.CommitAll("made outside the app");

        var plan = await f.Engine.PlanUndoAsync(f.Context, f.Engine.GetUndoable(f.RepositoryId)!);
        var refChange = Assert.Single(plan.Actions.OfType<RefChangeAction>());
        Assert.NotNull(refChange.Warning);
    }

    [Fact]
    public async Task Blocked_operation_creates_no_recovery_point()
    {
        using var f = await RecoveryFixture.CreateAsync();
        var outcome = await f.RunAsync(new CommitOperation("nothing staged", amend: false));
        Assert.Equal(OperationStatus.Blocked, outcome.Status);
        Assert.Null(outcome.Before);
        Assert.Empty(f.Storage.RecoveryPoints.List(f.RepositoryId));
        Assert.Null(f.Engine.GetUndoable(f.RepositoryId));
    }

    [Fact]
    public async Task Recovery_center_restores_deleted_branch_from_older_point()
    {
        using var f = await RecoveryFixture.CreateAsync();
        f.Test.Git("branch", "keep-me");
        var keepSha = f.Test.RevParse("keep-me");
        var point = await f.Engine.Points.CaptureAsync(f.Repository, f.RepositoryId, RecoveryPointKind.Manual, "Manual save");

        f.Test.Git("branch", "-D", "keep-me");            // terminalde, uygulama dışında
        f.Test.WriteFile("later.txt", "later work\n");
        f.Test.CommitAll("later work");                     // bu korunmalı

        var plan = await f.Engine.Planner.PlanRestoreAsync(f.Context, point, RestoreMode.BranchesOnly);
        var recreate = Assert.Single(plan.Actions.OfType<RefChangeAction>(), a => a.RefName == "refs/heads/keep-me");
        Assert.True(recreate.Selected);
        var mainMove = Assert.Single(plan.Actions.OfType<RefChangeAction>(), a => a.RefName == "refs/heads/main");
        Assert.True(mainMove.Selected);

        plan = plan.WithSelection(mainMove, false);
        var outcome = await f.Engine.ExecutePlanAsync(f.Context, plan, undoOf: null);
        Assert.True(outcome.Succeeded);
        Assert.Equal(keepSha, f.Test.RevParse("keep-me"));
        Assert.True(f.Test.FileExists("later.txt"));
    }
}
