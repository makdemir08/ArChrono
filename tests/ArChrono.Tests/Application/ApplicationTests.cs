using ArChrono.AI.Providers;
using ArChrono.Application;
using ArChrono.Application.Ai;
using ArChrono.Application.Repositories;
using ArChrono.Application.Search;
using ArChrono.Application.Settings;
using ArChrono.Git.Services;
using ArChrono.Platform;
using ArChrono.Platform.Credentials;
using ArChrono.Storage.Records;
using ArChrono.Storage.Stores;
using ArChrono.Tests.Support;

namespace ArChrono.Tests.Application;

public sealed class ApplicationTests
{
    private static AppPaths TempPaths(out string root)
    {
        root = TestGit.CreateTempDirectory("app");
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        return new AppPaths(root, Path.Combine(root, "logs"));
    }

    [Fact]
    public async Task Headless_end_to_end_mistake_then_undo_through_application_layer()
    {
        var paths = TempPaths(out var root);
        using var test = TestRepository.Create();
        await using (var app = AppServices.Create(paths))
        {
            app.Settings.Update(s => s with { Git = s.Git with { AutoFetch = false } });
            var session = await app.OpenSessionAsync(test.Root);
            Assert.Same(session, await app.OpenSessionAsync(test.Root));

            test.WriteFile("feature.cs", "// new feature\n");
            await session.Actions.StageAsync(["feature.cs"]);
            var commit = await session.Actions.CommitAsync("Add feature", amend: false);
            Assert.True(commit.Succeeded);

            test.WriteFile("feature.cs", "// uncommitted improvement\n");
            OperationCompletedCapture captured = new(session);
            var reset = await session.Actions.ResetAsync("HEAD~1", ResetMode.Hard, "main");
            Assert.True(reset.Succeeded);
            Assert.False(test.FileExists("feature.cs"));
            Assert.Equal(1, captured.Count);

            var undoState = session.GetUndoState();
            Assert.True(undoState.CanUndo);
            Assert.Equal("reset", undoState.Undoable!.Kind);

            var plan = await session.PlanUndoAsync(undoState.Undoable);
            Assert.False(plan.IsEmpty);
            var undo = await session.ExecutePlanAsync(plan, undoState.Undoable);
            Assert.True(undo.Succeeded);
            Assert.Equal("// uncommitted improvement\n", test.ReadFile("feature.cs"));
            Assert.True(session.GetUndoState().CanRedo);

            Assert.Contains(app.Console.Entries, e => e.Command.Arguments[0] == "reset");
            Assert.DoesNotContain(app.Console.Entries, e => e.Command.Arguments[0] == "write-tree");
        }
        TestGit.DeleteDirectory(root);
    }

    private sealed class OperationCompletedCapture
    {
        public int Count;

        public OperationCompletedCapture(RepositorySession session) => session.OperationCompleted += (_, _) => Count++;
    }

    [Fact]
    public async Task Time_machine_service_saves_snapshot_and_restores_file_as_undoable_operation()
    {
        var paths = TempPaths(out var root);
        using var test = TestRepository.Create();
        await using (var app = AppServices.Create(paths))
        {
            app.Settings.Update(s => s with { Git = s.Git with { AutoFetch = false } });
            var session = await app.OpenSessionAsync(test.Root);
            test.WriteFile("draft.md", "first draft\n");
            var saved = await session.TimeMachine.SaveSnapshotNowAsync("Before refactor");
            test.WriteFile("draft.md", "rewritten badly\n");

            var (snapshot, content) = await session.TimeMachine.ReadFileAtAsync("draft.md", DateTimeOffset.Now);
            Assert.Equal(saved.Snapshot.Id, snapshot!.Id);
            Assert.Equal("first draft\n", System.Text.Encoding.UTF8.GetString(content!));

            var restore = await session.TimeMachine.RestoreFilesAsync(saved.Snapshot, ["draft.md"]);
            Assert.True(restore.Succeeded);
            Assert.Equal("first draft\n", test.ReadFile("draft.md"));

            var undo = session.GetUndoState().Undoable!;
            Assert.Equal("restore", undo.Kind);
            Assert.True((await session.ExecutePlanAsync(await session.PlanUndoAsync(undo), undo)).Succeeded);
            Assert.Equal("rewritten badly\n", test.ReadFile("draft.md"));
        }
        TestGit.DeleteDirectory(root);
    }

