using ArChrono.Git.Diffing;
using ArChrono.Git.Errors;
using ArChrono.Git.Graph;
using ArChrono.Git.Models;
using ArChrono.Git.Parsing;
using ArChrono.Git.Process;
using ArChrono.Git.Services;

namespace ArChrono.Tests.Git;

public sealed class ParserAndGraphTests
{
    private static CommitInfo C(string sha, params string[] parents) =>
        new(sha, parents, "a", "a@x", DateTimeOffset.UnixEpoch, "a", "a@x", DateTimeOffset.UnixEpoch, sha);

    [Fact]
    public void Graph_linear_history_uses_single_lane()
    {
        var rows = CommitGraphLayout.Compute([C("c3", "c2"), C("c2", "c1"), C("c1")]);
        Assert.All(rows, r => Assert.Equal(0, r.NodeLane));
        Assert.All(rows, r => Assert.Equal(1, r.LaneCount));
        Assert.DoesNotContain(rows[0].Edges, e => e.FromAnchor == GraphAnchor.Top);
        Assert.DoesNotContain(rows[2].Edges, e => e.ToAnchor == GraphAnchor.Bottom);
    }

    [Fact]
    public void Graph_merge_opens_and_closes_second_lane()
    {
        //  m  (merge of a2 and b1)
        //  |\
        //  a2 b1
        //  |/
        //  base
        var rows = CommitGraphLayout.Compute([C("m", "a2", "b1"), C("a2", "base"), C("b1", "base"), C("base")]);

        Assert.True(rows[0].IsMerge);
        Assert.Contains(rows[0].Edges, e => e is { FromLane: 0, FromAnchor: GraphAnchor.Middle, ToLane: 1, ToAnchor: GraphAnchor.Bottom });
        Assert.Equal(0, rows[1].NodeLane);
        Assert.Equal(1, rows[2].NodeLane);
        // b1'in parent'ı (base) zaten lane 0'da bekleniyor; lane 1 base satırında lane 0'a birleşir.
        Assert.Equal(0, rows[3].NodeLane);
        Assert.Contains(rows[3].Edges, e => e is { FromLane: 1, FromAnchor: GraphAnchor.Top, ToLane: 0, ToAnchor: GraphAnchor.Middle });
        Assert.Equal(2, rows[3].LaneCount);
    }

    [Fact]
    public void Graph_layout_can_be_appended_in_pages()
    {
        var commits = new[] { C("m", "a2", "b1"), C("a2", "base"), C("b1", "base"), C("base") };
        var whole = CommitGraphLayout.Compute(commits);
        var paged = new CommitGraphLayout();
        var rows = paged.Append(commits[..2]).Concat(paged.Append(commits[2..])).ToList();
        Assert.Equal(whole.Select(r => (r.NodeLane, r.Edges.Count)), rows.Select(r => (r.NodeLane, r.Edges.Count)));
    }

    [Fact]
    public void Status_parser_handles_headers_and_unmerged_entries()
    {
        var output = string.Join('\0',
            "# branch.oid 1234567890123456789012345678901234567890",
            "# branch.head feature/x",
            "# branch.upstream origin/feature/x",
            "# branch.ab +3 -2",
            "u UU N... 100644 100644 100644 100644 1111111111111111111111111111111111111111 2222222222222222222222222222222222222222 3333333333333333333333333333333333333333 conflict file.txt",
            "? new file.txt",
            "") ;
        var (branch, entries) = StatusParser.Parse(output);
        Assert.Equal("feature/x", branch.BranchName);
        Assert.Equal(3, branch.Ahead);
        Assert.Equal(2, branch.Behind);
        Assert.True(entries[0].IsConflicted);
        Assert.Equal("conflict file.txt", entries[0].Path);
        Assert.Equal("UU", entries[0].ConflictCode);
        Assert.Equal("new file.txt", entries[1].Path);
    }

    [Fact]
    public void Status_parser_handles_detached_and_initial()
    {
        var (branch, _) = StatusParser.Parse("# branch.oid (initial)\0# branch.head main\0");
        Assert.True(branch.IsUnborn);
        var (detached, _) = StatusParser.Parse("# branch.oid abcdefabcdefabcdefabcdefabcdefabcdefabcd\0# branch.head (detached)\0");
        Assert.True(detached.IsDetached);
    }

    [Fact]
    public void Quoted_paths_are_unquoted()
    {
        Assert.Equal("a\tb.txt", ParsingHelpers.UnquotePath("\"a\\tb.txt\""));
        Assert.Equal("ä.txt", ParsingHelpers.UnquotePath("\"\\303\\244.txt\""));
        Assert.Equal("plain.txt", ParsingHelpers.UnquotePath("plain.txt"));
    }

    [Fact]
    public void Diff_parser_handles_rename_binary_and_mode_change()
    {
        const string diff = """
            diff --git a/old name.txt b/new name.txt
            similarity index 90%
            rename from old name.txt
            rename to new name.txt
            index 1111111..2222222 100644
            --- a/old name.txt
            +++ b/new name.txt
            @@ -1,2 +1,2 @@
             same
            -old
            +new
            diff --git a/image.png b/image.png
            index 3333333..4444444 100644
            Binary files a/image.png and b/image.png differ
            diff --git a/run.sh b/run.sh
            old mode 100644
            new mode 100755
            """;
        var files = DiffParser.Parse(diff);
        Assert.Equal(3, files.Count);
        Assert.Equal(ChangeKind.Renamed, files[0].Change);
        Assert.Equal("old name.txt", files[0].OldPath);
        Assert.Equal("new name.txt", files[0].NewPath);
        Assert.Equal(1, files[0].Additions);
        Assert.True(files[1].IsBinary);
        Assert.True(files[2].ModeChanged);
        Assert.Equal(0x1ED, files[2].NewMode & 0x1FF);
    }

