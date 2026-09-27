using ArChrono.Application.Repositories;
using ArChrono.Git.Models;
using ArChrono.Git.Services;
using ArChrono.Localization;
using ArChrono.Storage.Records;
using ArChrono.Storage.Stores;

namespace ArChrono.Application.Search;

public enum SearchResultKind
{
    Command,
    Branch,
    Tag,
    Commit,
    File,
    RecoveryPoint,
    Snapshot,
    ChangedCode,
}

public sealed record SearchResult(SearchResultKind Kind, string Title, string? Subtitle, object Payload, double Score);

/// <summary>
/// Arama sağlayıcısı. Lexical sağlayıcılar bugün; Phase 3'te lokal embedding tabanlı semantic sağlayıcı aynı arayüzle eklenir.
/// </summary>
public interface ISearchProvider
{
    SearchResultKind Kind { get; }

    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int limit, CancellationToken cancellationToken);
}

public static class FuzzyMatcher
{
    /// <summary>Alt dizi eşleşmesi puanı (0 = eşleşme yok). Kelime başı ve ardışık eşleşmeler ödüllendirilir.</summary>
    public static double Score(string query, string candidate)
    {
        if (string.IsNullOrEmpty(query)) return 1;
        if (string.IsNullOrEmpty(candidate)) return 0;

        var index = candidate.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index >= 0) return 100 - Math.Min(index, 50) + (index == 0 ? 50 : 0) - candidate.Length * 0.1;

        double score = 0;
        var position = 0;
        var consecutive = 0;
        foreach (var c in query)
        {
            if (char.IsWhiteSpace(c)) continue;
            var found = -1;
            for (var i = position; i < candidate.Length; i++)
            {
                if (char.ToLowerInvariant(candidate[i]) == char.ToLowerInvariant(c))
                {
                    found = i;
                    break;
                }
            }
            if (found < 0) return 0;
            consecutive = found == position ? consecutive + 1 : 0;
            var wordStart = found == 0 || candidate[found - 1] is ' ' or '/' or '-' or '_' or '.';
            score += 1 + consecutive * 2 + (wordStart ? 3 : 0);
            position = found + 1;
        }
        return score;
    }
}

public sealed class SearchService
{
    private readonly List<ISearchProvider> _providers;

    public SearchService(RepositorySession session, ArChronoStorage storage)
    {
        _providers =
        [
            new RefSearchProvider(session, SearchResultKind.Branch),
            new RefSearchProvider(session, SearchResultKind.Tag),
            new CommitSearchProvider(session),
            new FileSearchProvider(session),
            new RecoverySearchProvider(session, storage),
        ];
    }

    public void AddProvider(ISearchProvider provider) => _providers.Add(provider);

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, IReadOnlySet<SearchResultKind>? kinds = null, int limitPerKind = 8,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var providers = _providers.Where(p => kinds is null || kinds.Contains(p.Kind)).ToList();
        var tasks = providers.Select(async p =>
        {
            try
            {
                return await p.SearchAsync(query.Trim(), limitPerKind, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is Git.Errors.GitException or InvalidOperationException)
            {
                return (IReadOnlyList<SearchResult>)[];
            }
        });
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.SelectMany(r => r).OrderByDescending(r => r.Score).ToList();
    }

    /// <summary>Kod içeriğinde arama (git log -S). Yavaş olabilir; ayrı tetiklenir.</summary>
    public static async Task<IReadOnlyList<SearchResult>> SearchChangedCodeAsync(RepositorySession session, string text, CancellationToken cancellationToken = default)
    {
        var commits = await session.Git.History.SearchChangedCodeAsync(text, cancellationToken: cancellationToken).ConfigureAwait(false);
        return commits.Select(c => new SearchResult(SearchResultKind.ChangedCode, c.Subject, $"{c.ShortSha} · {c.AuthorName} · added or removed \"{text}\"", c, 50)).ToList();
    }
}

internal sealed class RefSearchProvider(RepositorySession session, SearchResultKind kind) : ISearchProvider
{
    public SearchResultKind Kind => kind;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var refs = await session.Git.Refs.GetRefsAsync(cancellationToken).ConfigureAwait(false);
        return refs
            .Where(r => kind == SearchResultKind.Tag ? r.Kind == RefKind.Tag : r.Kind is RefKind.LocalBranch or RefKind.RemoteBranch)
            .Select(r => new SearchResult(kind, r.Name, r.Subject, r, FuzzyMatcher.Score(query, r.Name) + (r.Kind == RefKind.LocalBranch ? 5 : 0)))
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .Take(limit)
            .ToList();
    }
}

internal sealed class CommitSearchProvider(RepositorySession session) : ISearchProvider
{
    public SearchResultKind Kind => SearchResultKind.Commit;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        if (query.Length < 3) return [];
        var commits = await session.Git.History.SearchAsync(query, limit, cancellationToken).ConfigureAwait(false);
        return commits.Select((c, i) => new SearchResult(Kind, c.Subject, $"{c.ShortSha} · {c.AuthorName} · {c.CommitDate:yyyy-MM-dd}", c,
            (c.Sha.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 150 : 60) - i)).ToList();
    }
}

internal sealed class FileSearchProvider(RepositorySession session) : ISearchProvider
{
    private IReadOnlyList<string>? _files;
    private DateTimeOffset _loadedAt;

    public SearchResultKind Kind => SearchResultKind.File;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        if (_files is null || DateTimeOffset.Now - _loadedAt > TimeSpan.FromSeconds(30))
        {
            var index = await session.Git.Objects.ListIndexAsync(cancellationToken).ConfigureAwait(false);
            _files = index.Select(e => e.Path).Distinct().ToList();
            _loadedAt = DateTimeOffset.Now;
        }
        return _files
            .Select(path => new SearchResult(Kind, Path.GetFileName(path), path, path,
                Math.Max(FuzzyMatcher.Score(query, Path.GetFileName(path)) * 1.5, FuzzyMatcher.Score(query, path))))
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .Take(limit)
            .ToList();
    }
}

internal sealed class RecoverySearchProvider(RepositorySession session, ArChronoStorage storage) : ISearchProvider
{
    public SearchResultKind Kind => SearchResultKind.RecoveryPoint;

    public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var points = storage.RecoveryPoints.List(session.Record.Id, 2000)
            .Where(p => p.Kind != RecoveryPointKind.AfterOperation)
            .Select(p => new SearchResult(Kind, p.Title, p.CreatedAt.ToString("MMM d, HH:mm", Loc.Culture) + " · " + (p.Branch ?? Loc.T("detached", "branch dışında")), p,
                Math.Max(FuzzyMatcher.Score(query, p.Title), FuzzyMatcher.Score(query, p.Branch ?? ""))))
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .Take(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<SearchResult>>(points);
    }
}