    [Fact]
    public async Task Search_finds_branches_files_commits_and_recovery_points()
    {
        var paths = TempPaths(out var root);
        using var test = TestRepository.Create();
        test.WriteFile("src/Services/TokenService.cs", "class TokenService {}\n");
        test.CommitAll("Add refresh token support");
        test.Git("branch", "feature/payment");
        await using (var app = AppServices.Create(paths))
        {
            app.Settings.Update(s => s with { Git = s.Git with { AutoFetch = false } });
            var session = await app.OpenSessionAsync(test.Root);
            await session.CreateManualRecoveryPointAsync("Before payment experiment");

            var results = await session.Search.SearchAsync("payment");
            Assert.Contains(results, r => r.Kind == SearchResultKind.Branch && r.Title == "feature/payment");
            Assert.Contains(results, r => r.Kind == SearchResultKind.RecoveryPoint);

            var files = await session.Search.SearchAsync("tokserv");
            Assert.Equal("TokenService.cs", files.First(r => r.Kind == SearchResultKind.File).Title);

            var commits = await session.Search.SearchAsync("refresh token");
            Assert.Contains(commits, r => r.Kind == SearchResultKind.Commit);
        }
        TestGit.DeleteDirectory(root);
    }

    [Fact]
    public void Watcher_classifies_git_metadata_and_ignores_noise()
    {
        var root = TestGit.CreateTempDirectory("watch");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            using var watcher = new RepositoryWatcher(root, Path.Combine(root, ".git"), Path.Combine(root, ".git"));
            Assert.Equal(RepositoryChange.WorkingTree, watcher.Classify(Path.Combine(root, "src", "a.cs")));
            Assert.Equal(RepositoryChange.Index, watcher.Classify(Path.Combine(root, ".git", "index")));
            Assert.Equal(RepositoryChange.Refs, watcher.Classify(Path.Combine(root, ".git", "refs", "heads", "main")));
            Assert.Equal(RepositoryChange.State, watcher.Classify(Path.Combine(root, ".git", "MERGE_HEAD")));
            Assert.Equal(RepositoryChange.None, watcher.Classify(Path.Combine(root, ".git", "index.lock")));
            Assert.Equal(RepositoryChange.None, watcher.Classify(Path.Combine(root, ".git", "objects", "ab", "cdef")));
            Assert.Equal(RepositoryChange.None, watcher.Classify(Path.Combine(root, ".git", "refs", "archrono", "pins", "x")));
        }
        finally
        {
            TestGit.DeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData("crbr", "Create Branch", true)]
    [InlineData("undo", "Undo last operation", true)]
    [InlineData("xyz", "Create Branch", false)]
    public void Fuzzy_matcher_scores_subsequences(string query, string candidate, bool matches) =>
        Assert.Equal(matches, FuzzyMatcher.Score(query, candidate) > 0);

