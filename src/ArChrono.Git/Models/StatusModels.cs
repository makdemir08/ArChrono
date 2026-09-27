namespace ArChrono.Git.Models;

public enum ChangeKind
{
    None,
    Modified,
    TypeChanged,
    Added,
    Deleted,
    Renamed,
    Copied,
    Unmerged,
    Untracked,
    Ignored,
}

public sealed record StatusEntry(
    string Path,
    string? OriginalPath,
    ChangeKind IndexChange,
    ChangeKind WorktreeChange,
    bool IsSubmodule,
    int HeadMode,
    int IndexMode,
    int WorktreeMode,
    string? HeadBlob,
    string? IndexBlob,
    string? ConflictCode = null)
{
    public bool IsUntracked => IndexChange == ChangeKind.Untracked;
    public bool IsConflicted => IndexChange == ChangeKind.Unmerged;
    public bool HasStagedChanges => IndexChange is not (ChangeKind.None or ChangeKind.Untracked or ChangeKind.Unmerged or ChangeKind.Ignored);
    public bool HasWorktreeChanges => IsUntracked || IsConflicted || WorktreeChange != ChangeKind.None;

    /// <summary>Kullanıcıya gösterilecek tek bir değişiklik türü (çalışma alanı tarafı öncelikli).</summary>
    public ChangeKind DisplayChange => IsConflicted ? ChangeKind.Unmerged
        : IsUntracked ? ChangeKind.Untracked
        : WorktreeChange != ChangeKind.None ? WorktreeChange
        : IndexChange;
}

public sealed record BranchStatus(string? HeadSha, string? BranchName, string? Upstream, int Ahead, int Behind)
{
    public bool IsDetached => BranchName is null && HeadSha is not null;
    public bool IsUnborn => HeadSha is null;
}

public sealed record RepositoryStatus(BranchStatus Branch, IReadOnlyList<StatusEntry> Entries, RepositoryState State)
{
    public IEnumerable<StatusEntry> Staged => Entries.Where(e => e.HasStagedChanges);
    public IEnumerable<StatusEntry> Unstaged => Entries.Where(e => e.HasWorktreeChanges);

    public int StagedCount => Entries.Count(e => e.HasStagedChanges);
    public int ModifiedCount => Entries.Count(e => !e.IsUntracked && !e.IsConflicted && e.WorktreeChange != ChangeKind.None);
    public int UntrackedCount => Entries.Count(e => e.IsUntracked);
    public int ConflictedCount => Entries.Count(e => e.IsConflicted);
    public bool IsClean => Entries.Count == 0;
}
