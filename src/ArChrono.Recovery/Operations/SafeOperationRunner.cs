using System.Collections.Concurrent;
using ArChrono.Git.Errors;
using ArChrono.Localization;
using ArChrono.Recovery.Points;
using ArChrono.Storage.Records;
using ArChrono.Storage.Stores;

namespace ArChrono.Recovery.Operations;

public sealed record OperationOutcome(
    GitOperationRecord Operation,
    OperationStatus Status,
    ValidationResult Validation,
    ExecutionResult? Execution,
    RecoveryPointRecord? Before,
    RecoveryPointRecord? After,
    string? VerificationProblem)
{
    public bool Succeeded => Status == OperationStatus.Succeeded && VerificationProblem is null;
    public bool CanUndo => Before is not null && Status is not (OperationStatus.Blocked or OperationStatus.Running);
    public GitError? Error => Execution?.Error;
}

/// <summary>
/// Tüm durum değiştiren Git işlemlerinin geçtiği zorunlu hat (ADR-0004).
/// Recovery point oluşturulamazsa işlem çalıştırılmaz.
/// </summary>
public sealed class SafeOperationRunner(ArChronoStorage storage, RecoveryPointService points)
{
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _gates = new();

    public event EventHandler<OperationOutcome>? OperationCompleted;

    public async Task<OperationOutcome> RunAsync(OperationContext context, IGitOperation operation, CancellationToken cancellationToken = default)
    {
        var gate = _gates.GetOrAdd(context.RepositoryId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var outcome = await RunCoreAsync(context, operation, cancellationToken).ConfigureAwait(false);
            OperationCompleted?.Invoke(this, outcome);
            return outcome;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<OperationOutcome> RunCoreAsync(OperationContext context, IGitOperation operation, CancellationToken cancellationToken)
    {
        // 1. validate
        ValidationResult validation;
        try
        {
            validation = await operation.ValidateAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (GitException ex)
        {
            validation = ValidationResult.Blocked(ex.Error.Title + " " + ex.Error.Explanation);
        }

        var record = storage.Operations.Start(context.RepositoryId, operation.Kind, operation.Title, operation.CommandPreview,
            operation.Risk, operation.UndoOfOperationId, operation.MetadataJson);

        if (!validation.CanProceed)
        {
            storage.Operations.Finish(record.Id, OperationStatus.Blocked, null, "validation", string.Join(" ", validation.Problems));
            return new OperationOutcome(Reload(record), OperationStatus.Blocked, validation, null, null, null, null);
        }

        // 2. recovery point
        RecoveryPointRecord? before = null;
        if (operation.Risk >= OperationRisk.Reversible)
        {
            var kind = operation.Kind is "undo" or "redo" or "restore" ? RecoveryPointKind.BeforeRestore : RecoveryPointKind.BeforeOperation;
            try
            {
                before = await points.CaptureAsync(context.Repository, context.RepositoryId, kind, Loc.T("Before ", "İşlemden önce: ") + operation.Title, record.Id,
                    operation.RefScope, operation.CapturesWorkingTree, operation.MetadataJson, cancellationToken).ConfigureAwait(false);
                storage.Operations.SetBeforePoint(record.Id, before.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var message = Loc.T("The operation was not started because a recovery point could not be created: ", "Kurtarma noktası oluşturulamadığı için işlem başlatılmadı: ") + ex.Message;
                storage.Operations.Finish(record.Id, OperationStatus.Blocked, null, "recovery-point", message, ex.ToString());
                return new OperationOutcome(Reload(record), OperationStatus.Blocked, ValidationResult.Blocked(message), null, null, null, null);
            }
        }

        // 3. execute
        ExecutionResult execution;
        try
        {
            execution = await operation.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (GitException ex)
        {
            execution = ExecutionResult.Failure(ex.Error);
        }
        catch (OperationCanceledException)
        {
            execution = new ExecutionResult(OperationStatus.Cancelled, GitError.Simple(GitErrorCode.Cancelled, Loc.T("The operation was cancelled.", "İşlem iptal edildi."), Loc.T("Git was stopped before it finished.", "Git bitmeden durduruldu.")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            execution = ExecutionResult.Failure(GitError.Simple(GitErrorCode.Unknown, Loc.T("The operation could not be completed.", "İşlem tamamlanamadı."), ex.Message));
        }

        // 4. verify
        string? verification = null;
        if (execution.Succeeded)
        {
            try
            {
                verification = await operation.VerifyAsync(context, execution, CancellationToken.None).ConfigureAwait(false);
            }
            catch (GitException ex)
            {
                verification = ex.Error.Title;
            }
        }

        // 5. after point + kaybolabilecek nesneleri koru
        RecoveryPointRecord? after = null;
        if (before is not null)
        {
            try
            {
                after = await points.CaptureAsync(context.Repository, context.RepositoryId, RecoveryPointKind.AfterOperation, Loc.T("After ", "İşlemden sonra: ") + operation.Title,
                    record.Id, operation.RefScope, operation.CapturesWorkingTree, execution.MetadataJson ?? operation.MetadataJson, CancellationToken.None).ConfigureAwait(false);
                await points.ProtectLostObjectsAsync(context.Repository, before, after, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is GitException or IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                // Undo "before" noktasıyla hâlâ mümkün; after noktası yalnızca planı daraltır.
            }
        }

        // 6. record
        var error = execution.Error;
        storage.Operations.Finish(record.Id, execution.Status, after?.Id, error?.Code.ToString(),
            verification ?? error?.Title, error?.TechnicalDetails, execution.MetadataJson);

        return new OperationOutcome(Reload(record), execution.Status, validation, execution, before, after, verification);
    }

    private GitOperationRecord Reload(GitOperationRecord record) => storage.Operations.Get(record.Id) ?? record;
}
