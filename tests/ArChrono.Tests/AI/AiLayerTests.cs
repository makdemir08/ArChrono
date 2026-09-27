using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ArChrono.AI.Context;
using ArChrono.AI.Privacy;
using ArChrono.AI.Providers;
using ArChrono.Git.Models;

namespace ArChrono.Tests.AI;

public sealed class AiLayerTests
{
    [Theory]
    [InlineData(".env", true)]
    [InlineData("config/.env.production", true)]
    [InlineData("certs/server.pem", true)]
    [InlineData("deploy/secrets/db.yaml", true)]
    [InlineData("credentials/aws.json", true)]
    [InlineData("home/.ssh/id_ed25519", true)]
    [InlineData("src/environment.ts", false)]
    [InlineData("src/secretsManager.cs", false)]
    [InlineData("docs/keys.md", false)]
    public void Sensitive_path_filter_uses_default_patterns(string path, bool sensitive) =>
        Assert.Equal(sensitive, new SensitivePathFilter().IsSensitive(path));

    [Fact]
    public void Sensitive_path_filter_supports_anchored_custom_patterns()
    {
        var filter = new SensitivePathFilter(["/config/prod.json", "*.sqlite", "private/**/notes.md"]);
        Assert.True(filter.IsSensitive("config/prod.json"));
        Assert.False(filter.IsSensitive("other/config/prod.json"));
        Assert.True(filter.IsSensitive("data/app.sqlite"));
        Assert.True(filter.IsSensitive("private/a/b/notes.md"));
    }

    [Fact]
    public void Secret_redactor_masks_keys_tokens_and_assignments()
    {
        const string text = """
            const apiKey = "sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123456789";
            password=SuperSecret123
            aws AKIAABCDEFGHIJKLMNOP
            -----BEGIN RSA PRIVATE KEY-----
            MIIEowIBAAKCAQEA
            -----END RSA PRIVATE KEY-----
            var count = 42;
            """;
        var (redacted, count) = SecretRedactor.Redact(text);
        Assert.DoesNotContain("sk-ant-api03", redacted);
        Assert.DoesNotContain("SuperSecret123", redacted);
        Assert.DoesNotContain("AKIAABCDEFGHIJKLMNOP", redacted);
        Assert.DoesNotContain("MIIEowIBAAKCAQEA", redacted);
        Assert.Contains("var count = 42;", redacted);
        Assert.True(count >= 4);
    }

    [Fact]
    public void Context_builder_never_includes_sensitive_files_and_reports_exclusions()
    {
        const string patch = """
            diff --git a/src/Auth.cs b/src/Auth.cs
            index 1111111..2222222 100644
            --- a/src/Auth.cs
            +++ b/src/Auth.cs
            @@ -1 +1,2 @@
             class Auth {}
            +// refresh token support
            diff --git a/.env b/.env
            new file mode 100644
            index 0000000..3333333
            --- /dev/null
            +++ b/.env
            @@ -0,0 +1 @@
            +DATABASE_PASSWORD=hunter2hunter2
            """;
        var builder = new AiContextBuilder(new SensitivePathFilter());
        var payload = builder.BuildCommitMessage(patch, CommitMessageStyle.Conventional, "feature/auth", ["feat: add login"]);

        Assert.Equal(["src/Auth.cs"], payload.IncludedPaths);
        Assert.Contains(payload.ExcludedPaths, e => e.Path == ".env");
        Assert.DoesNotContain("hunter2", payload.Prompt.User);
        Assert.DoesNotContain(".env b/.env", payload.Prompt.User);
        Assert.Contains("refresh token support", payload.Prompt.User);
        Assert.Contains("Conventional Commits", payload.Prompt.System);
        Assert.Equal(64, payload.PromptSha256.Length);
    }

    [Fact]
    public void Context_builder_truncates_large_patches()
    {
        var bigFile = "diff --git a/big.txt b/big.txt\n--- a/big.txt\n+++ b/big.txt\n@@ -0,0 +1,1 @@\n" + string.Concat(Enumerable.Repeat("+line\n", 20_000));
        var payload = new AiContextBuilder(new SensitivePathFilter(), maxPatchCharacters: 5_000)
            .BuildCommitMessage(bigFile + bigFile.Replace("big.txt", "other.txt"), CommitMessageStyle.Short, null, []);
        Assert.Contains("big.txt", payload.TruncatedPaths);
        Assert.True(payload.Prompt.User.Length < 7_000);
    }

    [Fact]
    public void Commit_message_cleanup_removes_fences_and_quotes()
    {
        Assert.Equal("feat: add login", AiContextBuilder.CleanCommitMessage("```\nfeat: add login\n```"));
        Assert.Equal("fix: typo", AiContextBuilder.CleanCommitMessage("\"fix: typo\""));
    }

    [Fact]
    public void Explain_conflict_refuses_sensitive_files()
    {
        var payload = new AiContextBuilder(new SensitivePathFilter()).BuildExplainConflict("secrets/prod.yaml", "a", "b", "c");
        Assert.Empty(payload.IncludedPaths);
        Assert.Single(payload.ExcludedPaths);
    }

