# Git Soyutlama Katmanı

Karar gerekçesi: [ADR-0002](../adr/0002-git-integration.md).

## 1. Adapter

```csharp
public interface IGitRunner
{
    GitExecutable Executable { get; }
    event EventHandler<GitCommandEventArgs>? CommandStarted;
    event EventHandler<GitCommandEventArgs>? CommandCompleted;

    Task<GitCommandResult> RunAsync(GitCommand command, CancellationToken ct = default);
}

public sealed record GitCommand(string WorkingDirectory, IReadOnlyList<string> Arguments)
{
    public bool ReadOnly { get; init; }                        // GIT_OPTIONAL_LOCKS=0
    public byte[]? StandardInput { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    public TimeSpan? Timeout { get; init; }
    public IProgress<string>? Progress { get; init; }           // clone/fetch stderr ilerlemesi
    public bool IsInternal { get; init; }                        // snapshot/pin gibi iç komutlar Git Console'da gizlenir
}

public sealed record GitCommandResult(
    GitCommand Command, int ExitCode, byte[] StandardOutputBytes, string StandardError, TimeSpan Duration)
{
    public string StandardOutput { get; }                        // UTF-8 çözümlenmiş (bayt çıktı: StandardOutputBytes)
    public bool Success => ExitCode == 0;
    public GitCommandResult EnsureSuccess();                     // başarısızsa GitException(GitError)
}
```

`GitProcessRunner` sorumlulukları: git'i bulmak (`GitLocator`), ortamı sabitlemek, stdout/stderr'i
eşzamanlı okumak (deadlock yok), iptalde süreç ağacını öldürmek, olay yayınlamak, URL'lerdeki kimlik
bilgisini loglarda maskelemek.

## 2. Servisler

Her servis tek bir alanın sahibidir ve yalnızca `IGitRunner` + `RepositoryInfo` alır.
`GitRepository` facade'ı servisleri bir araya getirir; kimse servisleri kendi başına oluşturmak zorunda kalmaz.

| Servis | Başlıca metotlar | Git komutları |
|---|---|---|
| `StatusService` | `GetStatusAsync`, `GetState` | `status --porcelain=v2 --branch -z --untracked-files=all`, `.git` durum dosyaları |
| `HistoryService` | `GetCommitsAsync(LogQuery)`, `GetCommitDetailsAsync`, `GetFileHistoryAsync`, `SearchAsync`, `SearchChangedCodeAsync`, `GetRangeAsync`, `CountAheadBehindAsync`, `GetMergeBaseAsync` | `log --format=… --topo-order`, `diff-tree --name-status/--numstat`, `log --follow`, `log -S`, `rev-list --left-right --count` |
| `RefService` | `GetRefsAsync`, `GetRefTargetsAsync`, `GetHeadAsync`, `ResolveCommitAsync`, `UpdateRefsAsync` (atomik), `SetHeadToBranchAsync`, `DetachHeadAsync`, `IsValidBranchNameAsync` | `for-each-ref`, `symbolic-ref`, `rev-parse`, `update-ref --stdin`, `check-ref-format` |
| `BranchService` | `CreateAsync`, `DeleteAsync`, `RenameAsync`, `SwitchAsync`, `SwitchToRemoteAsync`, `SwitchDetachedAsync`, `SetUpstreamAsync` | `branch`, `switch` |
| `WorkingTreeService` | `StageAsync`, `UnstageAsync`, `StageAllAsync`, `RestoreWorktreeAsync`, `RestoreFromHeadAsync`, `DeleteUntrackedFiles`, `ApplyPatchAsync` (hunk stage), `CommitAsync`, `GetHeadMessageAsync` | `add --pathspec-from-file`, `restore`, `apply --cached`, `commit -F -` |
| `DiffService` | `GetUnstagedAsync`, `GetStagedAsync`, `GetCommitDiffAsync`, `GetDiffBetweenAsync`, `GetStagedPatchAsync`, `GetCommitPatchAsync`, `GetUntrackedFileDiff` | `diff`, `diff --cached`, `show` (+ bellek içi Myers diff: `TextDiff`) |
| `IntegrationService` | `MergeAsync`, `PredictMergeAsync`, `RebaseAsync`, `InteractiveRebaseAsync`, `CherryPickAsync`, `RevertAsync`, `ResetAsync`, `AbortAsync`, `ContinueAsync`, `SkipAsync` | `merge`, `merge-tree --write-tree`, `rebase [-i]`, `cherry-pick`, `revert`, `reset` |
| `StashService` | `ListAsync`, `PushAsync`, `ApplyAsync`, `PopAsync`, `DropAsync`, `StoreAsync` | `stash` |
| `ReflogService` | `GetAsync(ref)` | `reflog show --date=unix` |
| `RemoteService` | `ListAsync`, `AddAsync`, `RemoveAsync`, `FetchAsync`, `PullAsync`, `PushAsync` (`--force-with-lease`), `DeleteRemoteBranchAsync` | `remote`, `fetch`, `pull`, `push` |
| `TagService` | `CreateAsync`, `DeleteAsync` | `tag` |
| `BlameService` | `BlameAsync(path, rev)` | `blame --line-porcelain` |
| `ObjectService` | `ReadBlobAsync`, `ListTreeAsync`, `ListIndexAsync`, `WriteTreeFromIndexCopyAsync`, `CommitTreeAsync`, `ReadTreeIntoIndexAsync`, `CheckoutIndexAsync`, `FindDanglingCommitsAsync` | `cat-file`, `ls-tree`, `ls-files -s`, `write-tree` (GIT_INDEX_FILE), `commit-tree`, `read-tree --reset`, `checkout-index --stdin`, `fsck` |
| `ConflictService` | `GetConflictsAsync`, `ReadStageAsync`, `ResolveWithSideAsync`, `SaveResolutionAsync` | `ls-files -u`, `cat-file blob :N:path`, `checkout --ours/--theirs` |
| `WorktreeService` | `ListAsync`, `AddAsync`, `RemoveAsync` | `worktree` |
| `BisectService` | `StartAsync`, `MarkAsync`, `ResetAsync` | `bisect` |
| `ConfigService` | `GetAsync`, `SetAsync` | `config` |

