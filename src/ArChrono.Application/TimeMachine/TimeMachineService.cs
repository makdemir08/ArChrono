using ArChrono.Application.Repositories;
using ArChrono.Git.Models;
using ArChrono.Localization;
using ArChrono.Recovery;
using ArChrono.Recovery.Operations;
using ArChrono.Recovery.Snapshots;
using ArChrono.Storage.Records;

namespace ArChrono.Application.TimeMachine;

/// <summary>Code Time Machine: çalışma alanı geçmişi, dosyanın geçmiş hâli, snapshot karşılaştırma ve geri yükleme.</summary>
public sealed class TimeMachineService(RepositorySession session, RecoveryEngine recovery)
{
    public async Task<SnapshotResult> CaptureAsync(SnapshotTrigger trigger, string? label, CancellationToken cancellationToken = default)
    {
        var result = await recovery.Snapshots.CaptureAsync(session.Git, session.Record.Id, trigger, label, force: trigger == SnapshotTrigger.Manual,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Unchanged) session.NotifySnapshot(result.Snapshot);
        return result;
    }

    public Task<SnapshotResult> SaveSnapshotNowAsync(string? label = null, CancellationToken cancellationToken = default) =>
        CaptureAsync(SnapshotTrigger.Manual, label ?? Loc.T("Saved snapshot", "Kaydedilmiş anlık görüntü"), cancellationToken);

    public IReadOnlyList<SnapshotRecord> GetSnapshots(DateTimeOffset? from = null, DateTimeOffset? to = null, int limit = 1000) =>
        recovery.Storage.Snapshots.List(session.Record.Id, from, to, limit);

    public SnapshotRecord? GetSnapshot(long id) => recovery.Storage.Snapshots.Get(id);

    public SnapshotRecord? FindSnapshotAt(DateTimeOffset time) => recovery.Storage.Snapshots.FindAtOrBefore(session.Record.Id, time);

    public IReadOnlyList<SnapshotEntryRecord> GetOverlay(SnapshotRecord snapshot) => recovery.Storage.Snapshots.GetEntries(snapshot.Id);

    public Task<IReadOnlyList<SnapshotFileChange>> CompareAsync(SnapshotRecord older, SnapshotRecord? newer, CancellationToken cancellationToken = default) =>
        recovery.SnapshotReader.CompareAsync(session.Git, older, newer, cancellationToken);

    public Task<FileDiff> DiffAsync(SnapshotFileChange change, CancellationToken cancellationToken = default) =>
        recovery.SnapshotReader.DiffAsync(session.Git, change, cancellationToken);

    public Task<byte[]?> ReadFileAsync(SnapshotRecord snapshot, string path, CancellationToken cancellationToken = default) =>
        recovery.SnapshotReader.ReadFileAsync(session.Git, snapshot, path, cancellationToken);

    /// <summary>"Bugün 10:15'te bu dosya nasıldı?"</summary>
    public async Task<(SnapshotRecord? Snapshot, byte[]? Content)> ReadFileAtAsync(string path, DateTimeOffset time, CancellationToken cancellationToken = default)
    {
        var snapshot = FindSnapshotAt(time);
        if (snapshot is null) return (null, null);
        return (snapshot, await ReadFileAsync(snapshot, path, cancellationToken).ConfigureAwait(false));
    }

    public Task<IReadOnlyList<(SnapshotRecord Snapshot, string? BlobId)>> GetFileTimelineAsync(string path, CancellationToken cancellationToken = default) =>
        recovery.SnapshotReader.GetFileTimelineAsync(session.Git, session.Record.Id, path, cancellationToken: cancellationToken);

    public Task<OperationOutcome> RestoreFilesAsync(SnapshotRecord snapshot, IReadOnlyCollection<string> paths) =>
        session.Actions.RestoreSnapshotAsync(snapshot, paths);

    public Task<OperationOutcome> RestoreWorkingTreeAsync(SnapshotRecord snapshot) =>
        session.Actions.RestoreSnapshotAsync(snapshot, null);

    public (long UsedBytes, long LimitBytes) GetStorageUsage(long limitBytes) => (recovery.Storage.Blobs.GetTotalStoredBytes(), limitBytes);
}