    [Theory]
    [InlineData("a\nb\nc\n", "a\nb\nc\n")]
    [InlineData("", "x\ny\n")]
    [InlineData("x\ny\n", "")]
    [InlineData("a\nb\nc\nd\ne\nf\ng\nh\n", "a\nB\nc\nd\ne\nf\nG\nh\ni\n")]
    [InlineData("1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n11\n12\n", "0\n1\n2\n4\n5\n6\n7\n8\n9\n10\n12\n13\n")]
    public void Text_diff_hunks_reconstruct_both_sides(string oldText, string newText)
    {
        var diff = TextDiff.Compare("f", "f", System.Text.Encoding.UTF8.GetBytes(oldText), System.Text.Encoding.UTF8.GetBytes(newText), contextLines: 100);
        var oldLines = TextDiff.SplitLines(oldText);
        var newLines = TextDiff.SplitLines(newText);

        if (oldText == newText)
        {
            Assert.Empty(diff.Hunks);
            return;
        }

        var hunk = Assert.Single(diff.Hunks);
        Assert.Equal(oldLines, hunk.Lines.Where(l => l.Kind != DiffLineKind.Added).Select(l => l.Text));
        Assert.Equal(newLines, hunk.Lines.Where(l => l.Kind != DiffLineKind.Removed).Select(l => l.Text));
    }

    [Fact]
    public void Text_diff_splits_distant_changes_into_hunks()
    {
        var oldText = string.Join("\n", Enumerable.Range(1, 40)) + "\n";
        var newText = oldText.Replace("\n5\n", "\nfive\n").Replace("\n35\n", "\nthirty-five\n");
        var diff = TextDiff.Compare("f", "f", System.Text.Encoding.UTF8.GetBytes(oldText), System.Text.Encoding.UTF8.GetBytes(newText));
        Assert.Equal(2, diff.Hunks.Count);
        Assert.Equal(2, diff.Additions);
        Assert.Equal(2, diff.Deletions);
        Assert.Equal(2, diff.Hunks[0].OldStart);
    }

    [Theory]
    [InlineData("error: Your local changes to the following files would be overwritten by checkout:\n\ta.txt", GitErrorCode.LocalChangesWouldBeOverwritten)]
    [InlineData("CONFLICT (content): Merge conflict in a.txt\nAutomatic merge failed; fix conflicts and then commit the result.", GitErrorCode.MergeConflict)]
    [InlineData(" ! [rejected]        main -> main (fetch first)\nerror: failed to push some refs", GitErrorCode.PushRejectedNonFastForward)]
    [InlineData("git@github.com: Permission denied (publickey).\nfatal: Could not read from remote repository.", GitErrorCode.AuthenticationFailed)]
    [InlineData("fatal: Unable to create '/x/.git/index.lock': File exists.", GitErrorCode.RepositoryLocked)]
    [InlineData("error: The branch 'feature' is not fully merged.", GitErrorCode.BranchNotFullyMerged)]
    [InlineData("fatal: a branch named 'main' already exists", GitErrorCode.AlreadyExists)]
    [InlineData("fatal: something entirely new", GitErrorCode.Unknown)]
    public void Error_translator_maps_common_failures(string stderr, GitErrorCode expected)
    {
        var error = GitErrorTranslator.Translate(stderr, "git test", 1);
        Assert.Equal(expected, error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Title));
        Assert.Contains(stderr.Split('\n')[0].Trim(), error.RawOutput);
    }

    [Fact]
    public void Secret_masker_hides_url_credentials()
    {
        Assert.Equal("https://***@github.com/org/repo.git", SecretMasker.Mask("https://user:ghp_secret@github.com/org/repo.git"));
        Assert.Equal("git fetch https://***@host/x", new GitCommand("/tmp", ["fetch", "https://token@host/x"]).DisplayText);
    }

    [Fact]
    public void Rebase_todo_builds_squash_groups_with_single_amend()
    {
        var dir = Path.Combine(Path.GetTempPath(), "archrono-todo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var todo = IntegrationService.BuildTodo(
            [
                new RebaseTodoItem("aaa", RebaseAction.Pick),
                new RebaseTodoItem("bbb", RebaseAction.Squash),
                new RebaseTodoItem("ccc", RebaseAction.Squash, "Combined message"),
                new RebaseTodoItem("ddd", RebaseAction.Drop),
                new RebaseTodoItem("eee", RebaseAction.Edit),
            ], dir);
            var lines = todo.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("drop ddd", lines[0]);
            Assert.Equal("pick aaa", lines[1]);
            Assert.Equal("squash bbb", lines[2]);
            Assert.Equal("squash ccc", lines[3]);
            Assert.StartsWith("exec git commit --amend --only -F ", lines[4]);
            Assert.Equal("edit eee", lines[5]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Merge_tree_parser_classifies_structural_conflicts()
    {
        const string output = "4b825dc642cb6eb9a060e54bf8d69288fbee4904\ndeleted.txt\n\nCONFLICT (modify/delete): deleted.txt deleted in theirs and modified in ours.\n";
        var prediction = MergeTreeParser.Parse(output, 1);
        var file = Assert.Single(prediction.Files);
        Assert.Equal(ConflictSeverity.Structural, file.Severity);
        Assert.Equal(1, prediction.StructuralConflicts);
    }
}
