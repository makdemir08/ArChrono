# Snapshot Engine ve Code Time Machine

Karar gerekçesi: [ADR-0003](../adr/0003-snapshot-storage.md).

```text
UI (Time Machine, Recovery Center)
 │
TimeMachineService / RecoveryPointService
 │
SnapshotEngine ──────────────── WorkingTreeRestorer
 │                                  │
ContentStore (store/objects)      Git plumbing (read-tree, checkout-index, cat-file)
 │
Local Disk
```

## 1. Snapshot'ın anatomisi

```text
Snapshot #1284   2026-09-16 13:42:18   trigger=operation  label="Before reset --hard"
├── head_ref     refs/heads/feature/login
├── head_sha     9f2c…            ← Git'te zaten var
├── index_tree   41ab…            ← write-tree (geçici index kopyasından)
├── pin_commit   c07e…            ← commit-tree index_tree -p head_sha  → refs/archrono/pins/c07e…
├── fingerprint  sha256(head_sha, index_tree, overlay)
└── overlay (snapshot_entry)
    ├── src/Auth/AuthService.cs   modified   blob 5d1e…  100644   ← ContentStore
    ├── src/Auth/Token.cs         deleted
    └── notes/todo.md             untracked  blob a7c9…  100644   ← ContentStore
```

Bir dosyanın snapshot anındaki içeriği:

1. Overlay'de varsa → overlay (store'dan blob ya da "silinmiş").
2. Yoksa → `index_tree` içindeki blob (Git nesne veritabanından, pin commit sayesinde korunur).
3. İkisinde de yoksa → dosya o anda yoktu.

## 2. Snapshot alma algoritması

```text
CaptureAsync(repo, trigger, label)
 1. status  ← git status --porcelain=v2 --branch -z --untracked-files=all      (GIT_OPTIONAL_LOCKS=0)
 2. head    ← status başlıkları (branch.oid, branch.head)
 3. index_tree
      copy .git/index → temp/index-<guid>
      GIT_INDEX_FILE=temp git write-tree
      (unmerged entry varsa write-tree başarısız olur → index_tree = HEAD^{tree};
       conflict'li dosyaların çalışma alanı içeriği overlay'e "conflicted" olarak alınır)
 4. overlay
      her status girdisi için:
        ordinary/renamed (worktree tarafı değişmiş)  → dosyayı oku, hash'le, store'a yaz  → modified
        worktree'de silinmiş                          → deleted
        untracked (?)                                 → oku, hash'le, store'a yaz       → untracked
        unmerged (u)                                  → oku, hash'le, store'a yaz       → conflicted
        submodule / boyut sınırını aşan / okunamayan  → skipped listesine
 5. fingerprint hesapla; son snapshot ile aynıysa → "unchanged" döndür (kayıt yok)
 6. pin_commit
      (head_sha, index_tree) için daha önce pin varsa yeniden kullan
      yoksa: git commit-tree index_tree [-p head_sha] -m "ArChrono snapshot pin"
             git update-ref refs/archrono/pins/<sha> <sha>
 7. SQLite transaction: snapshot + snapshot_entry + content_blob + git_pin
```

Karmaşıklık: Git'in stat cache'i sayesinde **O(değişen dosya)** okuma + tek `status` çağrısı.
Temiz bir repository'de snapshot maliyeti bir `status` + bir `write-tree` çağrısıdır.

### Dosya okuma kuralları

- Symlink: hedef yolu içerik olarak saklanır, mode `120000` (Git semantiği).
- Çalıştırılabilir bit: Unix'te `File.GetUnixFileMode`, Windows'ta Git'in bildirdiği mode.
- Dosya boyutu sınırı: varsayılan 25 MB (`SnapshotOptions.MaxFileSizeBytes`).
- Snapshot başına sınır: en fazla 5.000 overlay dosyası ve 512 MB ham içerik (`MaxOverlayFiles`, `MaxOverlayBytes`).
  Yanlışlıkla ignore edilmemiş devasa klasörler (ör. derleme çıktıları) store'u şişiremez; aşan dosyalar "skipped" olur.
- Okuma sırasında değişen dosya: boyut/mtime okuma öncesi ve sonrası karşılaştırılır, farklıysa bir kez tekrar denenir.

## 3. Content store

| Özellik | Değer |
|---|---|
| Anahtar | Git uyumlu blob id (repo'nun object format'ına göre SHA-1/SHA-256) |
| Yol | `objects/ab/cdef…` |
| Dosya biçimi | 8 byte başlık (`ACB1` + 1 byte sıkıştırma + 3 byte ayrılmış) + gövde |
| Sıkıştırma | Brotli; kazanç < %10 ise ya da bilinen sıkıştırılmış uzantılarda ham |
| Yazma | `tmp/<guid>` → fsync → atomik `Move` |
| Okuma doğrulaması | açılan içeriğin blob id'si yeniden hesaplanır (bozulma tespiti) |
| Toplam kullanım | `SUM(content_blob.stored_size)` |

