# Recovery Modeli

Karar gerekçesi: [ADR-0004](../adr/0004-recovery-and-safe-operations.md).

```text
UI (Undo düğmesi, Recovery Center, işlem diyalogları)
 │
UndoService · RecoveryTimelineService
 │
SafeOperationRunner ─────── RestorePlanner ─── RestoreExecutor
 │                                                  │
RecoveryPointService ── SnapshotEngine ─────────────┘
 │
Git services · SQLite
```

## 1. Varlıklar

```text
GitOperation 1 ──── 0..1 RecoveryPoint (before)
             1 ──── 0..1 RecoveryPoint (after)
             0..1 ─ undo_of ─► GitOperation

RecoveryPoint 1 ── * RecoveryRef        refs/heads/*, refs/remotes/*, (refs/tags/*)
              1 ── * RecoveryStash      stash@{n} → sha + mesaj
              * ── 0..1 Snapshot        index + working tree
```

| Alan | Açıklama |
|---|---|
| `created_at` | timestamp |
| `kind` | `before-operation`, `after-operation`, `before-restore`, `manual` |
| `title` | "Before reset --hard", "Before rebase feature/login onto main" |
| `branch`, `head_ref`, `head_sha` | HEAD sembolik ise branch, değilse detached sha |
| `repo_state` | `clean`, `merging`, `rebasing`, `cherry-picking`, `reverting`, `bisecting` |
| `changed_file_count` | snapshot overlay girdisi sayısı |
| `snapshot_id` | çalışma alanı snapshot'ı |
| `operation_id` | ilgili işlem |
| `is_pinned` | kullanıcı sabitledi → retention silmez |
| `metadata_json` | işleme özel bilgi (ör. rename: eski/yeni ad; push: remote, eski uzak sha) |

## 2. İşlem risk sınıfları

| Risk | Örnekler | Davranış |
|---|---|---|
| `Safe` | fetch, stage/unstage | recovery point yok, kayıt var |
| `Reversible` | commit, branch create, checkout, merge, stash push, tag create | otomatik recovery point, onay yok, işlem sonrası Undo toast'ı |
| `Destructive` | reset --hard, branch delete, rebase, amend, cherry-pick, revert, stash drop, clean/discard | otomatik recovery point + **onay diyaloğu** (recovery point zamanı gösterilir) |
| `Remote` | push, force push | Destructive kuralları + uzak etki uyarısı; force push daima `--force-with-lease` |

Onay diyaloğu:

```text
You are about to perform:  RESET --HARD
  Branch feature/login will move to 3e1a0c2 "Fix validation".
  12 modified files will be overwritten.

Recovery point:  Today 13:42:18   (created automatically)

[Cancel]                                                    [Continue]
```

İşlem sonrası:

```text
✓ Reset completed.                        [Undo]  [View Recovery Point]
```

## 3. SafeOperationRunner

```csharp
public async Task<OperationOutcome> RunAsync(RepositoryContext repo, IGitOperation op, CancellationToken ct)
{
    var validation = await op.ValidateAsync(repo, ct);           // 1. validate
    if (!validation.CanProceed) return OperationOutcome.Blocked(validation);

    var record = operations.Start(repo, op);
    RecoveryPoint? before = null;
    if (op.Risk >= OperationRisk.Reversible)
        before = await points.CaptureAsync(repo, PointKind.BeforeOperation, op, ct);   // 2. recovery point

    var exec = await op.ExecuteAsync(repo, ct);                   // 3. execute
    var verify = exec.Succeeded ? await op.VerifyAsync(repo, exec, ct) : null;       // 4. verify

    RecoveryPoint? after = before is null ? null
        : await points.CaptureAsync(repo, PointKind.AfterOperation, op, ct);
    await pins.ProtectChangedRefsAsync(repo, before, after, ct);  // kaybolabilecek commit'leri pinle

    operations.Finish(record, exec, verify, before, after);       // 5. record
    return OperationOutcome.From(exec, verify, before, after, undoAvailable: before is not null); // 6. undo
}
```

- `before` alınamazsa işlem **çalıştırılmaz**.
- Conflict ile duran merge/rebase/cherry-pick `Conflicted` durumunda kaydedilir; Undo = abort + before.
- Doğrulama başarısızsa (ör. reset sonrası HEAD hedefte değil) kullanıcıya "Operation finished but the result
  looks unexpected" + Undo gösterilir.

## 4. RestorePlanner kuralları

Girdi: `before`, `after` (varsa), `current` (şu anki refs/HEAD/stash/state).

