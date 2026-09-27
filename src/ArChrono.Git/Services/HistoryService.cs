using System.Text;
using ArChrono.Git.Errors;
using ArChrono.Git.Models;
using ArChrono.Git.Parsing;
using ArChrono.Git.Process;
using ArChrono.Localization;

namespace ArChrono.Git.Services;

public sealed record LogQuery
{
    /// <summary>Boşsa kullanıcı ref'leri: --branches --remotes --tags (+ detached HEAD). refs/archrono/* hiçbir zaman dahil edilmez.</summary>
    public IReadOnlyList<string>? Revisions { get; init; }
    public int MaxCount { get; init; } = 500;
    public int Skip { get; init; }
    public string? Path { get; init; }
    public bool FirstParent { get; init; }
    public bool TopoOrder { get; init; } = true;
}

public sealed class HistoryService(IGitRunner runner, RepositoryInfo repository, RefService refs) : GitServiceBase(runner, repository)
{
    private const string CommitFields = "%H%x1f%P%x1f%an%x1f%ae%x1f%at%x1f%cn%x1f%ce%x1f%ct%x1f%s";

    public async Task<IReadOnlyList<CommitInfo>> GetCommitsAsync(LogQuery query, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "log", "--format=" + LogParser.CommitFormat, $"--max-count={query.MaxCount}" };
        if (query.Skip > 0) args.Add($"--skip={query.Skip}");
        args.Add(query.TopoOrder ? "--topo-order" : "--date-order");
        if (query.FirstParent) args.Add("--first-parent");

        if (query.Revisions is { Count: > 0 } revisions)
        {
            args.AddRange(revisions);
        }
        else
        {
            var head = await refs.GetHeadAsync(cancellationToken).ConfigureAwait(false);
            if (head.IsUnborn && !(await refs.GetRefTargetsAsync(cancellationToken, "refs/heads", "refs/remotes", "refs/tags").ConfigureAwait(false)).Any())
                return [];
            args.AddRange(["--branches", "--remotes", "--tags"]);
            if (head.IsDetached) args.Add("HEAD");
        }

        if (query.Path is not null)
        {
            args.Add("--");
            args.Add(query.Path);
        }

