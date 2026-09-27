# Mimari Genel Bakış

## Katmanlar

```text
┌──────────────────────────────────────────────────────────────────────────┐
│ ArChrono.App  (Avalonia UI, MVVM)                                        │
│  Views · ViewModels · Controls (CommitGraph, DiffView, ChronoStrip)      │
│  CommandRegistry · DialogService · ToastService                         │
└───────────────┬──────────────────────────────────────────────────────────┘
                │ yalnızca Application servislerini çağırır
┌───────────────▼──────────────────────────────────────────────────────────┐
│ ArChrono.Application                                                     │
│  AppServices (composition root) · RepositorySession · GitActions        │
│  UndoService · TimeMachineService · SearchService · SettingsService     │
└──────┬───────────────────┬────────────────────┬──────────────────┬──────┘
       │                   │                    │                  │
┌──────▼───────┐  ┌────────▼────────┐  ┌────────▼───────┐  ┌───────▼────────┐
│ ArChrono.    │  │ ArChrono.       │  │ ArChrono.AI    │  │ ArChrono.      │
│ Recovery     │  │ Storage         │  │ providers,     │  │ Platform       │
│ SafeOperation│  │ SQLite, stores, │  │ context, privacy│ │ AppPaths,      │
│ Runner,      │  │ migrations      │  │                │  │ keychain,      │
│ Snapshot     │  │                 │  │                │  │ terminal       │
│ Engine,      │  └─────────────────┘  └───────┬────────┘  └────────────────┘
│ ContentStore,│                               │
│ RestorePlan  │                               │
└──────┬───────┘                               │
       │                                       │
┌──────▼───────────────────────────────────────▼───────────────────────────┐
│ ArChrono.Git   Git services → IGitRunner → git executable                │
└──────────────────────────────────────────────────────────────────────────┘
```

### Bağımlılık kuralları

| Proje | Bağımlı olabileceği projeler |
|---|---|
| `ArChrono.Localization` | — (yalnızca BCL; arayüz dili seçimi `Loc.T(en, tr)`) |
| `ArChrono.Git` | Localization |
| `ArChrono.Storage` | — (+ `Microsoft.Data.Sqlite`) |
| `ArChrono.Platform` | Localization |
| `ArChrono.Recovery` | Git, Storage |
| `ArChrono.AI` | Git |
| `ArChrono.Application` | Git, Storage, Recovery, AI, Platform |
| `ArChrono.App` | Application (+ alt katmanların model tipleri) |

- UI **hiçbir zaman** `IGitRunner` veya `git` sürecine doğrudan erişmez.
- Durum değiştiren her Git işlemi `SafeOperationRunner`'dan geçer (ADR-0004).
- Katmanlar arası iletişim async; UI thread'i Git çağrısı beklemez.

## Klasör yapısı

