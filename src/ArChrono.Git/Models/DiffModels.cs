namespace ArChrono.Git.Models;

public enum DiffLineKind
{
    Context,
    Added,
    Removed,
    NoNewlineMarker,
}

public sealed record DiffLine(DiffLineKind Kind, string Text, int? OldLineNumber, int? NewLineNumber);

public sealed record DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, string Header, IReadOnlyList<DiffLine> Lines)
{
    public string HeaderLine => $"@@ -{OldStart},{OldCount} +{NewStart},{NewCount} @@{(string.IsNullOrEmpty(Header) ? "" : " " + Header)}";
}

public sealed record FileDiff(
    string? OldPath,
    string? NewPath,
    ChangeKind Change,
    bool IsBinary,
    int? OldMode,
    int? NewMode,
    IReadOnlyList<DiffHunk> Hunks,
    bool IsTruncated = false)
{
    public string Path => NewPath ?? OldPath ?? string.Empty;
    public int Additions => Hunks.Sum(h => h.Lines.Count(l => l.Kind == DiffLineKind.Added));
    public int Deletions => Hunks.Sum(h => h.Lines.Count(l => l.Kind == DiffLineKind.Removed));
    public bool ModeChanged => OldMode is not null && NewMode is not null && OldMode != NewMode;
}
