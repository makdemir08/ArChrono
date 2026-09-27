# UI Navigasyonu ve Tasarım Dili

## 1. Prensipler

1. **Her tehlikeli işlem geri alınabilir — ve bu her an görünür.** Undo düğmesi üst çubukta kalıcıdır,
   son işlemin adını taşır ("↶ Undo reset --hard"). Chrono Strip pencerenin altında her zaman açıktır.
2. **Bilgi yoğun ama sakin.** Varsayılan görünüm tek bakışta: branch'ler, grafik, çalışma alanı, detay.
   Renk yalnızca anlam taşır (durum, risk, lane).
3. **Keyboard-first.** Her eylem Command Palette'ten ve kısayolla erişilebilir.
4. **İki dil, tek arayüz.** *Guided* modda doğal dil ve açıklamalar; *Pro* modda gerçek Git terminolojisi ve
   komut önizlemesi. Mod değiştirmek düzeni değiştirmez, yalnızca etiketleri ve açıklama yoğunluğunu değiştirir.
5. **Asla sessiz hata yok.** Hatalar insan diliyle; ham Git çıktısı "Technical details" altında.

## 2. Pencere düzeni

```text
┌─────────────────────────────────────────────────────────────────────────────────────────┐
│ ◷ ArChrono ▾  ⎇ feature/login ▾ ↑2 ↓1 │  🔍 Search or run a command…   ⌘K │ ⟳ ↓ ↑ │ ↶ Undo reset │ ✦ AI │ Guided|Pro │
├──┬──────────────────┬───────────────────────────────────────────────────────────────────┤
│▣ │ BRANCHES          │  COMMIT GRAPH                                                    │
│  │ ● main        ✓   │  ◌ Uncommitted changes · 12 files                                │
│⟲ │ ● feature/login   │  ●  Add refresh token support        feature/login   2h  Ada    │
│  │   ↑7 ↓2 · rebase  │  │●  Fix validation                                  3h  Ada    │
│⧗ │ ○ bugfix/182      │  ●┘ Merge branch 'develop'           main            1d  Can    │
│  │ REMOTES ▸         │  …                                                               │
│  │ TAGS ▸            ├───────────────────────────────────────────────────────────────────┤
│  │ STASHES (2)       │  DETAILS   [Changes] [Files] [✦ Explanation] [History]           │
│  ├──────────────────┤                                                                   │
│  │ WORKING TREE      │  diff / commit composer / AI açıklaması / dosya geçmişi          │
│⚙ │ 12 modified       │                                                                   │
│  │ 3 untracked       │                                                                   │
│  │ 2 staged          │                                                                   │
├──┴──────────────────┴───────────────────────────────────────────────────────────────────┤
│ CHRONO ─○──○───◆────○──○───◆──◆──────○──●now   Last snapshot 2 min ago · 1.8 / 5 GB      │
└─────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Üst çubuk sağı (Home ekranında da aynı):** `☾/☀` gece/gündüz modu · `🌐 TR/EN` dil menüsü (English / Türkçe / Sistemle aynı) · `⚙` Ayarlar · `ⓘ` Hakkında
  (ArSoft logosu, geliştirici, www.arsoft.com.tr — ArSnap ile aynı pencere; macOS'ta uygulama menüsünde de "ArChrono Hakkında").
- **Sol ray (nav rail):** `▣ Workspace` · `⟲ Recovery` · `⧗ Time Machine` · (`⌥ Worktrees` Phase 2) · alt: `>_ Console`, `⚙ Settings`
- **Chrono Strip:** bugünün zaman ekseni. `◆` amber = recovery point, `○` teal = snapshot, `✕` kırmızı = başarısız işlem.
  Üzerine gelince başlık; tıklayınca Recovery Center o noktaya odaklanır.
- **Working Tree kutusu** veya grafikteki "Uncommitted changes" satırı seçildiğinde Details alanı
  **Changes composer**'a dönüşür (unstaged/staged listeleri, diff, commit mesajı, ✦ Generate).

## 3. Görünümler (destinations)

| Görünüm | Kısayol | İçerik |
|---|---|---|
| **Home / Repositories** | `⌘⇧O` | Son kullanılanlar, favoriler, Open / Clone / Init, git kurulum durumu |
| **Workspace** | `⌘1` | Branch'ler, commit grafiği, çalışma alanı, detaylar |
| **Recovery Center** | `⌘2` | Zaman çizelgesi (Today/Yesterday…), nokta detayı, Restore planı, Reflog sekmesi, Deep scan |
| **Time Machine** | `⌘3` | Snapshot zaman ekseni, dosya ağacı, dosya içeriği, snapshot ↔ snapshot/current diff, dosya geri yükleme |
| **Settings** | `⌘,` | General (mode, theme, language), Time Machine & Storage, AI & Privacy, Git |

## 4. Overlay'ler (aynı pencere içinde, modal katman)

| Overlay | Açılış | Not |
|---|---|---|
| Command Palette | `⌘K`, `⌘⇧P` | Önekler: `>` komut, `@` branch, `#` commit, `/` dosya, `~` snapshot/recovery |
| Guided actions ("What do you want to do?") | Palette boşken, Guided modda Details boşken | Save my current work · Create a branch · Combine these changes · Undo last operation · Find when this bug was introduced · See what changed · Restore an old version |
| Safe operation confirm | Destructive işlem | İşlemin etkisi + otomatik recovery point zamanı |
| Operation toast | İşlem sonrası | `[Undo]` `[View Recovery Point]`, 8 sn |
| Restore plan | Undo/Restore | Adım listesi (checkbox), uyarılar, Pro modda git komutları |
| Merge preview | Merge/Rebase başlatma | Conflict tahmini (`merge-tree`), etkilenen dosyalar |
| Conflict resolver | Repo `merging/rebasing` durumunda banner | Dosya listesi, Base / Current / Incoming / Result |
| Interactive rebase | Commit'lerde "Edit history…" | Sürükle-bırak liste, pick/reword/edit/squash/fixup/drop |
| AI payload preview | AI isteği öncesi | Sağlayıcı, host, dosyalar, hariç tutulanlar, maskelenen satırlar, tam metin |
| Error | Başarısız işlem | Başlık, açıklama, öneri eylemi, Technical details |

