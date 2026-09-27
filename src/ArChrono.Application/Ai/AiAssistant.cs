using System.Security.Cryptography;
using System.Text;
using ArChrono.AI;
using ArChrono.AI.Context;
using ArChrono.AI.Privacy;
using ArChrono.AI.Providers;
using ArChrono.Application.Repositories;
using ArChrono.Application.Settings;
using ArChrono.Git.Services;
using ArChrono.Localization;
using ArChrono.Platform.Credentials;
using ArChrono.Storage.Records;
using ArChrono.Storage.Stores;

namespace ArChrono.Application.Ai;

/// <summary>
/// AI özelliklerinin uygulama tarafı: bağlam hazırlama → (önizleme) → gönderme → denetim kaydı → önbellek.
/// AI çıktısı hiçbir zaman otomatik commit edilmez veya uygulanmaz.
/// </summary>
public sealed class AiAssistant(SettingsService settings, ICredentialStore credentials, ArChronoStorage storage, HttpClient http)
{
    public AiSettings Settings => settings.Current.Ai;

    public bool IsEnabled => Settings.Enabled;

    public bool IsReady => IsEnabled && (!Settings.Provider.RequiresApiKey || HasApiKey(Settings.Provider.Kind));

    public string NotReadyReason => !IsEnabled
        ? Loc.T("AI features are off. Turn them on in Settings → AI & Privacy.", "AI özellikleri kapalı. Ayarlar → AI ve Gizlilik bölümünden açın.")
        : Loc.T($"Add an API key for {Settings.Provider.Kind} in Settings → AI & Privacy.", $"Ayarlar → AI ve Gizlilik bölümünden {Settings.Provider.Kind} için bir API anahtarı ekleyin.");

    private AiContextBuilder Builder => new(new SensitivePathFilter(Settings.ExcludedPatterns));

    public bool HasApiKey(AiProviderKind kind)
    {
        try
        {
            return !string.IsNullOrEmpty(credentials.Read(new AiProviderSettings { Kind = kind }.CredentialKey));
        }
        catch (CredentialStoreException)
        {
            return false;
        }
    }

    public void SaveApiKey(AiProviderKind kind, string apiKey) => credentials.Write(new AiProviderSettings { Kind = kind }.CredentialKey, apiKey.Trim());

    public bool RemoveApiKey(AiProviderKind kind) => credentials.Delete(new AiProviderSettings { Kind = kind }.CredentialKey);

    public async Task<AiPayload> PrepareCommitMessageAsync(RepositorySession session, CommitMessageStyle style, CancellationToken cancellationToken = default)
    {
        var patch = await session.Git.Diff.GetStagedPatchAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var head = await session.Git.Refs.GetHeadAsync(cancellationToken).ConfigureAwait(false);
        var recent = head.IsUnborn
            ? []
            : (await session.Git.History.GetCommitsAsync(new LogQuery { Revisions = ["HEAD"], MaxCount = 8 }, cancellationToken).ConfigureAwait(false))
                .Select(c => c.Subject).ToList();
        return Builder.BuildCommitMessage(patch, style, head.BranchName, recent);
    }