    [Fact]
    public void Settings_persist_and_map_retention_policy()
    {
        var root = TestGit.CreateTempDirectory("settings");
        try
        {
            var storage = ArChronoStorage.OpenAndMigrate(Path.Combine(root, "db.sqlite"));
            var settings = new SettingsService(storage.Settings);
            settings.Update(s => s with
            {
                Mode = UiMode.Pro,
                TimeMachine = s.TimeMachine with { Keep = SnapshotRetention.UntilDiskLimit, MaxStorageGigabytes = 1.5 },
                Ai = s.Ai with { Enabled = true, Provider = new AiProviderSettings { Kind = AiProviderKind.Ollama, Model = "llama3.2" } },
            });

            var reloaded = new SettingsService(storage.Settings).Current;
            Assert.Equal(UiMode.Pro, reloaded.Mode);
            Assert.Equal(AiProviderKind.Ollama, reloaded.Ai.Provider.Kind);
            var policy = reloaded.TimeMachine.ToPolicy();
            Assert.Null(policy.MaxAge);
            Assert.Equal((long)(1.5 * 1024 * 1024 * 1024), policy.MaxStorageBytes);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
        finally
        {
            TestGit.DeleteDirectory(root);
        }
    }

    private sealed class MemoryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _values = [];
        public bool IsAvailable => true;
        public string Description => "memory";
        public string? Read(string key) => _values.GetValueOrDefault(key);
        public void Write(string key, string secret) => _values[key] = secret;
        public bool Delete(string key) => _values.Remove(key);
    }

    private sealed class FakeHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    [Fact]
    public async Task Ai_assistant_records_audit_without_prompt_text_and_keeps_secrets_out_of_db()
    {
        var paths = TempPaths(out var root);
        using var test = TestRepository.Create();
        await using (var app = AppServices.Create(paths))
        {
            app.Settings.Update(s => s with { Git = s.Git with { AutoFetch = false } });
            var session = await app.OpenSessionAsync(test.Root);
            test.WriteFile("src/Login.cs", "class Login { /* password=hunter2hunter2 */ }\n");
            test.WriteFile(".env", "TOKEN=abc\n");
            test.Git("add", "-A");

            var credentials = new MemoryCredentialStore();
            var settings = app.Settings;
            settings.Update(s => s with { Ai = s.Ai with { Enabled = true, Provider = new AiProviderSettings { Kind = AiProviderKind.Anthropic } } });
            var assistant = new AiAssistant(settings, credentials, app.Storage,
                new HttpClient(new FakeHandler("""{"content":[{"type":"text","text":"```\nfeat(auth): add login\n```"}]}""")));
            Assert.False(assistant.IsReady);
            assistant.SaveApiKey(AiProviderKind.Anthropic, "sk-ant-test-key-value");
            Assert.True(assistant.IsReady);

            var payload = await assistant.PrepareCommitMessageAsync(session, ArChrono.AI.Context.CommitMessageStyle.Conventional);
            Assert.Contains(payload.ExcludedPaths, e => e.Path == ".env");
            Assert.DoesNotContain("hunter2", payload.Prompt.User);

            var result = await assistant.SendAsync(session, payload);
            Assert.Equal("feat(auth): add login", result.Text);

            var audit = Assert.Single(app.Storage.AiRequests.List());
            Assert.Equal("succeeded", audit.Status);
            Assert.Equal(["src/Login.cs"], audit.IncludedPaths);
            Assert.Equal(payload.PromptSha256, audit.PromptSha256);

            var dbBytes = File.ReadAllBytes(paths.DatabasePath);
            var dbText = System.Text.Encoding.UTF8.GetString(dbBytes);
            Assert.DoesNotContain("sk-ant-test-key-value", dbText);
        }
        TestGit.DeleteDirectory(root);
    }
}

public sealed class ConsoleParsingTests
{
    [Theory]
    [InlineData("log --oneline -5", new[] { "log", "--oneline", "-5" })]
    [InlineData("commit -m \"fix: a bug\"", new[] { "commit", "-m", "fix: a bug" })]
    [InlineData("tag -a v1 -m 'Release one'", new[] { "tag", "-a", "v1", "-m", "Release one" })]
    [InlineData("commit -m \"\"", new[] { "commit", "-m", "" })]
    public void Console_splits_arguments_like_a_shell(string text, string[] expected) =>
        Assert.Equal(expected, ArChrono.App.ViewModels.ConsoleViewModel.SplitArguments(text));
}
