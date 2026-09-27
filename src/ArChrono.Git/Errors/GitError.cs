namespace ArChrono.Git.Errors;

public enum GitErrorCode
{
    Unknown,
    GitNotFound,
    NotARepository,
    BareRepositoryNotSupported,
    LocalChangesWouldBeOverwritten,
    UntrackedFilesWouldBeOverwritten,
    MergeConflict,
    UnmergedFiles,
    PushRejectedNonFastForward,
    PushRejectedStaleLease,
    AuthenticationFailed,
    NetworkUnavailable,
    RemoteNotFound,
    RepositoryLocked,
    BranchNotFullyMerged,
    BranchCheckedOutElsewhere,
    NothingToCommit,
    IdentityNotConfigured,
    UnrelatedHistories,
    DirtyWorkingTree,
    AlreadyExists,
    InvalidName,
    ReferenceNotFound,
    OperationInProgress,
    NoOperationInProgress,
    StashNotFound,
    TimedOut,
    Cancelled,
}

/// <summary>Ham Git hatasının kullanıcıya gösterilecek, anlaşılır karşılığı.</summary>
public sealed record GitError(
    GitErrorCode Code,
    string Title,
    string Explanation,
    string? Suggestion,
    string CommandLine,
    int ExitCode,
    string RawOutput)
{
    public string TechnicalDetails =>
        $"$ {CommandLine}\nexit code: {ExitCode}\n\n{RawOutput}".TrimEnd();

    public static GitError Simple(GitErrorCode code, string title, string explanation, string? suggestion = null) =>
        new(code, title, explanation, suggestion, string.Empty, 0, string.Empty);
}

public sealed class GitException(GitError error) : Exception(error.Title + " " + error.Explanation)
{
    public GitError Error { get; } = error;
}
