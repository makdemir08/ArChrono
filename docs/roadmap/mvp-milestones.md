# Yol Haritası ve MVP Milestone'ları

**MVP satış cümlesi:** *"Git'te yaptığınız hiçbir şeyi kaybetmek zorunda değilsiniz."*

**Başarı ölçütü:** "Bir geliştirici Git'te hata yaptığında uygulama onu ne kadar kolay kurtarıyor?"
Her milestone'un kabul kriteri çalışan kod + otomatik testtir.

## MVP

| # | Milestone | Kapsam | Kabul kriteri | Durum |
|---|---|---|---|---|
| M0 | Temel | Solution, ortak build ayarları, dokümanlar (ADR, mimari) | `dotnet build` + boş test koşusu geçer | ✅ |
| M1 | Git adapter | `IGitRunner`, `GitLocator`, hata çevirisi, parser'lar (status, log, refs, diff, reflog, stash, blame, merge-tree), servisler, graph layout | Geçici repository'lerde servis testleri | ✅ 42 test |
| M2 | Storage | SQLite migration'ları, store sınıfları | Şema oluşturma + CRUD testleri | ✅ |
| M3 | Snapshot engine | Content store, capture, restore, dosya-zaman sorgusu, snapshot diff | Değiştir → snapshot → boz → restore testleri | ✅ |
| M4 | Recovery & Safe Git | Recovery point, `SafeOperationRunner`, yerleşik işlemler, `RestorePlanner/Executor`, Undo | recovery-model.md §7'deki 10 senaryo | ✅ 10/10 + rename, interactive rebase, tag, restore |
| M5 | Application katmanı | `RepositorySession`, watcher, `GitActions`, undo/redo, `TimeMachineService`, retention, search, settings | Headless uçtan uca test: "hata → undo" | ✅ |
| M6 | AI temel | Provider soyutlaması (OpenAI, Anthropic, Ollama, Azure, compatible), gizlilik filtresi, commit mesajı, commit açıklaması | Sahte HTTP handler ile istek/yanıt testleri; `.env` asla payload'da değil | ✅ (+ branch ve conflict açıklaması) |
| M7 | UI kabuğu | Home (repository manager), Workspace (branch'ler, grafik, working tree, detaylar), Changes composer, diff viewer, Command Palette, toast/confirm/error overlay'leri | Uygulama açılır, repo açılır, commit atılır | ✅ headless render + masaüstü duman testi |
| M8 | Recovery UI | Recovery Center (zaman çizelgesi, reflog, restore planı), Chrono Strip, Undo düğmesi, Time Machine görünümü, dosya geçmişi | Elle kabul: reset --hard → Undo | ✅ |
| M9 | Dağıtım | macOS (.app/.dmg, Apple Silicon + Intel), Windows (x64/ARM64 setup + portable) | Kurulum dosyaları üretilir | 🟡 macOS .app/.dmg script'i doğrulandı (ad-hoc imza); Windows publish script'i yazıldı, Windows'ta henüz doğrulanmadı; NSIS/MSIX kurulum yok |

### Bilinen sınırlar (MVP)

- Aynı anda tek repository açık tutulur; Time Machine yalnızca açık repository için çalışır.
- Windows yolları (Credential Manager, terminal, yol karşılaştırmaları) derleniyor ancak gerçek bir Windows makinesinde test edilmedi.
- macOS Keychain entegrasyonu kullanıcının anahtar zincirine yazdığı için otomatik testlerde çalıştırılmıyor.
- Hunk düzeyinde stage var; satır düzeyinde stage yok.
- Merge commit'leri içeren aralıklarda görsel interactive rebase devre dışı.
- Tema değiştirildiğinde bazı dönüştürücü tabanlı renkler (dosya değişiklik harfleri, toast vurgusu) bir sonraki yenilemede güncellenir.
- Arayüz İngilizce ve Türkçe. Dil değiştirilmeden önce kaydedilmiş işlem/kurtarma noktası başlıkları o dilde görünmeye devam eder.

MVP özellik listesi → milestone eşlemesi:

| MVP maddesi | Milestone |
|---|---|
| 1 Repository manager | M5, M7 |
| 2 Git status | M1, M7 |
| 3 Commit | M1, M4, M7 |
| 4 Branch | M1, M4, M7 |
| 5 Merge | M1, M4, M7 |
| 6 Rebase | M1, M4, M7 |
| 7 Visual commit graph | M1 (layout), M7 |
| 8 Diff viewer | M1, M7 |
| 9 Stash | M1, M4, M7 |
| 10 Reflog | M1, M8 |
| 11 Recovery Point | M4, M8 |
| 12 Automatic pre-operation snapshot | M3, M4 |
| 13 Undo | M4, M5, M8 |
| 14 File History | M1, M8 |
| 15 Command Palette | M7 |
| 16 Basic AI commit explanation | M6, M7 |
| 17 Basic AI commit message | M6, M7 |
| 18 Windows + macOS | M9 |

## Phase 2

- Code Time Machine'in tam deneyimi (dosya zaman çizelgesi, snapshot zinciri karşılaştırma, agent öncesi dönüş)
- Semantic history ("What changed?" yapılandırılmış açıklama)
- "Why did this change?" (blame → commit → related commits → PR)
- Smart conflict detection (merge/rebase öncesi `merge-tree` + semantik sınıflandırma)
- AI conflict explanation, 3-way Smart Merge (Base / Current / Incoming / Result)
- Visual interactive rebase (sürükle-bırak) · Visual bisect
- Worktrees + Agent sessions + gömülü PTY terminal
- Branch intelligence (ahead/behind, potential conflicts, stale branch)
- GitHub / GitLab / Azure DevOps entegrasyonu (token'lar OS keychain'de)

## Phase 3

- Lokal semantic code search (embedding)
- Advanced AI merge · Automatic regression detection
- Repository health · Branch cleanup assistant · Commit quality analysis
- Release preparation · Changelog · PR preparation
- Local AI models via Ollama (tam özellik seti) · Team collaboration
