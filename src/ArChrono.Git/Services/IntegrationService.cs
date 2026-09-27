using System.Text;
using ArChrono.Git.Errors;
using ArChrono.Git.Models;
using ArChrono.Git.Parsing;
using ArChrono.Git.Process;
using ArChrono.Localization;

namespace ArChrono.Git.Services;

public enum FastForwardMode
{
    Auto,
    Only,
    Never,
}

public enum ResetMode
{
    Soft,
    Mixed,
    Hard,
}

public enum RebaseAction
{
    Pick,
    Reword,
    Edit,
    Squash,
    Fixup,
    Drop,
}

public sealed record RebaseTodoItem(string Sha, RebaseAction Action, string? NewMessage = null);

/// <summary>Branch'leri birleştiren ve geçmişi yeniden yazan işlemler: merge, rebase, cherry-pick, revert, reset.</summary>
public sealed class IntegrationService(IGitRunner runner, RepositoryInfo repository) : GitServiceBase(runner, repository)
{
    public Task<GitCommandResult> MergeAsync(string revision, FastForwardMode fastForward = FastForwardMode.Auto, bool squash = false,
        bool allowUnrelatedHistories = false, string? message = null, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "merge", "--no-edit" };
        if (fastForward == FastForwardMode.Only) args.Add("--ff-only");
        if (fastForward == FastForwardMode.Never) args.Add("--no-ff");
        if (squash) args.Add("--squash");
        if (allowUnrelatedHistories) args.Add("--allow-unrelated-histories");
        if (message is not null)
        {
            args.Add("-m");
            args.Add(message);
        }
        args.Add(revision);
        return ExecuteAsync(args, cancellationToken);
    }

    /// <summary>Çalışma alanına ve index'e dokunmadan merge sonucunu ve conflict'leri tahmin eder.</summary>
    public async Task<ConflictPrediction> PredictMergeAsync(string ours, string theirs, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(["merge-tree", "--write-tree", "--name-only", "--messages", ours, theirs], cancellationToken, readOnly: true).ConfigureAwait(false);
        if (result.ExitCode is not (0 or 1)) result.EnsureSuccess();
        return MergeTreeParser.Parse(result.StandardOutput, result.ExitCode);
    }

    public Task<GitCommandResult> RebaseAsync(string upstream, string? onto = null, bool autostash = false, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "rebase" };
        if (autostash) args.Add("--autostash");
        if (onto is not null)
        {
            args.Add("--onto");
            args.Add(onto);
        }
        args.Add(upstream);
        return ExecuteAsync(args, cancellationToken);
    }

    /// <summary>
    /// Editörsüz interactive rebase. Todo listesi uygulama tarafından yazılır; <paramref name="baseRevision"/> null ise --root.
    /// Öğeler eskiden yeniye sıralı olmalıdır.
    /// </summary>
    public async Task<GitCommandResult> InteractiveRebaseAsync(string? baseRevision, IReadOnlyList<RebaseTodoItem> items, bool autostash = false, CancellationToken cancellationToken = default)
    {
        ValidateTodo(items);

        var workDir = Path.Combine(Repository.GitDir, "archrono", "rebase", Guid.NewGuid().ToString("N"));
        CleanupOldRebaseFiles();
        Directory.CreateDirectory(workDir);

        var todoPath = Path.Combine(workDir, "git-rebase-todo");
        await File.WriteAllTextAsync(todoPath, BuildTodo(items, workDir), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        var args = new List<string> { "rebase", "-i" };
        if (autostash) args.Add("--autostash");
        args.Add(baseRevision ?? "--root");

        var environment = new Dictionary<string, string>
        {
            ["GIT_SEQUENCE_EDITOR"] = "cp " + ShellQuote(todoPath),
        };
        return await ExecuteAsync(args, cancellationToken, environment: environment).ConfigureAwait(false);
    }

    internal static void ValidateTodo(IReadOnlyList<RebaseTodoItem> items)
    {
        var firstKept = items.FirstOrDefault(i => i.Action != RebaseAction.Drop);
        if (firstKept is not null && firstKept.Action is RebaseAction.Squash or RebaseAction.Fixup)
            throw new GitException(GitError.Simple(GitErrorCode.Unknown, Loc.T("The first commit can't be squashed.", "İlk commit squash edilemez."),
                Loc.T("Squash and fixup combine a commit into the one before it, but there is no commit before the first one.", "Squash ve fixup bir commit'i kendinden öncekiyle birleştirir, ancak ilk commit'ten önce commit yok.")));
    }

    internal static string BuildTodo(IReadOnlyList<RebaseTodoItem> items, string workDir)
    {
        var todo = new StringBuilder();
        var messageIndex = 0;

        string AmendWith(string message)
        {
            var file = Path.Combine(workDir, $"message-{messageIndex++}.txt");
            File.WriteAllText(file, message, new UTF8Encoding(false));
            return "exec git commit --amend --only -F " + ShellQuote(file);
        }

        var kept = items.Where(i => i.Action != RebaseAction.Drop).ToList();
        foreach (var dropped in items.Where(i => i.Action == RebaseAction.Drop))
            todo.Append("drop ").Append(dropped.Sha).Append('\n');

        for (var i = 0; i < kept.Count; i++)
        {
            var item = kept[i];
            var verb = item.Action switch
            {
                RebaseAction.Squash => "squash",
                RebaseAction.Fixup => "fixup",
                RebaseAction.Edit => "edit",
                _ => "pick",
            };
            todo.Append(verb).Append(' ').Append(item.Sha).Append('\n');

            var nextIsSquash = i + 1 < kept.Count && kept[i + 1].Action is RebaseAction.Squash or RebaseAction.Fixup;
            if (nextIsSquash) continue;

            // Grup sonu: grubun başındaki ya da gruptaki son özel mesaj uygulanır.
            var groupStart = i;
            while (groupStart > 0 && kept[groupStart].Action is RebaseAction.Squash or RebaseAction.Fixup) groupStart--;
            var message = kept.Skip(groupStart).Take(i - groupStart + 1).LastOrDefault(g => g.NewMessage is not null)?.NewMessage;
            if (message is not null && (item.Action != RebaseAction.Edit || groupStart != i))
                todo.Append(AmendWith(message)).Append('\n');
        }
        return todo.ToString();
    }

    private void CleanupOldRebaseFiles()
    {
        var root = Path.Combine(Repository.GitDir, "archrono", "rebase");
        if (!Directory.Exists(root) || Directory.Exists(Path.Combine(Repository.GitDir, "rebase-merge"))) return;
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string ShellQuote(string path) => "'" + path.Replace('\\', '/').Replace("'", "'\\''") + "'";

    public Task<GitCommandResult> CherryPickAsync(IReadOnlyList<CommitInfo> commits, bool recordOrigin = false, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "cherry-pick" };
        if (recordOrigin) args.Add("-x");
        if (commits.Any(c => c.IsMerge)) args.AddRange(["-m", "1"]);
        args.AddRange(commits.Select(c => c.Sha));
        return ExecuteAsync(args, cancellationToken);
    }

    public Task<GitCommandResult> RevertAsync(CommitInfo commit, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "revert", "--no-edit" };
        if (commit.IsMerge) args.AddRange(["-m", "1"]);
        args.Add(commit.Sha);
        return ExecuteAsync(args, cancellationToken);
    }

    public Task<GitCommandResult> ResetAsync(string revision, ResetMode mode, CancellationToken cancellationToken = default) =>
        ExecuteAsync(["reset", mode switch
        {
            ResetMode.Soft => "--soft",
            ResetMode.Hard => "--hard",
            _ => "--mixed",
        }, "-q", revision, "--"], cancellationToken);

    public Task<GitCommandResult> AbortAsync(RepositoryState state, CancellationToken cancellationToken = default) =>
        ExecuteAsync([SequenceCommand(state), "--abort"], cancellationToken);

    public Task<GitCommandResult> ContinueAsync(RepositoryState state, CancellationToken cancellationToken = default) =>
        ExecuteAsync([SequenceCommand(state), "--continue"], cancellationToken);

    public Task<GitCommandResult> SkipAsync(RepositoryState state, CancellationToken cancellationToken = default) =>
        ExecuteAsync([SequenceCommand(state), "--skip"], cancellationToken);

    private static string SequenceCommand(RepositoryState state) => state switch
    {
        RepositoryState.Merging => "merge",
        RepositoryState.Rebasing => "rebase",
        RepositoryState.ApplyingPatches => "am",
        RepositoryState.CherryPicking => "cherry-pick",
        RepositoryState.Reverting => "revert",
        _ => throw new GitException(GitError.Simple(GitErrorCode.NoOperationInProgress, Loc.T("There is nothing to continue or abort.", "Devam ettirilecek veya iptal edilecek bir işlem yok."),
            Loc.T("No merge, rebase, cherry-pick or revert is in progress.", "Devam eden bir merge, rebase, cherry-pick veya revert yok."))),
    };
}
