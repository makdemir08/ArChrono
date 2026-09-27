# ADR-0002 — Git entegrasyonu: git CLI adapter

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-16

## Bağlam

Git işlemleri üç yolla yapılabilir:

| Yol | Artı | Eksi |
|---|---|---|
| **libgit2 (LibGit2Sharp)** | Süreç başlatma maliyeti yok, nesnelere doğrudan erişim | Interactive rebase, worktree'nin tamamı, LFS, hooks, sparse checkout, credential helper, `includeIf`, fsmonitor gibi davranışlarda Git ile birebir değil; native binary dağıtımı |
| **Saf managed implementasyon** | Tam kontrol | Git'i yeniden yazmak; kabul edilemez risk |
| **git executable (CLI)** | Kullanıcının terminalde gördüğü davranışın aynısı; hooks, config, credential helper, LFS, submodule, worktree eksiksiz | Süreç başlatma maliyeti (macOS ~5–15 ms, Windows ~20–40 ms), çıktı parse etme |

"Uygulama Git repository'sine zarar vermemeli" kriteri, Git ile **birebir aynı davranışı** zorunlu kılar.

## Karar

Tüm Git işlemleri, **tek giriş noktası olan `IGitRunner`** üzerinden sistemdeki `git` çalıştırılarak yapılır.
UI ve application katmanları `git`'i asla doğrudan çağırmaz.

```text
UI (ViewModels)
 │
Application Services      ArChrono.Application   (RepositorySession, GitActions, UndoService…)
 │
Safe Operation Runner     ArChrono.Recovery      (validate → recovery point → execute → verify → record)
 │
Git Services              ArChrono.Git           (Status, History, Refs, Staging, Merge, Rebase, Stash…)
 │
Git Adapter               ArChrono.Git.Process   (GitProcessRunner, GitLocator, parsers, GitErrorTranslator)
 │
git executable
```

### Adapter kuralları

1. **Argüman listesi, asla shell string'i:** `ProcessStartInfo.ArgumentList` kullanılır; shell injection yok.
2. **Makine tarafından okunabilir formatlar:** `status --porcelain=v2 -z`, `log --format` + `%x1f`/`%x1e`
   ayırıcılar, `for-each-ref --format`, `blame --porcelain`, `ls-tree -z`, `worktree list --porcelain`.
3. **Sabit ortam:** `LC_ALL=C` (hata eşleme için İngilizce çıktı), `GIT_TERMINAL_PROMPT=0`
   (GUI'de takılı kalan şifre istemi olmaz), `GIT_PAGER=cat`, `-c core.quotepath=false`, `-c color.ui=never`.
4. **Okuma işlemleri kilit almaz:** `GIT_OPTIONAL_LOCKS=0`. Arka planda çalışan status yenilemesi,
   kullanıcının terminalde çalıştırdığı komutla `index.lock` çakışması üretmez.
5. **Her komut olay yayınlar:** `CommandStarted/CommandCompleted` → Git Console paneli ve işlem kaydı.
6. **İptal ve zaman aşımı:** `CancellationToken` süreç ağacını sonlandırır.
7. **Hata çevirisi:** `GitErrorTranslator` stderr kalıplarını `GitErrorCode` + kullanıcı dostu başlık, açıklama ve
   öneriye çevirir. Ham çıktı her zaman "Technical details" altında erişilebilir kalır.
8. **Uygulamanın kendi ref'leri gizlenir:** Grafik sorguları `--all` yerine `--branches --remotes --tags HEAD`
   kullanır; böylece `refs/archrono/*` pin ref'leri kullanıcı grafiğini kirletmez (bkz. ADR-0003).

### Minimum Git sürümü

- **2.38+** zorunlu: `git merge-tree --write-tree` (çalışma alanına dokunmadan conflict tahmini).
- **2.40+** önerilir. Açılışta sürüm kontrol edilir, düşükse yönlendirici bir mesaj gösterilir.

### Performans ilkeleri

- Tek ekran için tek toplu komut: branch listesi + ahead/behind tek `for-each-ref` çağrısı.
- Sayfalı log (`--max-count` + `--skip`), grafik yerleşimi artımlı hesaplanır.
- FileSystemWatcher olayları debounce edilir (varsayılan 400 ms); `.git` içindeki gürültülü dosyalar filtrelenir.

## Sonuçlar

- Windows'ta Git for Windows gereklidir; bulunamazsa kurulum rehberi gösterilir. macOS'ta Xcode CLT
  kurulu değilse `/usr/bin/git` shim'i kullanılmaz (kurulum diyaloğu tetiklememek için).
- Kimlik doğrulama Git'in credential helper'larına (osxkeychain, Git Credential Manager) ve SSH agent'a
  bırakılır. Phase 2: uygulamaya ait `GIT_ASKPASS` yardımcı süreci + OS keychain.
- libgit2 kullanılmadığı için native Git kütüphanesi dağıtılmaz.
