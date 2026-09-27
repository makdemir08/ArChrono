# ADR-0003 — Snapshot depolama: uygulamaya ait content store + Git nesne yeniden kullanımı

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-16
- **Detaylı tasarım:** [architecture/snapshot-engine.md](../architecture/snapshot-engine.md)

## Bağlam

Time Machine ve recovery point'ler çalışma alanının (index + working tree + untracked dosyalar)
geçmişini Git commit'lerinden bağımsız tutmalı. Tam repository kopyası kabul edilemez;
depolama sınırı ("Maximum storage: 5 GB") ve saklama süresi ayarlanabilir olmalı.

## Değerlendirilen seçenekler

### A) Her şeyi Git nesne veritabanına yazmak
Geçici index + `git add -A` + `write-tree` + `commit-tree` + `refs/archrono/...`.

- ➕ Git'in sıkıştırma, delta ve dedup altyapısı; restore çok basit.
- ➖ Kullanıcının `.git` klasörü büyür; büyük binary'ler Git nesnesi olarak kalır.
- ➖ **Disk limiti ölçülemez ve geri kazanılamaz:** Yer açmak için `git gc --prune=now` gerekir; bu komut
  kullanıcının kendi kayıp commit'lerini de siler. Yani kurtarma aracı, kurtarma malzemesini yok eder.

### B) Tam kopya (rsync benzeri)
- ➖ Depolama maliyeti kabul edilemez.

### C) Uygulamaya ait content-addressed store + Git'ten değişmeyen içeriği yeniden kullanmak ✅

## Karar

**Seçenek C.** Snapshot iki parçadan oluşur:

1. **Git'in zaten sahip olduğu durum** — `HEAD` commit'i ve index ağacı. Index `GIT_INDEX_FILE` ile
   kopyalanmış geçici bir index üzerinden `git write-tree` ile ağaca dönüştürülür ve **tek bir pin commit'i**
   (`commit-tree <index-tree> -p HEAD`) ile `refs/archrono/pins/<sha>` altında gc'ye karşı korunur.
   Kullanıcının gerçek index dosyasına hiç dokunulmaz.
2. **Git'in sahip olmadığı durum (overlay)** — index'ten farklı çalışma alanı dosyaları, silinmiş dosyalar ve
   ignore edilmemiş untracked dosyalar. İçerikleri uygulamanın **content store**'una yazılır.

### Content store

- Anahtar: **Git uyumlu blob id** (`sha1("blob <len>\0" + içerik)`, repo SHA-256 kullanıyorsa `sha256`).
  Böylece overlay dosyaları ile `git ls-tree` çıktısındaki blob'lar doğrudan karşılaştırılabilir ve
  snapshot diff'i Git ağaçlarıyla aynı kimlik uzayında çalışır.
- Konum: `<AppData>/ArChrono/store/objects/<2 hex>/<kalan hex>` — tüm repository'ler arasında
  paylaşılır (aynı repo'nun worktree'leri doğal olarak dedup edilir).
- Sıkıştırma: Brotli (zaten sıkıştırılmış uzantılar ve küçük kazançlı dosyalar ham saklanır).
- Yazma atomik: geçici dosya → `File.Move`.
- Referans sayımı SQLite'tan (mark & sweep): hiçbir snapshot'ın referans vermediği blob silinir.

### Artımlılık ve değişiklik takibi

- Değişiklik tespiti Git'in stat cache'i ile yapılır: `git status --porcelain=v2 -z --untracked-files=all`
  (`GIT_OPTIONAL_LOCKS=0`). Yalnızca değişmiş dosyalar okunur ve hash'lenir.
- Dosya içeriği zaten store'da varsa tekrar yazılmaz (dedup).
- Önceki snapshot ile aynı parmak izine (HEAD + index ağacı + overlay) sahip snapshot oluşturulmaz.
- `FileSystemWatcher` repository'yi "kirli" olarak işaretler; zamanlayıcı yalnızca kirli repository'lerde çalışır.

### Neden Git ref'i yazıyoruz?

`HEAD` ve index blob'ları reflog süresi dolduğunda ya da branch silindiğinde gc tarafından temizlenebilir.
Pin ref'leri bunu engeller. Bu, Jujutsu (`refs/jj/keep`) ve GitButler'ın kullandığı yerleşik bir tekniktir.

- Pin'ler `refs/archrono/pins/<sha>` altında; `git push --all/--tags` ile gönderilmez, fetch refspec'ine girmez.
- Aynı (HEAD, index ağacı) çifti için tekrar pin oluşturulmaz → ref sayısı commit/stage sıklığıyla sınırlı.
- Retention bir pin'e referans kalmadığında ref'i siler. Uygulama **asla `git gc` çalıştırmaz.**
- Ayarlardan kapatılabilir ("Protect recovery data with hidden refs") ve
  "Remove ArChrono data from this repository" komutu tüm `refs/archrono/*` ref'lerini temizler.

## Sınırlar (bilinçli)

- `.gitignore` kapsamındaki dosyalar (ör. `.env`, `node_modules`) snapshot'a **dahil edilmez**.
  Bu hem depolama hem güvenlik açısından doğru varsayılandır; `git clean -x` UI'da sunulmaz.
- Varsayılan dosya boyutu sınırı 25 MB; aşan dosyalar "skipped" olarak kaydedilir ve UI'da gösterilir.
- Submodule içerikleri snapshot'a dahil edilmez (submodule commit pointer'ı index ağacında zaten vardır).
- Clean/smudge filtreleri (autocrlf, LFS) nedeniyle overlay blob id'si Git'in hesapladığı id'den farklı olabilir;
  bu yalnızca snapshot diff'inde yanlış pozitif "değişti" üretebilir, geri yükleme doğruluğunu etkilemez.

## Sonuçlar

- `Maximum storage` ölçümü `content_blob.stored_size` toplamıdır ve kesindir.
- Silme kararları kullanıcının Git nesne veritabanını etkilemez.
- Restore, Git plumbing (`read-tree --reset`, `checkout-index`) + store'dan dosya yazma ile yapılır;
  restore'dan önce her zaman "Before restore" snapshot'ı alınır (restore da geri alınabilir).