    public async Task<AiPayload> PrepareExplainCommitAsync(RepositorySession session, string sha, CancellationToken cancellationToken = default)
    {
        var details = await session.Git.History.GetCommitDetailsAsync(sha, cancellationToken).ConfigureAwait(false);
        var patch = await session.Git.Diff.GetCommitPatchAsync(details.Commit, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Builder.BuildExplainCommit(details, patch);
    }

    public async Task<AiPayload> PrepareExplainBranchAsync(RepositorySession session, string branch, string baseBranch, CancellationToken cancellationToken = default)
    {
        var mergeBase = await session.Git.History.GetMergeBaseAsync(baseBranch, branch, cancellationToken).ConfigureAwait(false) ?? baseBranch;
        var commits = await session.Git.History.GetRangeAsync(mergeBase, branch, cancellationToken).ConfigureAwait(false);
        var result = await session.Git.Runner.RunAsync(new Git.Process.GitCommand(session.RootPath,
            ["diff", "--no-color", "--no-ext-diff", "--src-prefix=a/", "--dst-prefix=b/", "-M", mergeBase, branch]) { ReadOnly = true }, cancellationToken).ConfigureAwait(false);
        return Builder.BuildExplainBranch(branch, baseBranch, commits, result.EnsureSuccess().StandardOutput);
    }

    public async Task<AiPayload> PrepareExplainConflictAsync(RepositorySession session, string path, CancellationToken cancellationToken = default)
    {
        static string? Text(byte[]? bytes) => bytes is null ? null : Encoding.UTF8.GetString(bytes);
        var baseTask = session.Git.Conflicts.ReadStageAsync(path, 1, cancellationToken);
        var oursTask = session.Git.Conflicts.ReadStageAsync(path, 2, cancellationToken);
        var theirsTask = session.Git.Conflicts.ReadStageAsync(path, 3, cancellationToken);
        await Task.WhenAll(baseTask, oursTask, theirsTask).ConfigureAwait(false);
        return Builder.BuildExplainConflict(path, Text(await baseTask), Text(await oursTask), Text(await theirsTask));
    }

    public AiResult? TryGetCached(RepositorySession? session, AiPayload payload)
    {
        if (payload.Feature == AiFeature.CommitMessage) return null;
        var content = storage.AiRequests.GetCachedResult(CacheKey(payload));
        return content is null ? null
            : new AiResult(payload.Feature, payload.Subject, content, Settings.Provider.Kind, Settings.Provider.Model, TimeSpan.Zero, FromCache: true, SourceCommits(payload));
    }

    public async Task<AiResult> SendAsync(RepositorySession? session, AiPayload payload, CancellationToken cancellationToken = default)
    {
        if (!IsReady) throw new AiProviderException(NotReadyReason);
        if (payload.IncludedPaths.Count == 0 && payload.Feature != AiFeature.CommitMessage && payload.Prompt.User.Length == 0)
            throw new AiProviderException(Loc.T("Nothing can be shared for this request because all files are excluded by your privacy settings.", "Tüm dosyalar gizlilik ayarlarınızca hariç tutulduğu için bu istekte paylaşılabilecek bir şey yok."));

        var providerSettings = Settings.Provider;
        string? apiKey = null;
        if (providerSettings.RequiresApiKey || providerSettings.Kind == AiProviderKind.OpenAICompatible)
        {
            try
            {
                apiKey = credentials.Read(providerSettings.CredentialKey);
            }
            catch (CredentialStoreException ex)
            {
                throw new AiProviderException(ex.Message);
            }
        }

        var provider = AiProviderFactory.Create(http, providerSettings, apiKey);
        var started = DateTimeOffset.Now;
        try
        {
            var completion = await provider.CompleteAsync(payload.Prompt, cancellationToken).ConfigureAwait(false);
            var text = payload.Feature == AiFeature.CommitMessage ? AiContextBuilder.CleanCommitMessage(completion.Text) : completion.Text.Trim();
            Record(session, payload, provider, "succeeded", completion.Duration, completion.InputTokens, completion.OutputTokens, null, started);
            if (payload.Feature != AiFeature.CommitMessage)
                storage.AiRequests.PutCachedResult(CacheKey(payload), session?.Record.Id, payload.FeatureName, payload.Subject, providerSettings.Kind.ToString(), providerSettings.Model, text);
            return new AiResult(payload.Feature, payload.Subject, text, providerSettings.Kind, completion.Model, completion.Duration, FromCache: false, SourceCommits(payload));
        }
        catch (OperationCanceledException)
        {
            Record(session, payload, provider, "cancelled", DateTimeOffset.Now - started, null, null, null, started);
            throw;
        }
        catch (AiProviderException ex)
        {
            Record(session, payload, provider, "failed", DateTimeOffset.Now - started, null, null, ex.Message, started);
            throw;
        }
    }

    public async Task<string> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var payload = new AiPayload(AiFeature.ExplainCommit, "connection-test", new AiPrompt("Reply with the single word: ready", "ping", 16), [], [], [], 0);
        var providerSettings = Settings.Provider;
        var apiKey = providerSettings.RequiresApiKey || providerSettings.Kind == AiProviderKind.OpenAICompatible ? credentials.Read(providerSettings.CredentialKey) : null;
        var provider = AiProviderFactory.Create(http, providerSettings, apiKey);
        var completion = await provider.CompleteAsync(payload.Prompt, cancellationToken).ConfigureAwait(false);
        return Loc.T($"Connected to {provider.Endpoint.Host} · {completion.Model} · {completion.Duration.TotalMilliseconds:0} ms", $"{provider.Endpoint.Host} adresine bağlanıldı · {completion.Model} · {completion.Duration.TotalMilliseconds:0} ms");
    }

    private void Record(RepositorySession? session, AiPayload payload, IAiProvider provider, string status, TimeSpan duration, int? input, int? output, string? error, DateTimeOffset started)
    {
        storage.AiRequests.Insert(new AiRequestRecord(0, session?.Record.Id, started, payload.FeatureName, provider.Kind.ToString(), provider.Model,
            provider.Endpoint.Host, payload.ByteCount, payload.IncludedPaths, payload.ExcludedPaths.Select(e => e.Path).ToList(), payload.RedactionCount,
            payload.PromptSha256, status, (long)duration.TotalMilliseconds, input, output, error));
    }

    private string CacheKey(AiPayload payload) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{payload.FeatureName}|{payload.Subject}|{Settings.Provider.Kind}|{Settings.Provider.Model}|{payload.PromptSha256}")));

    private static IReadOnlyList<string> SourceCommits(AiPayload payload) =>
        payload.Feature == AiFeature.ExplainCommit ? [payload.Subject] : [];
}