| Karşılaştırma | Üretilen adım |
|---|---|
| current state ≠ clean | `AbortInProgress(merge/rebase/cherry-pick/revert)` |
| ref before'da var, after'da yok | `RefChangeAction(name, current=null, target=before)` — yeniden oluştur |
| ref before'da yok, after'da var | `RefChangeAction(name, current, target=null)` — sil |
| ref before ≠ after | `RefChangeAction(name, current, target=before)` — geri taşı |
| rename metadata | `RenameBranchAction(new → old)` (config/upstream korunur) |
| HEAD farklı | `SetHeadAction(symbolic branch | detached sha)` |
| before/after snapshot parmak izi farklı | `RestoreWorkingTreeAction(before.snapshot)` |
| stash before'da var, current'ta yok | `StoreStashAction(sha, message)` |
| stash after'da var, before'da yok | `DropStashAction(sha)` |
| remote ref before ≠ after (force push) | `RestoreRemoteBranchAction(remote, branch, before, lease=after)` — **varsayılan seçili değil, ayrı onay** |
| herhangi bir yarım işlem | `AbortInProgressAction(state)` |

- `current` değeri `after`'dan farklıysa adım uyarı taşır: "feature/login has new commits since the operation.
  They will be kept in the recovery point."
- Uzak işlem dışındaki tüm adımlar lokaldir.
- Recovery Center'dan keyfi bir noktaya dönüş için `after` yoktur; planner `before` ile `current`'ı
  karşılaştırır ve kullanıcı adımları tek tek seçebilir:
  **Restore files only · Restore branch pointers · Recreate deleted branches · Restore everything.**

## 5. RestoreExecutor sırası

```text
1. "Before restore/undo" recovery point  (başarısızsa dur)
2. AbortInProgress
3. Ref adımları   → tek git update-ref --stdin transaction'ı (current değerler "expected" olarak, atomik)
4. RenameBranch
5. SetHead        → git symbolic-ref HEAD refs/heads/x  |  git update-ref --no-deref HEAD <sha>
6. RestoreWorkingTree
7. Stash adımları
8. Doğrulama + GitOperation(kind=undo, undo_of=…) kaydı
```

## 6. Recovery Center zaman çizelgesi

`RecoveryTimelineService` üç kaynağı tek listede, zamana göre gruplanmış (Today / Yesterday / tarih) birleştirir:

| Kaynak | Girdi türü | Geri dönüş |
|---|---|---|
| `recovery_point` | Before Rebase, Before Reset, Commit… | Restore planı |
| `snapshot` (timer) | Working Tree Snapshot | Files only restore, dosya görüntüleme |
| `git reflog` (HEAD + branch'ler) | "checkout: moving from…", "commit: …" | Create branch here / Reset branch here (safe) |
| `git fsck --unreachable` (isteğe bağlı derin tarama) | Lost commit, lost stash | Create branch here / Restore stash |

Uygulama tarafından yapılan işlemlerin reflog kayıtları, recovery point ile aynı sha ve ±2 sn içindeyse
tekilleştirilir (aynı olay iki kez görünmez).

Undo/redo ve Recovery Center geri dönüşleri de `RestoreOperation` olarak aynı `SafeOperationRunner` hattından geçer
(kind = `undo` / `redo` / `restore`). Böylece her geri dönüşten önce bir "Before restore" noktası alınır.

UI davranışı: planda uyarı veya uzak adım yoksa undo tek tıkla uygulanır (Tower tarzı); aksi hâlde plan
önizlemesi gösterilir. Recovery Center'dan yapılan geri dönüşlerde plan her zaman gösterilir.

## 7. Kabul senaryoları (otomatik test)

Tümü `tests/ArChrono.Tests/Recovery/UndoScenarioTests.cs` içinde gerçek Git repository'leri üzerinde doğrulanır.

| # | Hata | Beklenen |
|---|---|---|
| 1 | Commit edilmemiş değişiklikler varken `reset --hard` | Undo sonrası dosyalar, index ve HEAD aynı |
| 2 | Merge edilmemiş branch'i silmek | Undo sonrası branch aynı commit'te |
| 3 | Yanlış rebase | Undo sonrası branch orijinal commit'lerinde |
| 4 | Yanlış branch'e commit | Undo sonrası branch geri, değişiklikler staged |
| 5 | Amend ile mesaj/içerik kaybı | Undo sonrası eski commit ve index |
| 6 | `stash drop` | Undo sonrası stash listede |
| 7 | Untracked dosyaları temizleme (discard/clean) | Undo sonrası dosyalar geri |
| 8 | Checkout sonrası beklenmeyen durum | Undo sonrası eski branch ve değişiklikler |
| 9 | Undo'yu geri alma | Before-undo noktasına dönüş |
| 10 | Conflict'te kalan merge | Undo = abort + orijinal durum |
