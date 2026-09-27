using ArChrono.Git;
using ArChrono.Git.Models;
using ArChrono.Localization;
using ArChrono.Storage.Records;
using ArChrono.Storage.Stores;

namespace ArChrono.Recovery.Timeline;

public enum TimelineEntryKind
{
    RecoveryPoint,
    Snapshot,
    Reflog,
    LostCommit,
    LostStash,
}

public sealed record TimelineEntry(
    TimelineEntryKind Kind,
    DateTimeOffset Time,
    string Title,
    string? Detail,
    string? Branch,
    string? Sha,
    int ChangedFiles,
    RecoveryPointRecord? Point = null,
    SnapshotRecord? Snapshot = null,
    GitOperationRecord? Operation = null,
    ReflogEntry? Reflog = null,
    CommitInfo? Commit = null)
{
    public string Id => Kind switch
    {
        TimelineEntryKind.RecoveryPoint => "point:" + Point!.Id,
        TimelineEntryKind.Snapshot => "snapshot:" + Snapshot!.Id,
        TimelineEntryKind.Reflog => "reflog:" + Reflog!.Selector + ":" + Sha,
        _ => "commit:" + Sha,
    };
}

/// <summary>Recovery Center: uygulama noktaları + Time Machine snapshot'ları + reflog + kayıp commit'ler (recovery-model.md §6).</summary>
public sealed class RecoveryTimelineService(ArChronoStorage storage)
{
    public async Task<IReadOnlyList<TimelineEntry>> GetTimelineAsync(GitRepository repository, long repositoryId, bool includeSnapshots = true,
        bool includeReflog = true, int limit = 500, CancellationToken cancellationToken = default)
    {
        var entries = new List<TimelineEntry>();
        var operations = storage.Operations.List(repositoryId, limit * 2).ToDictionary(o => o.Id);

        foreach (var point in storage.RecoveryPoints.List(repositoryId, limit))
        {
            if (point.Kind == RecoveryPointKind.AfterOperation) continue;
            var operation = point.OperationId is { } opId && operations.TryGetValue(opId, out var op) ? op : null;
            var title = point.Kind switch
            {
                RecoveryPointKind.Manual => point.Title,
                _ => point.Title,
            };
            var detail = operation is null ? null : DescribeOutcome(operation);
            entries.Add(new TimelineEntry(TimelineEntryKind.RecoveryPoint, point.CreatedAt, title, detail, point.Branch, point.HeadSha,
                point.ChangedFileCount, Point: point, Operation: operation));
        }

        if (includeSnapshots)
        {
            foreach (var snapshot in storage.Snapshots.List(repositoryId, limit: limit).Where(s => s.Trigger != SnapshotTrigger.Operation))
            {
                var title = snapshot.Trigger switch
                {
                    SnapshotTrigger.Manual => snapshot.Label ?? Loc.T("Saved snapshot", "Kaydedilmiş anlık görüntü"),
                    SnapshotTrigger.Shutdown => Loc.T("Working tree snapshot (app closed)", "Çalışma alanı anlık görüntüsü (uygulama kapandı)"),
                    SnapshotTrigger.Restore => snapshot.Label ?? Loc.T("Snapshot", "Anlık görüntü"),
                    _ => Loc.T("Working tree snapshot", "Çalışma alanı anlık görüntüsü"),
                };
                entries.Add(new TimelineEntry(TimelineEntryKind.Snapshot, snapshot.CreatedAt, title,
                    snapshot.EntryCount == 0 ? Loc.T("No uncommitted changes", "Commit edilmemiş değişiklik yok") : Loc.T($"{snapshot.EntryCount} uncommitted file(s)", $"{snapshot.EntryCount} commit edilmemiş dosya"),
                    snapshot.HeadRef is null ? null : RefNames.Shorten(snapshot.HeadRef), snapshot.HeadSha, snapshot.EntryCount, Snapshot: snapshot));
            }
        }

        if (includeReflog)
        {
            var reflog = await repository.Reflog.GetAsync("HEAD", limit, cancellationToken).ConfigureAwait(false);
            var appWindows = operations.Values
                .Where(o => o.FinishedAt is not null)
                .Select(o => (Start: o.StartedAt.AddSeconds(-1), End: o.FinishedAt!.Value.AddSeconds(2)))
                .ToList();
            foreach (var entry in reflog)
            {
                // Uygulamanın kendi işlemleri recovery point olarak zaten listede.
                if (appWindows.Any(w => entry.Timestamp >= w.Start && entry.Timestamp <= w.End)) continue;
                entries.Add(new TimelineEntry(TimelineEntryKind.Reflog, entry.Timestamp, DescribeReflog(entry), entry.Message, null, entry.Sha, 0, Reflog: entry));
            }
        }

        return entries.OrderByDescending(e => e.Time).Take(limit).ToList();
    }

