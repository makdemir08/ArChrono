# ADR-0001 — Masaüstü teknoloji seçimi

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-16

## Bağlam

Windows 10/11 ve macOS hedefleniyor, Linux engellenmemeli. Tek kod tabanı tercih ediliyor.
Uygulamanın ağırlık merkezi UI değil; **çok sayıda `git` süreci çalıştırmak, dosya sistemini izlemek,
içerik hash'lemek/sıkıştırmak, SQLite'a yazmak ve büyük listeleri (commit grafiği, diff) akıcı çizmek**.
Fork seviyesinde hız bir kabul kriteri.

## Değerlendirilen alternatifler

Puanlama: ●●● güçlü · ●● yeterli · ● zayıf

| Kriter | .NET 10 + Avalonia 12 | Tauri 2 (Rust + Web UI) | Electron | Flutter Desktop |
|---|---|---|---|---|
| Başlangıç süresi / bellek | ●●● native Skia, ~100 MB | ●●● küçük binary, WebView belleği ayrı | ● Chromium + Node, 200 MB+ | ●●● |
| Büyük liste/diff çizimi | ●●● sanallaştırma + özel çizim | ●● DOM sanallaştırma, IPC serileştirme maliyeti | ●● aynı | ●●● |
| Git süreç yönetimi | ●●● `System.Diagnostics.Process`, async stream, iptal | ●●● `std::process` / tokio | ●● `child_process`, dugite | ●● `dart:io Process` |
| Dosya sistemi izleme | ●●● `FileSystemWatcher` (macOS'ta FSEvents, Windows'ta ReadDirectoryChangesW) | ●●● `notify` crate | ●● chokidar / @parcel/watcher (native modül) | ● platform eklentileri |
| Native entegrasyon (keychain, credential manager) | ●●● doğrudan P/Invoke | ●●● Rust FFI / keyring crate | ●● native modül derlemesi gerekir | ● platform channel/eklenti |
| Tray / menü | ●●● Avalonia `TrayIcon`, `NativeMenu` | ●●● | ●●● | ● eklenti |
| Gömülü terminal | ● hazır kontrol yok (ConPTY/forkpty + VT çizici gerekir) | ●●● xterm.js + portable-pty | ●●● xterm.js + node-pty | ● |
| SQLite | ●●● Microsoft.Data.Sqlite | ●●● rusqlite | ●● better-sqlite3 (native modül) | ●● sqflite_common_ffi |
| Tek dil | ●●● C# | ● Rust + TypeScript | ●● TypeScript | ●●● Dart |
| Dağıtım (dmg, MSIX/NSIS, imzalama) | ●●● ekipte hazır pipeline (ArSnap) | ●●● | ●●● | ●● |
| Linux | ●● X11 (Wayland XWayland ile) | ●● webkit2gtk farkları | ●●● | ●● |
| Görsel tutarlılık (platformlar arası) | ●●● tek render motoru | ● WebView2 vs WKWebView vs WebKitGTK | ●●● | ●●● |
| Ekip deneyimi | ●●● ArSnap (.NET 10 + Avalonia 12) | ● Rust toolchain yok | ●● | ●● ArZip (Flutter) |

### Ek değerlendirme: React Native (react-native-windows + react-native-macos)

"Tüm platformları destekler" argümanı mobil için doğrudur; masaüstü için durum farklıdır:

| Kriter | Değerlendirme |
|---|---|
| Masaüstü platformları | Windows ve macOS, Microsoft'un **ayrı ayrı** sürdürdüğü out-of-tree platformlardır; **Linux desteği yoktur**. |
| Git süreç yönetimi | JS runtime'ında (Hermes) `child_process` yoktur. Her `git` çağrısı, dosya izleme, hash/sıkıştırma ve keychain için **platform başına native modül** gerekir: Windows'ta C++/WinRT veya C#, macOS'ta Swift/Obj-C. Ürünün çekirdeği iki ayrı native backend'e bölünür. |
| Dil sayısı | TypeScript + C++/C# + Swift → "tek codebase" hedefinin tersi. |
| Masaüstü UX | Çoklu pencere, native menü, tray, klavye odaklı kullanım ve binlerce satırlık özel çizimli commit grafiği/diff görünümleri mobil öncelikli bileşen modelinde zayıf kalır. |
| Mobil kazanç | Tam bir Git istemcisi (snapshot engine, git süreçleri, worktree, terminal) mobilde anlamlı bir hedef değildir. |

Sonuç: React Native, bu ürünün ağırlık merkezi olan **süreç + dosya sistemi + native güvenlik** işlerini
JS katmanının dışına ve iki ayrı native koda iteceği için elendi. İleride mobil bir "Recovery companion"
(ör. bildirim/salt okunur görünüm) istenirse, çekirdek servisler .NET'te kaldığı için ayrı bir istemci olarak eklenebilir.

## Karar

**.NET 10 (C#) + Avalonia 12** seçildi.

Gerekçeler:

1. **Tek dil, tek runtime:** Git adapter, snapshot engine, SQLite, AI HTTP istemcileri ve UI aynı dilde;
   katmanlar arası serileştirme/IPC yok. Ürünün kritik yolu (snapshot → git → SQLite) tek süreçte ve
   doğrudan test edilebilir.
2. **Performans:** Commit grafiği ve diff görünümleri sanallaştırılmış listelerle ve özel `Render`
   çizimiyle Skia üzerinden çizilir; WebView/DOM maliyeti yoktur.
3. **Native güvenlik entegrasyonu:** macOS Keychain (`Security.framework` `SecItem*`) ve Windows
   Credential Manager (`CredWriteW/CredReadW`) ek bağımlılık olmadan P/Invoke ile kullanılır (bkz. ADR-0007).
4. **Ekip altyapısı:** ArSnap'in paketleme, imzalama ve kurulum script'leri doğrudan yeniden kullanılabilir.
5. **Linux yolu açık:** Avalonia X11 desteği mevcut; platforma özel kod `ArChrono.Platform` içinde izole.

## Kabul edilen dezavantaj ve azaltma

- **Gömülü terminal:** Avalonia'da hazır terminal kontrolü yoktur. MVP'de:
  - Uygulamanın çalıştırdığı her Git komutunu gösteren ve serbest `git` komutu çalıştırılabilen
    **Git Console** paneli (öğreticidir ve profesyonellere şeffaflık sağlar),
  - Tek tıkla/kısayolla **sistem terminalini repository veya worktree dizininde açma**
    (macOS Terminal/iTerm, Windows Terminal/PowerShell).
  - Phase 2'de agent session'ları için ConPTY (Windows) ve `forkpty` (macOS/Linux) üzerine
    VT100 uyumlu gömülü terminal (bkz. ADR-0008).
- **Git dağıtımı:** Uygulama sistemdeki `git`'i kullanır; bulunamazsa kurulum rehberi gösterir.
  İleride GitHub Desktop'taki gibi gömülü MinGit/portable git değerlendirilebilir.

## Sonuçlar

- UI: Avalonia XAML + MVVM (`CommunityToolkit.Mvvm` source generator). DI container kullanılmaz;
  tek bir composition root (`AppServices`) bağımlılıkları açıkça kurar.
- Harici bağımlılıklar bilinçli olarak sınırlı tutulur:
  `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Fonts.Inter`,
  `CommunityToolkit.Mvvm`, `Microsoft.Data.Sqlite`. Test: `xunit.v3`.
