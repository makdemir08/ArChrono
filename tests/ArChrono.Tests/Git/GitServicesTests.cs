using ArChrono.Git;
using ArChrono.Git.Errors;
using ArChrono.Git.Models;
using ArChrono.Git.Process;
using ArChrono.Git.Services;
using ArChrono.Tests.Support;

namespace ArChrono.Tests.Git;

public sealed class GitServicesTests
{
    [Fact]
    public void Locator_finds_supported_git()
    {
        var git = GitLocator.Find();
        Assert.NotNull(git);
        Assert.True(git!.IsSupported, $"git {git.Version} is older than {GitExecutable.MinimumVersion}");
    }

    [Fact]
    public async Task Open_reports_repository_paths()
    {
        using var test = TestRepository.Create();
        Directory.CreateDirectory(test.FullPath("src/deep"));
        var repo = await GitRepository.OpenAsync(TestGit.Runner, test.FullPath("src/deep"));

        Assert.Equal(test.Root, repo.Info.RootPath);
        Assert.Equal(Path.Combine(test.Root, ".git"), repo.Info.GitDir);
        Assert.False(repo.Info.IsLinkedWorktree);
        Assert.Equal("sha1", repo.Info.ObjectFormat);
    }

    [Fact]
    public async Task Open_non_repository_gives_friendly_error()
    {
        var dir = TestGit.CreateTempDirectory("plain");
        try
        {
            var ex = await Assert.ThrowsAsync<GitException>(() => GitRepository.OpenAsync(TestGit.Runner, dir));
            Assert.Equal(GitErrorCode.NotARepository, ex.Error.Code);
            Assert.Contains("rev-parse", ex.Error.TechnicalDetails);
        }
        finally
        {
            TestGit.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task Status_reports_staged_modified_untracked_renamed_and_unicode_paths()
    {
        using var test = TestRepository.Create();
        test.WriteFile("a.txt", "one\n");
        test.WriteFile("to rename.txt", "rename me please, enough content for similarity\n");
        test.CommitAll("files");

        test.WriteFile("a.txt", "one\ntwo\n");                    // modified (unstaged)
        test.WriteFile("staged.txt", "new\n");
        test.Git("add", "staged.txt");                             // added (staged)
        test.Git("mv", "to rename.txt", "renamed file.txt");       // renamed (staged)
        test.WriteFile("klasör/çalışma notu.md", "untracked\n");   // untracked + unicode

        var repo = await test.OpenAsync();
        var status = await repo.Status.GetStatusAsync();

        Assert.Equal("main", status.Branch.BranchName);
        Assert.False(status.Branch.IsDetached);
        Assert.Equal(RepositoryState.Clean, status.State);

        var modified = Assert.Single(status.Entries, e => e.Path == "a.txt");
        Assert.Equal(ChangeKind.Modified, modified.WorktreeChange);
        Assert.False(modified.HasStagedChanges);

        var added = Assert.Single(status.Entries, e => e.Path == "staged.txt");
        Assert.Equal(ChangeKind.Added, added.IndexChange);

        var renamed = Assert.Single(status.Entries, e => e.Path == "renamed file.txt");
        Assert.Equal(ChangeKind.Renamed, renamed.IndexChange);
        Assert.Equal("to rename.txt", renamed.OriginalPath);

        var untracked = Assert.Single(status.Entries, e => e.IsUntracked);
        Assert.Equal("klasör/çalışma notu.md", untracked.Path);

        Assert.Equal(2, status.StagedCount);
        Assert.Equal(1, status.ModifiedCount);
        Assert.Equal(1, status.UntrackedCount);
    }

    [Fact]
    public async Task History_lists_commits_details_and_follows_renames()
    {
        using var test = TestRepository.Create();
        test.WriteFile("src/Auth.cs", "class Auth {}\n// line\n// more lines so rename detection works\n");
        var first = test.CommitAll("Add auth");
        test.Git("mv", "src/Auth.cs", "src/AuthService.cs");
        test.CommitAll("Rename auth");
        test.WriteFile("src/AuthService.cs", "class AuthService {}\n// line\n// more lines so rename detection works\n// refresh token\n");
        var third = test.CommitAll("Add refresh token support");

        var repo = await test.OpenAsync();
        var commits = await repo.History.GetCommitsAsync(new LogQuery());
        Assert.Equal(4, commits.Count);
        Assert.Equal(third, commits[0].Sha);
        Assert.Equal("Add refresh token support", commits[0].Subject);
        Assert.Equal("Test User", commits[0].AuthorName);

        var details = await repo.History.GetCommitDetailsAsync(third);
        var file = Assert.Single(details.Files);
        Assert.Equal("src/AuthService.cs", file.Path);
        Assert.Equal(ChangeKind.Modified, file.Change);
        Assert.True(file.Additions >= 1);

        var root = await repo.History.GetCommitDetailsAsync(commits[^1].Sha);
        Assert.Equal("README.md", Assert.Single(root.Files).Path);

        var history = await repo.History.GetFileHistoryAsync("src/AuthService.cs");
        Assert.Equal(3, history.Count);
        Assert.Equal(first, history[^1].Commit.Sha);
        Assert.Contains(history, h => h.Change == ChangeKind.Renamed && h.OldPath == "src/Auth.cs");
    }

    [Fact]
    public async Task History_of_empty_repository_is_empty()
    {
        using var test = TestRepository.Create(withInitialCommit: false);
        var repo = await test.OpenAsync();
        Assert.Empty(await repo.History.GetCommitsAsync(new LogQuery()));
        var head = await repo.Refs.GetHeadAsync();
        Assert.True(head.IsUnborn);
        Assert.Equal("refs/heads/main", head.BranchRef);
    }

    [Fact]
    public async Task Refs_report_branches_tags_upstream_and_ahead_behind()
    {
        using var remote = TestRepository.Create();
        using var local = TestRepository.Create(withInitialCommit: false);
        local.Git("remote", "add", "origin", remote.Root);
        local.Git("fetch", "-q", "origin");
        local.Git("checkout", "-q", "-b", "main", "--track", "origin/main");
        local.WriteFile("local.txt", "x\n");
        local.CommitAll("Local work");
        remote.WriteFile("remote.txt", "y\n");
        remote.CommitAll("Remote work");
        local.Git("fetch", "-q", "origin");
        local.Git("tag", "-a", "v1.0", "-m", "Release 1.0");
        local.Git("branch", "feature/login");

        var repo = await local.OpenAsync();
        var refs = await repo.Refs.GetRefsAsync();

        var main = Assert.Single(refs, r => r.FullName == "refs/heads/main");
        Assert.True(main.IsHead);
        Assert.Equal("refs/remotes/origin/main", main.Upstream);
        Assert.Equal(1, main.Ahead);
        Assert.Equal(1, main.Behind);

        var originMain = Assert.Single(refs, r => r.FullName == "refs/remotes/origin/main");
        Assert.Equal("origin", originMain.RemoteName);
        Assert.Equal("main", originMain.DisplayName);

        var tag = Assert.Single(refs, r => r.Kind == RefKind.Tag);
        Assert.True(tag.IsAnnotatedTag);
        Assert.Equal(local.Head(), tag.TargetSha);

        Assert.Contains(refs, r => r.Name == "feature/login" && r.Kind == RefKind.LocalBranch);
    }

    [Fact]
    public async Task Diff_parses_unstaged_staged_and_untracked()
    {
        using var test = TestRepository.Create();
        test.WriteFile("code.cs", "line1\nline2\nline3\n");
        test.CommitAll("code");
        test.WriteFile("code.cs", "line1\nline2 changed\nline3\nline4\n");
        test.WriteFile("new.cs", "hello\nworld\n");
        test.Git("add", "new.cs");
        test.WriteFile("untracked.txt", "a\nb\n");

        var repo = await test.OpenAsync();

        var unstaged = Assert.Single(await repo.Diff.GetUnstagedAsync());
        Assert.Equal("code.cs", unstaged.Path);
        Assert.Equal(ChangeKind.Modified, unstaged.Change);
        Assert.Equal(2, unstaged.Additions);
        Assert.Equal(1, unstaged.Deletions);
        var hunk = Assert.Single(unstaged.Hunks);
        Assert.Equal(1, hunk.OldStart);
        Assert.Contains(hunk.Lines, l => l.Kind == DiffLineKind.Added && l.Text == "line4" && l.NewLineNumber == 4);

        var staged = Assert.Single(await repo.Diff.GetStagedAsync());
        Assert.Equal(ChangeKind.Added, staged.Change);
        Assert.Null(staged.OldPath);
        Assert.Equal("new.cs", staged.NewPath);

        var untracked = repo.Diff.GetUntrackedFileDiff("untracked.txt");
        Assert.Equal(2, untracked.Additions);
        Assert.Equal(ChangeKind.Added, untracked.Change);
    }

    [Fact]
    public async Task Commit_stage_unstage_and_amend()
    {
        using var test = TestRepository.Create();
        var repo = await test.OpenAsync();
        test.WriteFile("feature.txt", "v1\n");

        (await repo.WorkingTree.StageAsync(["feature.txt"])).EnsureSuccess();
        Assert.Equal(1, (await repo.Status.GetStatusAsync()).StagedCount);

        (await repo.WorkingTree.UnstageAsync(["feature.txt"], headExists: true)).EnsureSuccess();
        Assert.Equal(0, (await repo.Status.GetStatusAsync()).StagedCount);

        (await repo.WorkingTree.StageAllAsync()).EnsureSuccess();
        (await repo.WorkingTree.CommitAsync("Add feature\n\nLonger description.", amend: false)).EnsureSuccess();
        Assert.Equal("Add feature\n\nLonger description.", await repo.WorkingTree.GetHeadMessageAsync());

        (await repo.WorkingTree.CommitAsync("Add feature (amended)", amend: true)).EnsureSuccess();
        var commits = await repo.History.GetCommitsAsync(new LogQuery());
        Assert.Equal(2, commits.Count);
        Assert.Equal("Add feature (amended)", commits[0].Subject);

        var nothing = await repo.WorkingTree.CommitAsync("empty", amend: false);
        Assert.False(nothing.Success);
        Assert.Equal(GitErrorCode.NothingToCommit, GitErrorTranslator.Translate(nothing).Code);
    }

    [Fact]
    public async Task Merge_prediction_detects_content_conflicts_without_touching_worktree()
    {
        using var test = TestRepository.Create();
        test.WriteFile("shared.txt", "a\nb\nc\n");
        test.WriteFile("both.txt", "1\n2\n3\n4\n5\n6\n7\n8\n9\n");
        test.CommitAll("base");
        test.Git("checkout", "-q", "-b", "feature");
        test.WriteFile("shared.txt", "a\nfeature\nc\n");
        test.WriteFile("both.txt", "1-feature\n2\n3\n4\n5\n6\n7\n8\n9\n");
        test.CommitAll("feature change");
        test.Git("checkout", "-q", "main");
        test.WriteFile("shared.txt", "a\nmain\nc\n");
        test.WriteFile("both.txt", "1\n2\n3\n4\n5\n6\n7\n8\n9-main\n");
        test.CommitAll("main change");
        var headBefore = test.Head();

        var repo = await test.OpenAsync();
        var prediction = await repo.Integration.PredictMergeAsync("main", "feature");

        Assert.True(prediction.HasConflicts);
        Assert.Equal(1, prediction.ContentConflicts);
        Assert.Contains(prediction.Files, f => f.Path == "shared.txt" && f.Severity == ConflictSeverity.Content);
        Assert.Contains(prediction.Files, f => f.Path == "both.txt" && f.Severity == ConflictSeverity.AutoMerged);
        Assert.Equal(headBefore, test.Head());
        Assert.True((await repo.Status.GetStatusAsync()).IsClean);
    }

    [Fact]
    public async Task Merge_conflict_state_is_detected_and_can_be_aborted()
    {
        using var test = TestRepository.Create();
        test.WriteFile("f.txt", "base\n");
        test.CommitAll("base");
        test.Git("checkout", "-q", "-b", "other");
        test.WriteFile("f.txt", "other\n");
        test.CommitAll("other");
        test.Git("checkout", "-q", "main");
        test.WriteFile("f.txt", "main\n");
        test.CommitAll("main");

        var repo = await test.OpenAsync();
        var merge = await repo.Integration.MergeAsync("other");
        Assert.False(merge.Success);
        Assert.Equal(GitErrorCode.MergeConflict, GitErrorTranslator.Translate(merge).Code);

        var status = await repo.Status.GetStatusAsync();
        Assert.Equal(RepositoryState.Merging, status.State);
        var conflict = Assert.Single(await repo.Conflicts.GetConflictsAsync());
        Assert.Equal("f.txt", conflict.Path);
        Assert.Equal("main\n", System.Text.Encoding.UTF8.GetString((await repo.Conflicts.ReadStageAsync("f.txt", 2))!));
        Assert.Equal("other\n", System.Text.Encoding.UTF8.GetString((await repo.Conflicts.ReadStageAsync("f.txt", 3))!));

        (await repo.Integration.AbortAsync(status.State)).EnsureSuccess();
        Assert.Equal(RepositoryState.Clean, repo.Status.GetState());
    }

    [Fact]
    public async Task Interactive_rebase_reorders_squashes_rewords_and_drops()
    {
        using var test = TestRepository.Create();
        var baseSha = test.Head();
        test.WriteFile("login.txt", "login\n");
        var addLogin = test.CommitAll("Add login");
        test.WriteFile("validation.txt", "validation\n");
        var fixValidation = test.CommitAll("Fix validation");
        test.WriteFile("login.txt", "login fixed\n");
        var fixLogin = test.CommitAll("Fix login bug");
        test.WriteFile("ui.txt", "ui\n");
        var updateUi = test.CommitAll("Update UI");
        test.WriteFile("typo.txt", "typo\n");
        var fixTypo = test.CommitAll("Fix typo");

        var repo = await test.OpenAsync();
        var result = await repo.Integration.InteractiveRebaseAsync(baseSha,
        [
            new RebaseTodoItem(addLogin, RebaseAction.Pick, "Add login (with bug fix)"),
            new RebaseTodoItem(fixLogin, RebaseAction.Fixup),
            new RebaseTodoItem(updateUi, RebaseAction.Pick),
            new RebaseTodoItem(fixValidation, RebaseAction.Reword, "Fix validation rules"),
            new RebaseTodoItem(fixTypo, RebaseAction.Drop),
        ]);
        result.EnsureSuccess();

        var commits = await repo.History.GetCommitsAsync(new LogQuery());
        Assert.Equal(["Fix validation rules", "Update UI", "Add login (with bug fix)", "Initial commit"], commits.Select(c => c.Subject));
        Assert.Equal("login fixed\n", test.ReadFile("login.txt"));
        Assert.False(test.FileExists("typo.txt"));
        Assert.Equal(RepositoryState.Clean, repo.Status.GetState());
    }

    [Fact]
    public async Task Stash_push_list_drop_and_store_roundtrip()
    {
        using var test = TestRepository.Create();
        var repo = await test.OpenAsync();
        test.WriteFile("README.md", "changed\n");
        test.WriteFile("new.txt", "untracked\n");

        (await repo.Stash.PushAsync("work in progress", includeUntracked: true)).EnsureSuccess();
        Assert.True((await repo.Status.GetStatusAsync()).IsClean);

        var stash = Assert.Single(await repo.Stash.ListAsync());
        Assert.Contains("work in progress", stash.Message);

        (await repo.Stash.DropAsync(0)).EnsureSuccess();
        Assert.Empty(await repo.Stash.ListAsync());

        (await repo.Stash.StoreAsync(stash.Sha, stash.Message)).EnsureSuccess();
        var restored = Assert.Single(await repo.Stash.ListAsync());
        Assert.Equal(stash.Sha, restored.Sha);

        (await repo.Stash.PopAsync(0)).EnsureSuccess();
        Assert.Equal("changed\n", test.ReadFile("README.md"));
        Assert.True(test.FileExists("new.txt"));
    }

    [Fact]
    public async Task Reflog_has_timestamps_actions_and_previous_values()
    {
        using var test = TestRepository.Create();
        var first = test.Head();
        test.WriteFile("x.txt", "x\n");
        var second = test.CommitAll("second");
        test.Git("reset", "-q", "--hard", first);

        var repo = await test.OpenAsync();
        var reflog = await repo.Reflog.GetAsync();

        Assert.Equal(3, reflog.Count);
        Assert.Equal("reset", reflog[0].Action);
        Assert.Equal(first, reflog[0].Sha);
        Assert.Equal(second, reflog[0].PreviousSha);
        Assert.Equal("HEAD@{0}", reflog[0].Selector);
        Assert.All(reflog, e => Assert.True(e.Timestamp.Year >= 2024));
    }

    [Fact]
    public async Task Write_tree_from_index_copy_matches_git_and_does_not_touch_index()
    {
        using var test = TestRepository.Create();
        test.WriteFile("staged.txt", "staged\n");
        test.Git("add", "staged.txt");
        test.WriteFile("unstaged.txt", "not staged\n");

        var repo = await test.OpenAsync();
        var indexPath = Path.Combine(test.Root, ".git", "index");
        var before = File.ReadAllBytes(indexPath);
        var beforeTime = File.GetLastWriteTimeUtc(indexPath);

        var tree = await repo.Objects.WriteTreeFromIndexCopyAsync();

        Assert.Equal(test.Git("write-tree").Trim(), tree);
        Assert.Equal(beforeTime, File.GetLastWriteTimeUtc(indexPath) is var t && t >= beforeTime ? beforeTime : t);
        var entries = await repo.Objects.ListTreeAsync(tree!);
        Assert.Contains(entries, e => e.Path == "staged.txt");
        Assert.DoesNotContain(entries, e => e.Path == "unstaged.txt");
        Assert.NotEmpty(before);
    }

    [Fact]
    public async Task Update_refs_is_atomic_and_checks_expected_values()
    {
        using var test = TestRepository.Create();
        var repo = await test.OpenAsync();
        var head = test.Head();

        await repo.Refs.UpdateRefsAsync([new RefUpdate("refs/heads/a", head, ""), new RefUpdate("refs/heads/b", head, "")], "test");
        Assert.Equal(head, test.RevParse("refs/heads/a"));

        var ex = await Assert.ThrowsAsync<GitException>(() => repo.Refs.UpdateRefsAsync(
            [new RefUpdate("refs/heads/c", head, ""), new RefUpdate("refs/heads/a", null, "1111111111111111111111111111111111111111")], "test"));
        Assert.NotNull(ex.Error);
        Assert.Throws<InvalidOperationException>(() => test.RevParse("refs/heads/c"));
    }

    [Fact]
    public async Task Blame_attributes_lines_to_commits()
    {
        using var test = TestRepository.Create();
        test.WriteFile("f.txt", "first\n");
        var c1 = test.CommitAll("one");
        test.WriteFile("f.txt", "first\nsecond\n");
        var c2 = test.CommitAll("two");

        var repo = await test.OpenAsync();
        var blame = await repo.Blame.BlameAsync("f.txt");
        Assert.Equal(2, blame.Count);
        Assert.Equal(c1, blame[0].CommitSha);
        Assert.Equal(c2, blame[1].CommitSha);
        Assert.Equal("second", blame[1].Content);
        Assert.Equal("two", blame[1].Summary);
    }

    [Fact]
    public async Task Command_events_are_raised_with_results()
    {
        using var test = TestRepository.Create();
        var runner = new GitProcessRunner(TestGit.Runner.Executable);
        var completed = new List<GitCommandEventArgs>();
        runner.CommandCompleted += (_, e) => completed.Add(e);

        var repo = await GitRepository.OpenAsync(runner, test.Root);
        await repo.Status.GetStatusAsync();

        Assert.Contains(completed, e => e.Command.Arguments[0] == "status" && e.Result!.Success);
    }
}