    private sealed class CapturingHandler(string responseJson, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(responseJson, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly AiPrompt Prompt = new("system text", "user text", 300);

    [Fact]
    public async Task Anthropic_provider_uses_messages_api()
    {
        var handler = new CapturingHandler("""{"content":[{"type":"text","text":"feat: login"}],"usage":{"input_tokens":12,"output_tokens":3}}""");
        var provider = AiProviderFactory.Create(new HttpClient(handler), new AiProviderSettings { Kind = AiProviderKind.Anthropic, Model = "claude-sonnet-5" }, "key-123");
        var result = await provider.CompleteAsync(Prompt);

        Assert.Equal("feat: login", result.Text);
        Assert.Equal(12, result.InputTokens);
        Assert.Equal("https://api.anthropic.com/v1/messages", handler.Request!.RequestUri!.ToString());
        Assert.Equal("key-123", handler.Request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", handler.Request.Headers.GetValues("anthropic-version").Single());
        var body = JsonNode.Parse(handler.Body!)!;
        Assert.Equal("system text", body["system"]!.GetValue<string>());
        Assert.Equal(300, body["max_tokens"]!.GetValue<int>());
        Assert.Equal("user", body["messages"]![0]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task OpenAi_compatible_provider_uses_chat_completions_with_bearer()
    {
        var handler = new CapturingHandler("""{"choices":[{"message":{"content":"fix: bug"}}],"usage":{"prompt_tokens":5,"completion_tokens":2}}""");
        var settings = new AiProviderSettings { Kind = AiProviderKind.OpenAICompatible, Endpoint = "http://localhost:1234/v1/", Model = "local-model" };
        var provider = AiProviderFactory.Create(new HttpClient(handler), settings, "token");
        var result = await provider.CompleteAsync(Prompt);

        Assert.Equal("fix: bug", result.Text);
        Assert.Equal("http://localhost:1234/v1/chat/completions", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.True(settings.IsLocal);
        Assert.Equal("system", JsonNode.Parse(handler.Body!)!["messages"]![0]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task Ollama_and_azure_providers_build_expected_requests()
    {
        var ollamaHandler = new CapturingHandler("""{"message":{"content":"ok"},"prompt_eval_count":3,"eval_count":1}""");
        var ollama = AiProviderFactory.Create(new HttpClient(ollamaHandler), new AiProviderSettings { Kind = AiProviderKind.Ollama, Model = "qwen2.5-coder" }, null);
        Assert.Equal("ok", (await ollama.CompleteAsync(Prompt)).Text);
        Assert.Equal("http://localhost:11434/api/chat", ollamaHandler.Request!.RequestUri!.ToString());
        Assert.False(JsonNode.Parse(ollamaHandler.Body!)!["stream"]!.GetValue<bool>());

        var azureHandler = new CapturingHandler("""{"choices":[{"message":{"content":"ok"}}]}""");
        var azure = AiProviderFactory.Create(new HttpClient(azureHandler),
            new AiProviderSettings { Kind = AiProviderKind.AzureOpenAI, Endpoint = "https://contoso.openai.azure.com", Deployment = "gpt-5-mini-prod" }, "azure-key");
        await azure.CompleteAsync(Prompt);
        Assert.Equal("https://contoso.openai.azure.com/openai/deployments/gpt-5-mini-prod/chat/completions?api-version=2024-10-21", azureHandler.Request!.RequestUri!.ToString());
        Assert.Equal("azure-key", azureHandler.Request.Headers.GetValues("api-key").Single());
    }

    [Fact]
    public async Task Provider_errors_are_human_readable_and_missing_key_is_detected()
    {
        var handler = new CapturingHandler("""{"error":{"message":"invalid x-api-key"}}""", HttpStatusCode.Unauthorized);
        var provider = AiProviderFactory.Create(new HttpClient(handler), new AiProviderSettings { Kind = AiProviderKind.Anthropic }, "bad");
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => provider.CompleteAsync(Prompt));
        Assert.Contains("rejected the API key", ex.Message);
        Assert.Equal(401, ex.StatusCode);

        var noKey = AiProviderFactory.Create(new HttpClient(handler), new AiProviderSettings { Kind = AiProviderKind.OpenAI }, null);
        await Assert.ThrowsAsync<AiProviderException>(() => noKey.CompleteAsync(Prompt));
    }

    [Fact]
    public void Explain_commit_prompt_contains_grounding_rules()
    {
        var commit = new CommitInfo(new string('a', 40), [], "Ada", "ada@x", DateTimeOffset.Now, "Ada", "ada@x", DateTimeOffset.Now, "Add refresh token");
        var details = new CommitDetails(commit, "Body", [new CommitFileChange("src/TokenService.cs", null, ChangeKind.Modified, 10, 2, false)]);
        var payload = new AiContextBuilder(new SensitivePathFilter()).BuildExplainCommit(details, "diff --git a/src/TokenService.cs b/src/TokenService.cs\n+x\n");
        Assert.Contains("WHAT CHANGED?", payload.Prompt.System);
        Assert.Contains("Never invent", payload.Prompt.System);
        Assert.Equal(commit.Sha, payload.Subject);
    }
}
