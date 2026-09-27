# ADR-0004 — Recovery modeli ve Safe Git operasyon hattı

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-16
- **Detaylı tasarım:** [architecture/recovery-model.md](../architecture/recovery-model.md)

## Bağlam

Ürünün ana prensibi: **"Her tehlikeli işlem geri alınabilir."** Bu, UI'daki bir onay kutusu değil,
tüm Git işlemlerinin geçtiği zorunlu bir hat olmalı. Aksi hâlde yeni eklenen her özellik güvenlik
garantisini sessizce delebilir.

## Karar

### 1. Her durum değiştiren işlem `SafeOperationRunner` üzerinden çalışır

```text
validate ──► create recovery point (before) ──► execute ──► verify ──► create recovery point (after)
                                                                          │
                                                          record GitOperation + expose Undo
```

- `IGitOperation` arayüzü: `Kind`, `Title`, `Risk` (`Safe`, `Reversible`, `Destructive`, `Remote`),
  `RefScope`, `ValidateAsync`, `ExecuteAsync`, `VerifyAsync`, `DescribeCommand`.
- `Risk >= Reversible` olan işlemler recovery point oluşturmadan **çalıştırılamaz**. Recovery point
  oluşturulamazsa (disk dolu, repo bozuk) işlem iptal edilir ve kullanıcıya nedeni gösterilir.
- Başarısız veya yarım kalan (conflict) işlemler de kaydedilir; `before` noktası her zaman geri dönüş yoludur.

### 2. Recovery point = ref durumu + HEAD + çalışma alanı snapshot'ı + stash listesi

Tek bir kayıtta şunlar tutulur: zaman, işlem, branch, `HEAD` (sembolik/detached), ilgili ref'lerin
hedefleri (`refs/heads`, `refs/remotes`, gerekirse `refs/tags`), stash girdileri, repository durumu
(merging/rebasing…), değişmiş dosya sayısı ve snapshot id'si.

### 3. Undo = "before" noktasına göre hesaplanan bir **RestorePlan**

Undo kör bir `reset` değildir. `RestorePlanner`, before/after/current durumlarını karşılaştırıp
açık adımlardan oluşan bir plan üretir:

`AbortInProgressAction` · `RefChangeAction` (oluştur/taşı/sil) · `RenameBranchAction` · `SetHeadAction` ·
`RestoreWorkingTreeAction` · `StoreStashAction` · `DropStashAction` · `RestoreRemoteBranchAction`

- Yalnızca işlemin **değiştirdiği** ref'ler geri alınır; kullanıcının başka branch'lerde yaptığı işler etkilenmez.
- Bir ref işlemden sonra tekrar değişmişse plan uyarı içerir.
- Plan çalıştırılmadan önce **"Before undo" recovery point** alınır → undo da geri alınabilir (redo).
- Plan UI'da insan diliyle ve (Pro modda) eşdeğer Git komutlarıyla önizlenir.

### 4. Uzak sunucuyu etkileyen geri alma otomatik yapılmaz

Force push geri alma planı `--force-with-lease` ile eski commit'i geri iten ayrı bir adım olarak
üretilir ve **her zaman ayrı ve açık kullanıcı onayı** ister. Normal push'un geri alınması için
"Revert commit" önerilir.

### 5. Recovery Center üç kaynağı birleştirir

1. Uygulamanın recovery point'leri ve Time Machine snapshot'ları (SQLite),
2. Git reflog (`HEAD` ve branch reflog'ları),
3. İsteğe bağlı derin tarama: `git fsck --unreachable --no-reflogs` ile kayıp commit ve stash'ler.

Uygulama dışında (terminalde) yapılan işlemler de reflog üzerinden görünür ve kurtarılabilir.

## Sonuçlar

- Yeni bir Git özelliği eklemek = yeni bir `IGitOperation` yazmak; güvenlik garantisi otomatik gelir.
- Kabul testleri "hata yap → undo → orijinal durum" senaryoları olarak yazılır
  (reset --hard, branch delete, rebase, amend, stash drop, clean, yanlış branch'e commit, checkout).
- Hatalar ham Git çıktısı yerine `GitError` (başlık, açıklama, öneri, teknik detay) olarak gösterilir.