## 4. Geri yükleme (WorkingTreeRestorer)

```text
RestoreAsync(snapshot, options)
 0. SafeOperationRunner önce "Before restore" noktası alır (restore da geri alınabilir)
 1. current_index  ← git ls-files -s -z
 2. snap_index     ← git ls-tree -r -z <index_tree>
 3. git read-tree --reset <index_tree>              (eşleşen girdilerde stat bilgisi korunur)
 4. hedef dosya kümesi = snap_index ∪ overlay
      overlay girdisi      → store'dan yaz (içerik aynıysa dokunma) / sil
      yalnızca snap_index  → git status'ta farklı görünüyorsa git checkout-index -f --stdin
 5. current_index'te olup hedefte olmayan dosyalar → sil (before-restore noktası bunları içerir)
 6. Snapshot'tan sonra oluşmuş untracked dosyalar → varsayılan olarak korunur
      (options.RemoveFilesCreatedAfterSnapshot = true ise silinir)
 7. boşalan klasörleri temizle
 8. git update-index --refresh ile stat bilgisi yenilenir; hatalar RestoreReport.Failures ile bildirilir
```

Tek dosya geri yükleme ("Restore this file") yalnızca 4. adımı ilgili yol için çalıştırır ve index'e dokunmaz.

## 5. Code Time Machine

- **Zamanlayıcı:** `RepositoryWatcher` (FileSystemWatcher, 400 ms debounce) repository'yi kirli işaretler.
  `TimeMachineService` her `Interval` (varsayılan 5 dk; 1/5/15/30 seçilebilir) kirli repository'lerde
  `trigger=timer` snapshot alır. Repository açıldıktan birkaç saniye sonra ve uygulama kapanırken
  (`trigger=shutdown`) de bir snapshot denenir. "Save snapshot now" ile elle alınabilir (`trigger=manual`).
- **Dosya zaman makinesi:** `GetFileAtAsync(path, time)` → `created_at <= time` olan en yeni snapshot,
  ardından Bölüm 1'deki çözümleme kuralı.
- **Dosya zaman çizelgesi:** `snapshot_entry.path` indeksi + index ağaçlarındaki blob id değişimleri →
  yalnızca dosyanın **gerçekten değiştiği** anlar listelenir.
- **Snapshot karşılaştırma:** iki snapshot'ın etkin dosya haritaları (`ls-tree` + overlay) blob id üzerinden
  karşılaştırılır; değişen dosyalar için metin diff'i bellekte üretilir.
  `Current ↔ 10:15 ↔ 09:45` zinciri UI'da ardışık karşılaştırmalar olarak gösterilir.

## 6. Retention

```text
Keep snapshots:  ○ 1 day  ○ 7 days  ● 30 days  ○ 90 days  ○ Until disk limit
Maximum storage: 5 GB
```

`RetentionService` periyodik çalışır (başlangıçta + saatte bir):

1. **Korunanlar asla silinmez:** kullanıcının sabitlediği recovery point'ler ve son 24 saatteki
   `before-*` noktaları.
2. Süresi dolan `timer` snapshot'ları silinir. "Until disk limit" seçiliyse süre sınırı uygulanmaz.
3. Toplam kullanım limitin üzerindeyse: önce hiçbir recovery point'in kullanmadığı en eski snapshot'lar,
   sonra 24 saatten eski ve sabitlenmemiş en eski recovery point'ler silinir (50'lik partiler hâlinde, limit altına inene kadar).
   Seyreltme (son 7 günde saatlik, daha eskide günlük) Phase 2'de eklenecek.
4. Mark & sweep: hiçbir `snapshot_entry` tarafından referans verilmeyen `content_blob`'lar diskten silinir.
   Yarıda kesilmiş yazımlardan kalan, veritabanında kaydı olmayan ve 1 saatten eski store dosyaları da temizlenir.
5. Referansı kalmayan `git_pin`'ler için `refs/archrono/pins/<sha>` ref'i silinir.

Uygulama `git gc` çalıştırmaz; pin ref'i kaldırılan nesneler Git'in normal gc politikasına bırakılır.

## 7. Güvenlik notları

- Snapshot'lar yalnızca yereldir; AI veya herhangi bir ağ işlemine girdi olmaz.
- Ignore edilmiş dosyalar (`.env` vb.) snapshot'a alınmaz.
- "Delete all ArChrono data for this repository": SQLite kayıtları + pin ref'leri + referanssız blob'lar.
