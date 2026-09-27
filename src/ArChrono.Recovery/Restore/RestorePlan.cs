using ArChrono.Git.Models;
using ArChrono.Localization;

namespace ArChrono.Recovery.Restore;

/// <summary>Geri dönüş planının tek bir adımı. Plan UI'da önizlenir, kullanıcı adımları seçebilir.</summary>
public abstract record RestoreAction
{
    public bool Selected { get; init; } = true;

    /// <summary>Uzak sunucuyu etkiler: her zaman ayrıca onay ister, varsayılan seçili değildir.</summary>
    public virtual bool IsRemote => false;

    public string? Warning { get; init; }

    public abstract string Description { get; }

    public abstract string CommandPreview { get; }

    /// <summary>Yürütme sırası (küçük önce).</summary>
    public abstract int Order { get; }
}

public sealed record AbortInProgressAction(RepositoryState State) : RestoreAction
{
    public override string Description => Loc.T($"Stop the unfinished {Operations.AbortOperation.Describe(State)}", $"Yarım kalan işlemi durdur ({Operations.AbortOperation.Describe(State)})");
    public override string CommandPreview => State switch
    {
        RepositoryState.Merging => "git merge --abort",
        RepositoryState.Rebasing => "git rebase --abort",
        RepositoryState.CherryPicking => "git cherry-pick --abort",
        RepositoryState.Reverting => "git revert --abort",
        RepositoryState.ApplyingPatches => "git am --abort",
        _ => "",
    };
    public override int Order => 0;
}

/// <summary>Ref oluşturma/güncelleme/silme. <see cref="TargetSha"/> null ise ref silinir; <see cref="CurrentSha"/> null ise oluşturulur.</summary>
public sealed record RefChangeAction(string RefName, string? CurrentSha, string? TargetSha) : RestoreAction
{
    private string Name => RefNames.Shorten(RefName);
    private bool IsTag => RefName.StartsWith(RefNames.TagsPrefix, StringComparison.Ordinal);
    private string Noun => IsTag ? "tag" : "branch";

    public override string Description => (CurrentSha, TargetSha) switch
    {
        (null, not null) => Loc.T($"Recreate {Noun} {Name} at {Short(TargetSha)}", $"{Name} {Noun}'ini {Short(TargetSha)} üzerinde yeniden oluştur"),
        (not null, null) => Loc.T($"Remove {Noun} {Name} (created by the operation)", $"{Name} {Noun}'ini kaldır (işlem tarafından oluşturuldu)"),
        _ => Loc.T($"Move {Noun} {Name} back from {Short(CurrentSha)} to {Short(TargetSha)}", $"{Name} {Noun}'ini {Short(CurrentSha)} konumundan {Short(TargetSha)} konumuna geri taşı"),
    };

    public override string CommandPreview => TargetSha is null
        ? $"git update-ref -d {RefName}"
        : $"git update-ref {RefName} {Short(TargetSha)}";

    public override int Order => 1;

    internal static string Short(string? sha) => sha is null ? Loc.T("(none)", "(yok)") : sha[..Math.Min(7, sha.Length)];
}

public sealed record RenameBranchAction(string CurrentName, string RestoredName) : RestoreAction
{
    public override string Description => Loc.T($"Rename branch {CurrentName} back to {RestoredName}", $"{CurrentName} branch'inin adını yeniden {RestoredName} yap");
    public override string CommandPreview => $"git branch -m {CurrentName} {RestoredName}";
    public override int Order => 2;
}

public sealed record SetHeadAction(string? BranchRef, string? Sha) : RestoreAction
{
    public override string Description => BranchRef is not null
        ? Loc.T($"Switch back to branch {RefNames.Shorten(BranchRef)}", $"{RefNames.Shorten(BranchRef)} branch'ine geri geç")
        : Loc.T($"Switch back to commit {RefChangeAction.Short(Sha)} (not on a branch)", $"{RefChangeAction.Short(Sha)} commit'ine geri geç (branch dışında)");
    public override string CommandPreview => BranchRef is not null
        ? $"git symbolic-ref HEAD {BranchRef}"
        : $"git update-ref --no-deref HEAD {RefChangeAction.Short(Sha)}";
    public override int Order => 3;
}