```text
ArChrono/
├── ArChrono.slnx
├── Directory.Build.props          ortak derleme ayarları (.NET 10, nullable, analyzers)
├── Directory.Packages.props       merkezi paket sürümleri
├── README.md
├── docs/
│   ├── adr/                       Architecture Decision Records
│   ├── architecture/              katman tasarımları (bu klasör)
│   ├── design/                    rakip analizi, UI navigasyonu
│   ├── roadmap/                   MVP milestone'ları, Phase 2/3
│   └── screenshots/               tools/ArChrono.Screenshots çıktısı
├── scripts/                       build/test/run yardımcıları
├── src/
│   ├── ArChrono.Localization/     Loc: İngilizce/Türkçe metin seçimi, gösterim kültürü
│   ├── ArChrono.Git/
│   │   ├── Process/               IGitRunner, GitProcessRunner, GitLocator, GitCommand
│   │   ├── Errors/                GitError, GitErrorCode, GitErrorTranslator, GitException
│   │   ├── Models/                durum, commit, ref, diff, stash, reflog, blame, worktree modelleri
│   │   ├── Parsing/               porcelain/format çıktı ayrıştırıcıları (saf, test edilebilir)
│   │   ├── Graph/                 CommitGraphLayout (lane yerleşimi)
│   │   ├── Services/              alan başına servisler (Status, History, Refs, Staging, …)
│   │   ├── GitRepository.cs       servisleri bir araya getiren facade
│   │   └── GitClient.cs           clone/init/version (repository dışı işlemler)
│   ├── ArChrono.Storage/
│   │   ├── Migrations/            0001_initial.sql … (embedded resource)
│   │   ├── Records/               tablo kayıtları
│   │   └── Stores/                tablo başına küçük veri erişim sınıfları
│   ├── ArChrono.Recovery/
│   │   ├── Content/               ContentStore, BlobHasher
│   │   ├── Snapshots/             SnapshotEngine, WorkingTreeRestorer, SnapshotReader
│   │   ├── Points/                RecoveryPointService
│   │   ├── Restore/               RestorePlan, RestorePlanner, RestoreExecutor
│   │   ├── Operations/            IGitOperation, SafeOperationRunner, yerleşik işlemler
│   │   ├── Retention/             RetentionService
│   │   └── Timeline/              RecoveryTimelineService (points + reflog + lost commits)
│   ├── ArChrono.AI/
│   │   ├── Providers/             OpenAI, Anthropic, Ollama, Azure OpenAI, OpenAI-compatible
│   │   ├── Privacy/               SensitivePathFilter, SecretRedactor
│   │   ├── Context/               AiContextBuilder, AiPayload
│   │   └── AiService.cs
│   ├── ArChrono.Platform/
│   │   ├── AppPaths.cs
│   │   ├── SystemLanguage.cs      işletim sisteminin tercih edilen arayüz dili
│   │   ├── Credentials/           macOS Keychain, Windows Credential Manager
│   │   └── Shell/                 TerminalLauncher, FileRevealer
│   ├── ArChrono.Application/
│   │   ├── AppServices.cs         composition root + GitConsoleLog + retention zamanlayıcısı
│   │   ├── Repositories/          RepositorySession (undo/redo, olaylar), GitActions, RepositoryWatcher
│   │   ├── TimeMachine/           TimeMachineService
│   │   ├── Search/                SearchService, ISearchProvider, FuzzyMatcher
│   │   ├── Ai/                    AiAssistant (hazırla → önizle → gönder → denetim kaydı)
│   │   └── Settings/              AppSettings, SettingsService
│   └── ArChrono.App/
│       ├── Theme/                 Colors.axaml (token'lar), Styles.axaml, Icons.axaml
│       ├── Assets/                ArSoftLogo.png (Hakkında)
│       ├── Controls/              IconView, CommitGraphCell, ChronoStrip, DayNightToggleButton
│       ├── Infrastructure/        ViewModelBase, ViewLocator, converter'lar, biçimlendirme
│       ├── Localization/          {l:Loc 'en', 'tr'} markup extension, ayar seçeneği etiketleri
│       ├── Services/              AppIcon
│       ├── ViewModels/            Shell (diyalog/toast), Home, Repository, Workspace, Details,
│       │                          RecoveryAndTimeMachine, PaletteConsole, Dialogs/
│       └── Views/                 ekranlar + Dialogs/, ShellToolbar (tema · dil · ayarlar · hakkında), AboutWindow
├── tools/
│   └── ArChrono.Screenshots/      headless Skia render: ekran görüntüleri ve uygulama ikonu
├── installers/macos/              entitlements
└── tests/
    └── ArChrono.Tests/            geçici Git repository'leri üzerinde uçtan uca testler
```

## Çalışma zamanı veri konumları

| Veri | macOS | Windows |
|---|---|---|
| SQLite | `~/Library/Application Support/ArChrono/archrono.db` | `%LOCALAPPDATA%\ArChrono\archrono.db` |
| Content store | `~/Library/Application Support/ArChrono/store/objects/` | `%LOCALAPPDATA%\ArChrono\store\objects\` |
| Log | `~/Library/Logs/ArChrono/` | `%LOCALAPPDATA%\ArChrono\logs\` |
| Sırlar | Keychain (`dev.archrono.*`) | Credential Manager (`ArChrono:*`) |
| Repository içinde | yalnızca `refs/archrono/pins/*` | aynı |

Linux: `$XDG_DATA_HOME/archrono` (yoksa `~/.local/share/archrono`).
