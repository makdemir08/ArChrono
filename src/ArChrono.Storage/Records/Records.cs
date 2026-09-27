namespace ArChrono.Storage.Records;

public sealed record RepositoryRecord(
    long Id,
    string RootPath,
    string GitDir,
    string CommonDir,
    string Name,
    string ObjectFormat,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastOpenedAt,
    bool IsFavorite,
    bool TimeMachineEnabled);

public sealed record ContentBlobRecord(string BlobId, long Size, long StoredSize, string Compression, DateTimeOffset CreatedAt);

public enum SnapshotTrigger
{
    Operation,
    Timer,
    Manual,
    Restore,
    Shutdown,
}

public sealed record SnapshotRecord(
    long Id,
    long RepositoryId,
    DateTimeOffset CreatedAt,
    SnapshotTrigger Trigger,
    string? Label,
    string? HeadRef,
    string? HeadSha,
    string IndexTree,
    string? PinCommit,
    string Fingerprint,
    int EntryCount,
    long TotalBytes,
    long NewBytes,
    string? SkippedJson);

public enum SnapshotEntryKind
{
    Modified,
    Untracked,
    Deleted,
    Conflicted,
}

public sealed record SnapshotEntryRecord(string Path, SnapshotEntryKind Kind, string? BlobId, int? Mode, long? Size);

public enum OperationRisk
{
    Safe,
    Reversible,
    Destructive,
    Remote,
}

public enum OperationStatus
{
    Running,
    Succeeded,
    Failed,
    Conflicted,
    Blocked,
    Cancelled,
}

public sealed record GitOperationRecord(
    long Id,
    long RepositoryId,
    string Kind,
    string Title,
    string CommandText,
    OperationRisk Risk,
    OperationStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? ErrorCode,
    string? ErrorMessage,
    string? ErrorDetails,
    long? BeforePointId,
    long? AfterPointId,
    long? UndoOfOperationId,
    long? UndoneByOperationId,
    string? MetadataJson);

public enum RecoveryPointKind
{
    BeforeOperation,
    AfterOperation,
    BeforeRestore,
    Manual,
}

public sealed record RecoveryPointRecord(
    long Id,
    long RepositoryId,
    DateTimeOffset CreatedAt,
    RecoveryPointKind Kind,
    string Title,
    long? OperationId,
    string? HeadRef,
    string? HeadSha,
    string? Branch,
    string RepoState,
    long? SnapshotId,
    int ChangedFileCount,
    bool IsPinned,
    string? MetadataJson);

public sealed record RecoveryRefRecord(string RefName, string TargetSha);

public sealed record RecoveryStashRecord(int Position, string CommitSha, string Message);

public sealed record AiRequestRecord(
    long Id,
    long? RepositoryId,
    DateTimeOffset CreatedAt,
    string Feature,
    string Provider,
    string Model,
    string EndpointHost,
    long RequestBytes,
    IReadOnlyList<string> IncludedPaths,
    IReadOnlyList<string> ExcludedPaths,
    int RedactionCount,
    string PromptSha256,
    string Status,
    long? DurationMs,
    int? InputTokens,
    int? OutputTokens,
    string? ErrorMessage);

public sealed record WorkspaceRecord(
    long Id,
    long RepositoryId,
    string WorktreePath,
    string? Branch,
    string? Label,
    string? AgentKind,
    string? AgentCommand,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastActiveAt);

internal static class EnumText
{
    public static string ToDb(this SnapshotTrigger value) => value.ToString().ToLowerInvariant();
    public static string ToDb(this SnapshotEntryKind value) => value.ToString().ToLowerInvariant();
    public static string ToDb(this OperationRisk value) => value.ToString().ToLowerInvariant();
    public static string ToDb(this OperationStatus value) => value.ToString().ToLowerInvariant();

    public static string ToDb(this RecoveryPointKind value) => value switch
    {
        RecoveryPointKind.BeforeOperation => "before-operation",
        RecoveryPointKind.AfterOperation => "after-operation",
        RecoveryPointKind.BeforeRestore => "before-restore",
        _ => "manual",
    };

    public static T Parse<T>(string value) where T : struct, Enum =>
        Enum.Parse<T>(value.Replace("-", string.Empty), ignoreCase: true);
}