## 5. Guided ↔ Pro etiketleri

| Pro | Guided |
|---|---|
| Commit | Save my current work |
| Stash | Put my changes aside |
| Merge | Combine these changes |
| Rebase | Move my commits on top of… |
| Reset --hard | Throw away changes and go back to… |
| Cherry-pick | Copy this commit here |
| Revert | Create a commit that undoes this |
| Checkout | Switch to |
| Reflog | Everything HEAD has pointed to |
| Detached HEAD | Viewing an old version (not on a branch) |
| ↑7 ↓2 | 7 commits not in main yet · 2 new commits in main |

## 6. Klavye haritası

| Kısayol | Eylem |
|---|---|
| `⌘K` / `⌘⇧P` | Command palette |
| `⌘Z` / `⌘⇧Z` | Son Git işlemini geri al / undo'yu geri al (metin kutusu dışında) |
| `⌘1` `⌘2` `⌘3` | Workspace / Recovery / Time Machine |
| `⌘R` | Yenile |
| `⌥⌘F` `⌥⌘L` `⌥⌘P` | Fetch / Pull / Push |
| `⌘B` | Branch oluştur |
| `⌘⇧S` | Değişiklikleri kenara koy (stash) |
| `⌘⏎` | Commit (composer'da) |
| `⌘E` | Seçili commit'i AI ile açıkla |
| `` ⌘` `` | Git Console |
| `⌘J` | Terminali bu dizinde aç |
| `⌘,` | Ayarlar |
| `↑` `↓` `Enter` | Liste gezinme / aç |
| `Space` | Seçili dosyayı stage/unstage |
| `Esc` | Overlay kapat |

Windows'ta `⌘` = `Ctrl`, `⌥` = `Alt`.

## 7. Görsel dil

- **Arayüz dili:** İngilizce ve Türkçe. Varsayılan "Sistemle aynı": macOS'ta tercih edilen dil listesi, diğer
  sistemlerde işletim sistemi kültürü; Türkçe değilse İngilizce. Metinler çağrı noktasında iki dilde yazılır
  (C#: `Loc.T("Undo", "Geri al")`, XAML: `{l:Loc 'Undo', 'Geri al'}`). Dil değişince kabuk görünümleri yeniden
  kurar (açık depo, bölüm, konsol ve commit mesajı taslağı korunur). Kayıtlı işlem ve kurtarma noktası başlıkları
  oluşturuldukları dilde kalır. Git terimleri (commit, branch, merge, rebase…) Türkçe arayüzde de korunur.
  Süreç kültürü en-US'e sabit kalır; tarih/sayı yalnızca gösterimde `Loc.Culture` ile biçimlenir.
- **Gece / gündüz:** üst çubuktaki düğme görünen temanın tersine geçer ve tercihi kaydeder; Ayarlar'da
  "Sistemle aynı" seçeneği de vardır.

- **Renk token'ları** (`Theme/Colors.axaml`), açık ve koyu tema:
  - `Accent.Safe` teal — geri alınabilir, başarılı, recovery güvencesi
  - `Accent.Time` amber — recovery point, snapshot, zaman
  - `Accent.Danger` kırmızı — destructive onay, başarısız işlem
  - `Accent.AI` mor — yalnızca AI üretimi içerik rozetleri
  - Lane paleti: 8 renk, hem açık hem koyu zeminde ≥ 3:1 kontrast
- **Tipografi:** UI için Inter; kod/diff/sha için sistem monospace (SF Mono / Cascadia Mono / Menlo / Consolas).
- **Yoğunluk:** 28 px liste satırı, 13 px gövde metni, 11 px meta. Grafik düğümü 8 px daire; merge commit içi boş.
- **Hareket:** yalnızca durum geçişlerinde 120–160 ms; grafik ve listeler animasyonsuz (hız algısı).