        var result = await ExecuteAsync(args, cancellationToken, readOnly: true, environment: LiteralPathspecs).ConfigureAwait(false);
        if (!result.Success && result.StandardError.Contains("does not have any commits", StringComparison.Ordinal)) return [];
        return LogParser.ParseCommits(result.EnsureSuccess().StandardOutput);
    }

    public async Task<CommitInfo?> GetCommitAsync(string revision, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(["log", "-1", "--format=" + LogParser.CommitFormat, revision, "--"], cancellationToken, readOnly: true).ConfigureAwait(false);
        return result.Success ? LogParser.ParseCommits(result.StandardOutput).FirstOrDefault() : null;
    }

    /// <summary>Verilen sha'ların commit bilgileri (sırası korunur, bulunamayanlar atlanır).</summary>
    public async Task<IReadOnlyList<CommitInfo>> GetCommitsByShaAsync(IEnumerable<string> shas, CancellationToken cancellationToken = default)
    {
        var list = shas.Distinct().ToList();
        if (list.Count == 0) return [];
        var input = Encoding.UTF8.GetBytes(string.Join('\n', list) + "\n");
        var result = await ExecuteAsync(["log", "--no-walk=unsorted", "--ignore-missing", "--stdin", "--format=" + LogParser.CommitFormat],
            cancellationToken, readOnly: true, standardInput: input).ConfigureAwait(false);
        return result.Success ? LogParser.ParseCommits(result.StandardOutput) : [];
    }

    public async Task<CommitDetails> GetCommitDetailsAsync(string sha, CancellationToken cancellationToken = default)
    {
        var show = await ExecuteAsync(["log", "-1", $"--format={CommitFields}%x1f%b%x1e", sha, "--"], cancellationToken, readOnly: true).ConfigureAwait(false);
        var record = show.EnsureSuccess().StandardOutput.TrimEnd('\n', ParsingHelpers.RecordSeparator);
        var commit = LogParser.ParseCommit(record) ?? throw new GitException(GitError.Simple(GitErrorCode.ReferenceNotFound, Loc.T("Commit not found.", "Commit bulunamadı."), sha));
        var fields = record.Split(ParsingHelpers.FieldSeparator);
        var body = string.Join(ParsingHelpers.FieldSeparator, fields.Skip(9)).Trim();

        var parentArgs = commit.Parents.Count > 0 ? new[] { commit.Parents[0], sha } : new[] { "--root", sha };
        var nameStatusTask = ExecuteAsync(Args(["diff-tree", "-r", "-z", "-M", "--no-commit-id", "--name-status"], parentArgs), cancellationToken, readOnly: true);
        var numstatTask = ExecuteAsync(Args(["diff-tree", "-r", "-z", "-M", "--no-commit-id", "--numstat"], parentArgs), cancellationToken, readOnly: true);
        await Task.WhenAll(nameStatusTask, numstatTask).ConfigureAwait(false);

        var nameStatus = LogParser.ParseNameStatusZ((await nameStatusTask).EnsureSuccess().StandardOutput);
        var numstat = LogParser.ParseNumstatZ((await numstatTask).EnsureSuccess().StandardOutput);

        var files = nameStatus.Select(entry =>
        {
            numstat.TryGetValue(entry.Path, out var stats);
            return new CommitFileChange(entry.Path, entry.OldPath, entry.Change, stats.Additions, stats.Deletions, stats.IsBinary);
        }).ToList();

        return new CommitDetails(commit, body, files);
    }

    public async Task<IReadOnlyList<FileHistoryEntry>> GetFileHistoryAsync(string path, int maxCount = 300, string revision = "HEAD", CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(
            ["log", "--follow", "-M", $"--max-count={maxCount}", $"--format=%x1e{CommitFields}", "--name-status", revision, "--", path],
            cancellationToken, readOnly: true, environment: LiteralPathspecs).ConfigureAwait(false);
        if (!result.Success && result.StandardError.Contains("does not have any commits", StringComparison.Ordinal)) return [];
        return LogParser.ParseFileHistory(result.EnsureSuccess().StandardOutput, path);
    }

    /// <summary>Commit mesajı, yazar ve sha önekinde arama.</summary>
    public async Task<IReadOnlyList<CommitInfo>> SearchAsync(string text, int maxCount = 50, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var found = new List<CommitInfo>();

        if (text.Length >= 4 && text.All(Uri.IsHexDigit))
        {
            var bySha = await refs.ResolveCommitAsync(text, cancellationToken).ConfigureAwait(false);
            if (bySha is not null && await GetCommitAsync(bySha, cancellationToken).ConfigureAwait(false) is { } commit) found.Add(commit);
        }

        var baseArgs = new[] { "log", "--branches", "--remotes", "--tags", "--date-order", $"--max-count={maxCount}", "--format=" + LogParser.CommitFormat, "-i", "--fixed-strings" };
        var byMessage = ExecuteAsync(Args(baseArgs, ["--grep=" + text]), cancellationToken, readOnly: true);
        var byAuthor = ExecuteAsync(Args(baseArgs, ["--author=" + text]), cancellationToken, readOnly: true);
        await Task.WhenAll(byMessage, byAuthor).ConfigureAwait(false);

        foreach (var result in new[] { await byMessage, await byAuthor })
        {
            if (result.Success) found.AddRange(LogParser.ParseCommits(result.StandardOutput));
        }
        return found.DistinctBy(c => c.Sha).OrderByDescending(c => c.CommitDate).Take(maxCount).ToList();
    }

    /// <summary>Kodda ekleme/silme araması (<c>git log -S</c>). Büyük repository'lerde yavaş olabilir.</summary>
    public async Task<IReadOnlyList<CommitInfo>> SearchChangedCodeAsync(string text, int maxCount = 30, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var result = await ExecuteAsync(
            ["log", "--branches", "--remotes", "--tags", $"--max-count={maxCount}", "--format=" + LogParser.CommitFormat, "-S" + text],
            cancellationToken, readOnly: true, timeout: TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        return result.Success ? LogParser.ParseCommits(result.StandardOutput) : [];
    }

    /// <summary>İki revizyon arasındaki commit'ler: <c>base..head</c> (base hariç), eskiden yeniye.</summary>
    public async Task<IReadOnlyList<CommitInfo>> GetRangeAsync(string baseRevision, string headRevision, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(
            ["log", "--reverse", "--topo-order", "--format=" + LogParser.CommitFormat, $"{baseRevision}..{headRevision}", "--"],
            cancellationToken, readOnly: true).ConfigureAwait(false);
        return LogParser.ParseCommits(result.EnsureSuccess().StandardOutput);
    }

    public async Task<(int Ahead, int Behind)> CountAheadBehindAsync(string left, string right, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(["rev-list", "--left-right", "--count", $"{left}...{right}", "--"], cancellationToken, readOnly: true).ConfigureAwait(false);
        if (!result.Success) return (0, 0);
        var parts = result.StandardOutput.Trim().Split('\t', ' ');
        return parts.Length >= 2 && int.TryParse(parts[0], out var ahead) && int.TryParse(parts[1], out var behind) ? (ahead, behind) : (0, 0);
    }

    public async Task<string?> GetMergeBaseAsync(string a, string b, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(["merge-base", a, b], cancellationToken, readOnly: true).ConfigureAwait(false);
        return result.Success ? result.StandardOutput.Trim() : null;
    }

    public async Task<bool> IsAncestorAsync(string ancestor, string descendant, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(["merge-base", "--is-ancestor", ancestor, descendant], cancellationToken, readOnly: true).ConfigureAwait(false);
        return result.ExitCode == 0;
    }
}