    /// <summary>Hiçbir ref veya reflog'un göstermediği commit'ler ve stash'ler (derin tarama, yavaş olabilir).</summary>
    public async Task<IReadOnlyList<TimelineEntry>> FindLostWorkAsync(GitRepository repository, CancellationToken cancellationToken = default)
    {
        var dangling = await repository.Objects.FindDanglingCommitsAsync(cancellationToken).ConfigureAwait(false);
        var commits = await repository.History.GetCommitsByShaAsync(dangling, cancellationToken).ConfigureAwait(false);
        return commits
            .Where(c => !c.Subject.StartsWith("ArChrono snapshot pin", StringComparison.Ordinal) && !c.Subject.StartsWith("index on ", StringComparison.Ordinal)
                        && !c.Subject.StartsWith("untracked files on ", StringComparison.Ordinal))
            .Select(c =>
            {
                var isStash = c.Parents.Count >= 2 && (c.Subject.StartsWith("WIP on ", StringComparison.Ordinal) || c.Subject.StartsWith("On ", StringComparison.Ordinal));
                return new TimelineEntry(isStash ? TimelineEntryKind.LostStash : TimelineEntryKind.LostCommit, c.CommitDate,
                    isStash ? Loc.T("Lost stash: ", "Kayıp stash: ") + c.Subject : Loc.T("Lost commit: ", "Kayıp commit: ") + c.Subject, $"{c.AuthorName} · {c.ShortSha}", null, c.Sha, 0, Commit: c);
            })
            .OrderByDescending(e => e.Time)
            .ToList();
    }

    public static string DescribeOutcome(GitOperationRecord operation) => operation.Status switch
    {
        OperationStatus.Succeeded when operation.UndoneByOperationId is not null => Loc.T("Completed, then undone", "Tamamlandı, sonra geri alındı"),
        OperationStatus.Succeeded => Loc.T("Completed", "Tamamlandı"),
        OperationStatus.Conflicted => Loc.T("Stopped with conflicts", "Çakışmalarla durdu"),
        OperationStatus.Failed => Loc.T("Failed: ", "Başarısız: ") + (operation.ErrorMessage ?? Loc.T("unknown error", "bilinmeyen hata")),
        OperationStatus.Blocked => Loc.T("Not started: ", "Başlatılmadı: ") + (operation.ErrorMessage ?? ""),
        OperationStatus.Cancelled => Loc.T("Cancelled", "İptal edildi"),
        _ => Loc.T("In progress", "Devam ediyor"),
    };

    internal static string DescribeReflog(ReflogEntry entry)
    {
        var message = entry.Message;
        return entry.Action switch
        {
            "commit" or "commit (initial)" => "Commit" + After(message),
            "commit (amend)" => "Amend" + After(message),
            "commit (merge)" => "Merge commit" + After(message),
            "checkout" => Loc.T("Switched" + After(message).Replace(" moving from ", " from "),
                "Geçiş" + After(message).Replace(" moving from ", " ").Replace(" to ", " → ")),
            "reset" => "Reset" + After(message),
            "merge" or "pull" => char.ToUpperInvariant(entry.Action[0]) + entry.Action[1..] + After(message),
            _ when entry.Action.StartsWith("rebase", StringComparison.Ordinal) => "Rebase" + After(message),
            "cherry-pick" => "Cherry-pick" + After(message),
            "revert" => "Revert" + After(message),
            _ => message,
        } + Loc.T(" (outside ArChrono)", " (ArChrono dışında)");

        static string After(string text)
        {
            var colon = text.IndexOf(':');
            return colon >= 0 && colon + 1 < text.Length ? ":" + text[(colon + 1)..] : string.Empty;
        }
    }
}
