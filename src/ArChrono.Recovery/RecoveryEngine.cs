using ArChrono.Recovery.Content;
using ArChrono.Recovery.Operations;
using ArChrono.Recovery.Points;
using ArChrono.Recovery.Restore;
using ArChrono.Recovery.Retention;
using ArChrono.Recovery.Snapshots;
using ArChrono.Recovery.Timeline;
using ArChrono.Storage.Records;
using ArChrono.Storage.Stores;

namespace ArChrono.Recovery;

public sealed record RecoveryOptions
{
    public SnapshotOptions Snapshots { get; init; } = new();
    public bool PinInRepository { get; init; } = true;
}

/// <summary>Recovery katmanının composition root'u: tüm servisler tek yerde, açık bağımlılıklarla kurulur.</summary>
public sealed class RecoveryEngine
{
    private RecoveryOptions _options;

    public RecoveryEngine(ArChronoStorage storage, string contentStoreDirectory, RecoveryOptions? options = null)
    {
        _options = options ?? new RecoveryOptions();
        Storage = storage;
        ContentStore = new ContentStore(contentStoreDirectory);
        Pins = new PinManager(storage, () => _options.PinInRepository);
        Snapshots = new SnapshotEngine(storage, ContentStore, Pins, () => _options.Snapshots);
        SnapshotReader = new SnapshotReader(storage, ContentStore, Snapshots);
        Restorer = new WorkingTreeRestorer(storage, ContentStore);
        Points = new RecoveryPointService(storage, Snapshots, Pins);
        Runner = new SafeOperationRunner(storage, Points);
        Planner = new RestorePlanner(storage, Points);
        Executor = new RestoreExecutor(storage, Restorer);
        Retention = new RetentionService(storage, ContentStore, Pins);
        Timeline = new RecoveryTimelineService(storage);
    }

    public ArChronoStorage Storage { get; }
    public ContentStore ContentStore { get; }
    public PinManager Pins { get; }
    public SnapshotEngine Snapshots { get; }
    public SnapshotReader SnapshotReader { get; }
    public WorkingTreeRestorer Restorer { get; }
    public RecoveryPointService Points { get; }
    public SafeOperationRunner Runner { get; }
    public RestorePlanner Planner { get; }
    public RestoreExecutor Executor { get; }
    public RetentionService Retention { get; }
    public RecoveryTimelineService Timeline { get; }

    public RecoveryOptions Options
    {
        get => _options;
        set => _options = value;
    }

    public Task<OperationOutcome> RunAsync(OperationContext context, IGitOperation operation, CancellationToken cancellationToken = default) =>
        Runner.RunAsync(context, operation, cancellationToken);

    public GitOperationRecord? GetUndoable(long repositoryId) => Storage.Operations.GetLastUndoable(repositoryId);

    public GitOperationRecord? GetRedoable(long repositoryId) => Storage.Operations.GetLastRedoable(repositoryId);

    public Task<RestorePlan> PlanUndoAsync(OperationContext context, GitOperationRecord operation, CancellationToken cancellationToken = default) =>
        Planner.PlanUndoAsync(context, operation, cancellationToken);

    /// <summary>Planı Safe Git hattından çalıştırır. Undo'nun kendisi de geri alınabilir (redo).</summary>
    public Task<OperationOutcome> ExecutePlanAsync(OperationContext context, RestorePlan plan, GitOperationRecord? undoOf, CancellationToken cancellationToken = default)
    {
        var kind = undoOf is null ? "restore" : undoOf.Kind == "undo" ? "redo" : "undo";
        return Runner.RunAsync(context, new RestoreOperation(plan, Executor, kind, undoOf?.Id), cancellationToken);
    }

    public async Task<OperationOutcome?> UndoLastAsync(OperationContext context, CancellationToken cancellationToken = default)
    {
        var operation = GetUndoable(context.RepositoryId);
        if (operation is null) return null;
        var plan = await PlanUndoAsync(context, operation, cancellationToken).ConfigureAwait(false);
        return await ExecutePlanAsync(context, plan, operation, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationOutcome?> RedoAsync(OperationContext context, CancellationToken cancellationToken = default)
    {
        var undo = GetRedoable(context.RepositoryId);
        if (undo is null) return null;
        var plan = await PlanUndoAsync(context, undo, cancellationToken).ConfigureAwait(false);
        return await ExecutePlanAsync(context, plan, undo, cancellationToken).ConfigureAwait(false);
    }
}