public sealed record RestoreWorkingTreeAction(long SnapshotId, DateTimeOffset SnapshotTime, int ChangedFileCount) : RestoreAction
{
    public bool RemoveFilesCreatedAfterSnapshot { get; init; }

    public override string Description => ChangedFileCount == 0
        ? Loc.T($"Restore your files and staging area as they were at {SnapshotTime:HH:mm:ss}", $"Dosyalarınızı ve staging alanını {SnapshotTime:HH:mm:ss} anındaki hâline getir")
        : Loc.T($"Restore your files and staging area as they were at {SnapshotTime:HH:mm:ss} ({ChangedFileCount} uncommitted change{(ChangedFileCount == 1 ? "" : "s")})", $"Dosyalarınızı ve staging alanını {SnapshotTime:HH:mm:ss} anındaki hâline getir ({ChangedFileCount} commit edilmemiş değişiklik)");
    public override string CommandPreview => $"archrono restore-snapshot #{SnapshotId}  (git read-tree + checkout-index + stored files)";
    public override int Order => 4;
}

public sealed record StoreStashAction(string Sha, string Message) : RestoreAction
{
    public override string Description => Loc.T($"Bring back stash \"{Message}\"", $"\"{Message}\" stash'ini geri getir");
    public override string CommandPreview => $"git stash store -m \"{Message}\" {RefChangeAction.Short(Sha)}";
    public override int Order => 5;
}

public sealed record DropStashAction(string Sha, string Message) : RestoreAction
{
    public override string Description => Loc.T($"Remove stash \"{Message}\" (created by the operation)", $"\"{Message}\" stash'ini kaldır (işlem tarafından oluşturuldu)");
    public override string CommandPreview => $"git stash drop <{RefChangeAction.Short(Sha)}>";
    public override int Order => 6;
}

public sealed record RestoreRemoteBranchAction(string Remote, string Branch, string TargetSha, string ExpectedRemoteSha) : RestoreAction
{
    public override bool IsRemote => true;
    public override string Description => Loc.T($"Put {Remote}/{Branch} back to {RefChangeAction.Short(TargetSha)} on the server (force push with lease)", $"Sunucudaki {Remote}/{Branch} branch'ini {RefChangeAction.Short(TargetSha)} konumuna geri al (force push with lease)");
    public override string CommandPreview => $"git push --force-with-lease=refs/heads/{Branch}:{RefChangeAction.Short(ExpectedRemoteSha)} {Remote} {RefChangeAction.Short(TargetSha)}:refs/heads/{Branch}";
    public override int Order => 7;
}

public sealed record RestorePlan(string Title, long TargetPointId, DateTimeOffset TargetTime, IReadOnlyList<RestoreAction> Actions, IReadOnlyList<string> Notes)
{
    public IEnumerable<RestoreAction> SelectedActions => Actions.Where(a => a.Selected).OrderBy(a => a.Order);

    public bool IsEmpty => !Actions.Any(a => a.Selected);

    public bool HasRemoteActions => SelectedActions.Any(a => a.IsRemote);

    public RestorePlan WithSelection(RestoreAction action, bool selected) =>
        this with { Actions = Actions.Select(a => ReferenceEquals(a, action) ? a with { Selected = selected } : a).ToList() };
}

public enum RestoreMode
{
    /// <summary>Branch'ler, HEAD, dosyalar ve stash'ler.</summary>
    Everything,
    /// <summary>Yalnızca çalışma alanı ve staging area; branch'lere dokunulmaz.</summary>
    FilesOnly,
    /// <summary>Yalnızca branch/tag işaretçileri ve HEAD.</summary>
    BranchesOnly,
}
