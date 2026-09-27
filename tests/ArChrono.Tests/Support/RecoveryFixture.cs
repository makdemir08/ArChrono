using ArChrono.Git;
using ArChrono.Recovery;
using ArChrono.Recovery.Operations;
using ArChrono.Storage.Stores;

namespace ArChrono.Tests.Support;

/// <summary>Geçici repository + geçici uygulama veri dizini (SQLite + content store) + RecoveryEngine.</summary>
public sealed class RecoveryFixture : IDisposable
{
    private readonly string _dataDirectory;

    private RecoveryFixture(TestRepository test, string dataDirectory, ArChronoStorage storage, RecoveryEngine engine, GitRepository repository, long repositoryId)
    {
        Test = test;
        _dataDirectory = dataDirectory;
        Storage = storage;
        Engine = engine;
        Repository = repository;
        RepositoryId = repositoryId;
    }

    public TestRepository Test { get; }
    public ArChronoStorage Storage { get; }
    public RecoveryEngine Engine { get; }
    public GitRepository Repository { get; }
    public long RepositoryId { get; }
    public OperationContext Context => new(Repository, RepositoryId);

    public static async Task<RecoveryFixture> CreateAsync(bool withInitialCommit = true)
    {
        var test = TestRepository.Create(withInitialCommit);
        var data = TestGit.CreateTempDirectory("data");
        var storage = ArChronoStorage.OpenAndMigrate(Path.Combine(data, "archrono.db"));
        var engine = new RecoveryEngine(storage, Path.Combine(data, "store"));
        var repository = await test.OpenAsync();
        var record = storage.Repositories.Upsert(repository.Info.RootPath, repository.Info.GitDir, repository.Info.CommonDir, repository.Info.Name, repository.Info.ObjectFormat);
        return new RecoveryFixture(test, data, storage, engine, repository, record.Id);
    }

    public Task<OperationOutcome> RunAsync(IGitOperation operation) => Engine.RunAsync(Context, operation);

    public async Task<OperationOutcome> UndoAsync()
    {
        var outcome = await Engine.UndoLastAsync(Context);
        Assert.NotNull(outcome);
        Assert.True(outcome!.Succeeded, $"Undo failed: {outcome.Error?.Title} {outcome.Error?.Explanation} {outcome.VerificationProblem} {string.Join(";", outcome.Validation.Problems)}");
        return outcome;
    }

    /// <summary>Çalışma alanının karşılaştırılabilir özeti: HEAD, branch, index ve dosya içerikleri.</summary>
    public WorkspaceState Capture()
    {
        var files = Directory.EnumerateFiles(Test.Root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(Test.Root, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToDictionary(f => f, f => File.ReadAllText(Path.Combine(Test.Root, f)));
        string head;
        try
        {
            head = Test.Head();
        }
        catch (InvalidOperationException)
        {
            head = "(unborn)";
        }
        var branch = Test.Git("symbolic-ref", "-q", "--short", "HEAD").Trim();
        var index = Test.Git("ls-files", "-s");
        var refs = Test.Git("for-each-ref", "--format=%(refname) %(objectname)", "refs/heads", "refs/tags");
        var stashes = Test.Git("stash", "list", "--format=%H");
        return new WorkspaceState(head, branch, index, refs, stashes, files);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Test.Dispose();
        TestGit.DeleteDirectory(_dataDirectory);
    }
}

public sealed record WorkspaceState(string Head, string Branch, string Index, string Refs, string Stashes, IReadOnlyDictionary<string, string> Files)
{
    public void AssertEqual(WorkspaceState other)
    {
        Assert.Equal(Head, other.Head);
        Assert.Equal(Branch, other.Branch);
        Assert.Equal(Index, other.Index);
        Assert.Equal(Refs, other.Refs);
        Assert.Equal(Stashes, other.Stashes);
        Assert.Equal(Files.Keys, other.Files.Keys);
        foreach (var (path, content) in Files) Assert.Equal(content, other.Files[path]);
    }
}
