using ArChrono.Git.Errors;
using ArChrono.Git.Models;
using ArChrono.Git.Process;
using ArChrono.Git.Services;
using ArChrono.Localization;

namespace ArChrono.Git;

/// <summary>Bir çalışma dizini için Git servislerini bir araya getiren facade.</summary>
public sealed class GitRepository
{
    private GitRepository(IGitRunner runner, RepositoryInfo info)
    {
        Runner = runner;
        Info = info;
        Refs = new RefService(runner, info);
        Status = new StatusService(runner, info);
        History = new HistoryService(runner, info, Refs);
        Branches = new BranchService(runner, info);
        WorkingTree = new WorkingTreeService(runner, info);
        Diff = new DiffService(runner, info);
        Integration = new IntegrationService(runner, info);
        Stash = new StashService(runner, info);
        Reflog = new ReflogService(runner, info);
        Remotes = new RemoteService(runner, info);
        Tags = new TagService(runner, info);
        Blame = new BlameService(runner, info);
        Objects = new ObjectService(runner, info);
        Conflicts = new ConflictService(runner, info, Objects);
        Worktrees = new WorktreeService(runner, info);
        Bisect = new BisectService(runner, info);
        Config = new ConfigService(runner, info);
    }

    public IGitRunner Runner { get; }
    public RepositoryInfo Info { get; }

    public RefService Refs { get; }
    public StatusService Status { get; }
    public HistoryService History { get; }
    public BranchService Branches { get; }
    public WorkingTreeService WorkingTree { get; }
    public DiffService Diff { get; }
    public IntegrationService Integration { get; }
    public StashService Stash { get; }
    public ReflogService Reflog { get; }
    public RemoteService Remotes { get; }
    public TagService Tags { get; }
    public BlameService Blame { get; }
    public ObjectService Objects { get; }
    public ConflictService Conflicts { get; }
    public WorktreeService Worktrees { get; }
    public BisectService Bisect { get; }
    public ConfigService Config { get; }

    /// <summary>Verilen dizini içeren repository'yi açar (üst dizinlerde arar).</summary>
    public static async Task<GitRepository> OpenAsync(IGitRunner runner, string path, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(path))
            throw new GitException(GitError.Simple(GitErrorCode.NotARepository, Loc.T("This folder does not exist.", "Bu klasör mevcut değil."), path));

        var result = await runner.RunAsync(new GitCommand(path,
        [
            "rev-parse", "--path-format=absolute", "--show-toplevel", "--git-dir", "--git-common-dir", "--show-object-format",
        ]) { ReadOnly = true, IsInternal = true }, cancellationToken).ConfigureAwait(false);

        var lines = result.EnsureSuccess().StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 4)
            throw new GitException(GitErrorTranslator.Translate(result.CombinedOutput, result.Command.DisplayText, result.ExitCode));

        var info = new RepositoryInfo(
            NormalizePath(lines[0]),
            NormalizePath(lines[1]),
            NormalizePath(lines[2]),
            lines[3] == "sha256" ? "sha256" : "sha1");
        return new GitRepository(runner, info);
    }

    internal static string NormalizePath(string path) =>
        Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar)).TrimEnd(Path.DirectorySeparatorChar);
}

/// <summary>Repository dışı işlemler: init, clone.</summary>
public sealed class GitClient(IGitRunner runner)
{
    public IGitRunner Runner { get; } = runner;

    public async Task<GitRepository> InitAsync(string path, string initialBranch = "main", CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(path);
        var result = await Runner.RunAsync(new GitCommand(path, ["init", "--initial-branch=" + initialBranch]), cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess();
        return await GitRepository.OpenAsync(Runner, path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitRepository> CloneAsync(string url, string destination, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(destination)) ?? throw new ArgumentException("Invalid destination.", nameof(destination));
        Directory.CreateDirectory(parent);
        var result = await Runner.RunAsync(new GitCommand(parent, ["clone", "--progress", url, destination])
        {
            Progress = progress,
            Timeout = TimeSpan.FromHours(2),
        }, cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess();
        return await GitRepository.OpenAsync(Runner, destination, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetGlobalConfigAsync(string key, CancellationToken cancellationToken = default)
    {
        var result = await Runner.RunAsync(new GitCommand(Path.GetTempPath(), ["config", "--global", "--get", key]) { ReadOnly = true, IsInternal = true }, cancellationToken).ConfigureAwait(false);
        return result.Success ? result.StandardOutput.Trim() : null;
    }

    public async Task SetGlobalConfigAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        var result = await Runner.RunAsync(new GitCommand(Path.GetTempPath(), ["config", "--global", key, value]), cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess();
    }
}
