using ArChrono.Git;
using ArChrono.Git.Errors;
using ArChrono.Git.Process;
using ArChrono.Recovery.Points;
using ArChrono.Storage.Records;

namespace ArChrono.Recovery.Operations;

public sealed record OperationContext(GitRepository Repository, long RepositoryId);

public sealed record ValidationResult(bool CanProceed, IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings)
{
    public static ValidationResult Ok(params string[] warnings) => new(true, [], warnings);

    public static ValidationResult Blocked(string problem) => new(false, [problem], []);
}

public sealed record ExecutionResult(OperationStatus Status, GitError? Error = null, GitCommandResult? Command = null, string? MetadataJson = null, string? Message = null)
{
    public bool Succeeded => Status == OperationStatus.Succeeded;

    public static ExecutionResult Success(GitCommandResult? command = null, string? message = null) => new(OperationStatus.Succeeded, null, command, Message: message);

    public static ExecutionResult Failure(GitError error, GitCommandResult? command = null) => new(OperationStatus.Failed, error, command);

    /// <summary>Git sonucunu duruma çevirir; conflict ile duran işlemler <see cref="OperationStatus.Conflicted"/> olur.</summary>
    public static ExecutionResult FromCommand(GitCommandResult result, bool conflictsPossible = false)
    {
        if (result.Success) return Success(result);
        var error = GitErrorTranslator.Translate(result);
        var status = conflictsPossible && error.Code is GitErrorCode.MergeConflict ? OperationStatus.Conflicted
            : error.Code == GitErrorCode.Cancelled ? OperationStatus.Cancelled
            : OperationStatus.Failed;
        return new ExecutionResult(status, error, result);
    }
}

/// <summary>
/// Durum değiştiren her Git işlemi bu arayüzü uygular ve <see cref="SafeOperationRunner"/> üzerinden çalışır:
/// validate → recovery point → execute → verify → record → undo.
/// </summary>
public interface IGitOperation
{
    /// <summary>Kalıcı tür adı: "reset", "branch-delete", "undo"…</summary>
    string Kind { get; }

    /// <summary>İnsan dilinde başlık: "Reset main to 3e1a0c2 (hard)".</summary>
    string Title { get; }

    /// <summary>Eşdeğer git komutu (gösterim amaçlı).</summary>
    string CommandPreview { get; }

    OperationRisk Risk { get; }

    RefScope RefScope => RefScope.Default;

    bool CapturesWorkingTree => true;

    /// <summary>Undo/redo işlemleri için hedef işlem.</summary>
    long? UndoOfOperationId => null;

    string? MetadataJson => null;

    Task<ValidationResult> ValidateAsync(OperationContext context, CancellationToken cancellationToken) => Task.FromResult(ValidationResult.Ok());

    Task<ExecutionResult> ExecuteAsync(OperationContext context, CancellationToken cancellationToken);

    /// <summary>Başarılı yürütme sonrası beklenen durum kontrolü; sorun varsa açıklama döner.</summary>
    Task<string?> VerifyAsync(OperationContext context, ExecutionResult result, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