## 3. Interactive rebase (editörsüz)

Git'in interaktif editörü GUI'de kullanılamaz. Todo listesi uygulama tarafından üretilir ve
`GIT_SEQUENCE_EDITOR` hazır dosyayı Git'in todo dosyasının üzerine kopyalayan bir komutla ayarlanır:

| UI işlemi | Üretilen todo |
|---|---|
| Sıralama | satır sırası |
| pick | `pick <sha>` |
| drop | `drop <sha>` |
| edit | `edit <sha>` (rebase durur, UI "Continue" gösterir) |
| squash | `squash <sha>` + zincirin sonunda `exec git commit --amend -F <msgfile>` |
| fixup | `fixup <sha>` |
| reword | `pick <sha>` + `exec git commit --amend --only -F <msgfile>` |

`GIT_EDITOR=true` ile Git hiçbir zaman etkileşimli editör açmaz.

## 4. Commit grafiği yerleşimi

`CommitGraphLayout` saf bir algoritmadır (UI'dan bağımsız, test edilebilir):

1. Commit'ler `--topo-order` ile gelir. Aktif lane dizisi "beklenen sha"ları tutar.
2. Commit, onu bekleyen ilk lane'e yerleşir; yoksa ilk boş lane ayrılır.
3. Aynı commit'i bekleyen diğer lane'ler düğüme birleşir (merge çizgileri) ve boşalır.
4. İlk parent düğümün lane'ini devralır; diğer parent'lar mevcut lane'lerine bağlanır ya da yeni lane açar.
5. Her satır için `GraphRow { NodeLane, NodeColor, Edges[] }` üretilir. Kenarlar satırın üst yarısından
   ortasına veya ortasından alt yarısına çizilir; böylece her satır bağımsız çizilebilir (sanallaştırma dostu).
6. Lane renkleri lane açıldığında atanır ve lane yaşadıkça sabit kalır.

Sayfalama: `CommitGraphLayout` örneği durumu (aktif lane'ler) saklar; `Append` yeni sayfayı kaldığı yerden yerleştirir.

## 5. Hata çevirisi

```csharp
public sealed record GitError(
    GitErrorCode Code, string Title, string Explanation, string? Suggestion,
    string CommandLine, int ExitCode, string RawOutput);
```

Örnek eşlemeler:

| stderr kalıbı | Code | Kullanıcıya |
|---|---|---|
| `would be overwritten by checkout/merge` | `LocalChangesWouldBeOverwritten` | "Your uncommitted changes would be lost." + "Save my current work (stash) and try again" |
| `CONFLICT (` | `MergeConflict` | "Some files were changed in both branches." |
| `[rejected] … (non-fast-forward)` / `fetch first` | `PushRejectedNonFastForward` | "The remote has commits you don't have yet." + "Pull first" |
| `Authentication failed`, `Permission denied (publickey)` | `AuthenticationFailed` | kimlik doğrulama rehberi |
| `Could not resolve host` | `NetworkUnavailable` | ağ mesajı |
| `index.lock': File exists` | `RepositoryLocked` | "Another Git process is running." |
| `not fully merged` | `BranchNotFullyMerged` | "This branch has commits that exist nowhere else." (+ recovery güvencesi) |
| `nothing to commit` | `NothingToCommit` | |
| `not a git repository` | `NotARepository` | |
| `refusing to merge unrelated histories` | `UnrelatedHistories` | |
| `You have unstaged changes` / `cannot rebase` | `DirtyWorkingTree` | |
| `already exists` | `AlreadyExists` | |
| diğer | `Unknown` | "Git reported an error." + ham çıktı |
