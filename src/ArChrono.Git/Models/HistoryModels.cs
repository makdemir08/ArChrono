namespace ArChrono.Git.Models;

public sealed record CommitInfo(
    string Sha,
    IReadOnlyList<string> Parents,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthorDate,
    string CommitterName,
    string CommitterEmail,
    DateTimeOffset CommitDate,
    string Subject)
{
    public string ShortSha => Sha.Length > 7 ? Sha[..7] : Sha;
    public bool IsMerge => Parents.Count > 1;
    public bool IsRoot => Parents.Count == 0;
}

public sealed record CommitFileChange(string Path, string? OldPath, ChangeKind Change, int Additions, int Deletions, bool IsBinary);

public sealed record CommitDetails(CommitInfo Commit, string Body, IReadOnlyList<CommitFileChange> Files)
{
    public string Message => string.IsNullOrWhiteSpace(Body) ? Commit.Subject : Commit.Subject + "\n\n" + Body;
    public int TotalAdditions => Files.Sum(f => f.Additions);
    public int TotalDeletions => Files.Sum(f => f.Deletions);
}

public sealed record FileHistoryEntry(CommitInfo Commit, string Path, string? OldPath, ChangeKind Change);

public enum RefKind
{
    LocalBranch,
    RemoteBranch,
    Tag,
    Stash,
    Other,
}

public sealed record RefInfo(
    string FullName,
    RefKind Kind,
    string TargetSha,
    string? Upstream,
    int Ahead,
    int Behind,
    bool UpstreamGone,
    bool IsHead,
    DateTimeOffset? LastCommitDate,
    string Subject,
    bool IsAnnotatedTag = false)
{
    public string Name => RefNames.Shorten(FullName);

    /// <summary>Uzak branch'ler için "origin".</summary>
    public string? RemoteName => Kind == RefKind.RemoteBranch && Name.IndexOf('/') is var i and > 0 ? Name[..i] : null;

    public string DisplayName => Kind == RefKind.RemoteBranch && RemoteName is { } remote ? Name[(remote.Length + 1)..] : Name;
}

public sealed record StashEntry(int Index, string Sha, string Message, DateTimeOffset Date)
{
    public string Selector => $"stash@{{{Index}}}";
}

public sealed record ReflogEntry(string RefName, int Index, string Sha, string? PreviousSha, string Action, string Message, DateTimeOffset Timestamp)
{
    public string Selector => $"{RefName}@{{{Index}}}";
}

public sealed record BlameLine(int LineNumber, string CommitSha, string Author, DateTimeOffset AuthorTime, string Summary, int OriginalLineNumber, string OriginalPath, string Content, bool IsBoundary);

public sealed record TreeEntry(int Mode, string Type, string ObjectId, string Path);

public sealed record IndexEntry(int Mode, string ObjectId, int Stage, string Path);

public enum ConflictSeverity
{
    /// <summary>Her iki tarafta değişmiş ama Git otomatik birleştirebildi — gözden geçirme önerilir.</summary>
    AutoMerged,
    /// <summary>Metin çakışması: aynı satırlar iki tarafta farklı değişmiş.</summary>
    Content,
    /// <summary>Yapısal çakışma: modify/delete, rename, add/add, file/directory…</summary>
    Structural,
}

public sealed record PredictedConflict(string Path, ConflictSeverity Severity, string Description);

public sealed record ConflictPrediction(bool HasConflicts, string? ResultTree, IReadOnlyList<PredictedConflict> Files, IReadOnlyList<string> Messages)
{
    public int ContentConflicts => Files.Count(f => f.Severity == ConflictSeverity.Content);
    public int StructuralConflicts => Files.Count(f => f.Severity == ConflictSeverity.Structural);
    public int AutoMergedFiles => Files.Count(f => f.Severity == ConflictSeverity.AutoMerged);
}

public sealed record ConflictFile(string Path, IndexEntry? Base, IndexEntry? Ours, IndexEntry? Theirs)
{
    public string Kind => (Base, Ours, Theirs) switch
    {
        (_, not null, null) => "deleted by them",
        (_, null, not null) => "deleted by us",
        (null, not null, not null) => "added by both",
        _ => "modified by both",
    };
}
