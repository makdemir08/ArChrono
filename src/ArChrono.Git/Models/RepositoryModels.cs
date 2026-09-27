namespace ArChrono.Git.Models;

public sealed record RepositoryInfo(string RootPath, string GitDir, string CommonDir, string ObjectFormat)
{
    public string Name => Path.GetFileName(RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>Bu çalışma dizini ana repository'nin bağlı bir worktree'si mi?</summary>
    public bool IsLinkedWorktree => !PathsEqual(GitDir, CommonDir);

    public string IndexFilePath => Path.Combine(GitDir, "index");

    internal static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

/// <summary>Yarım kalmış çok adımlı Git işlemi.</summary>
public enum RepositoryState
{
    Clean,
    Merging,
    Rebasing,
    ApplyingPatches,
    CherryPicking,
    Reverting,
    Bisecting,
}

public static class RepositoryStateExtensions
{
    public static string ToStorageValue(this RepositoryState state) => state switch
    {
        RepositoryState.Clean => "clean",
        RepositoryState.Merging => "merging",
        RepositoryState.Rebasing => "rebasing",
        RepositoryState.ApplyingPatches => "applying",
        RepositoryState.CherryPicking => "cherry-picking",
        RepositoryState.Reverting => "reverting",
        RepositoryState.Bisecting => "bisecting",
        _ => "clean",
    };

    public static RepositoryState FromStorageValue(string? value) => value switch
    {
        "merging" => RepositoryState.Merging,
        "rebasing" => RepositoryState.Rebasing,
        "applying" => RepositoryState.ApplyingPatches,
        "cherry-picking" => RepositoryState.CherryPicking,
        "reverting" => RepositoryState.Reverting,
        "bisecting" => RepositoryState.Bisecting,
        _ => RepositoryState.Clean,
    };

    /// <summary>Abort edilebilen (commit akışını bloke eden) durumlar.</summary>
    public static bool IsBlocking(this RepositoryState state) =>
        state is RepositoryState.Merging or RepositoryState.Rebasing or RepositoryState.ApplyingPatches
            or RepositoryState.CherryPicking or RepositoryState.Reverting;
}

/// <summary>HEAD'in durumu.</summary>
public sealed record HeadInfo(string? BranchRef, string? Sha)
{
    public bool IsDetached => BranchRef is null && Sha is not null;
    public bool IsUnborn => Sha is null;
    public string? BranchName => BranchRef is null ? null : RefNames.Shorten(BranchRef);
}

public static class RefNames
{
    public const string HeadsPrefix = "refs/heads/";
    public const string RemotesPrefix = "refs/remotes/";
    public const string TagsPrefix = "refs/tags/";
    public const string Stash = "refs/stash";
    public const string ArChronoPrefix = "refs/archrono/";
    public const string PinsPrefix = "refs/archrono/pins/";

    public static string Shorten(string fullName)
    {
        foreach (var prefix in new[] { HeadsPrefix, RemotesPrefix, TagsPrefix })
        {
            if (fullName.StartsWith(prefix, StringComparison.Ordinal)) return fullName[prefix.Length..];
        }
        return fullName.StartsWith("refs/", StringComparison.Ordinal) ? fullName[5..] : fullName;
    }

    public static string Branch(string shortName) =>
        shortName.StartsWith(HeadsPrefix, StringComparison.Ordinal) ? shortName : HeadsPrefix + shortName;

    public static string Tag(string shortName) =>
        shortName.StartsWith(TagsPrefix, StringComparison.Ordinal) ? shortName : TagsPrefix + shortName;
}

public sealed record RemoteInfo(string Name, string FetchUrl, string PushUrl);

public sealed record WorktreeInfo(string Path, string? HeadSha, string? BranchRef, bool IsBare, bool IsDetached, bool IsLocked, bool IsPrunable)
{
    public string? BranchName => BranchRef is null ? null : RefNames.Shorten(BranchRef);
}
