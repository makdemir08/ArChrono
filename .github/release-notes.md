**Git without fear.** Her riskli Git işleminden önce otomatik kurtarma noktası, tek tıkla geri alma ve çalışma alanınız için bir zaman makinesi. Arayüz Türkçe ve İngilizce.

## Hangi dosyayı indirmeliyim?

| Sistem | Dosya |
|---|---|
| Mac — Apple Silicon (M1, M2, M3, M4…) | `ArChrono-…-macOS-AppleSilicon.dmg` |
| Mac — Intel | `ArChrono-…-macOS-Intel.dmg` |
| Windows 10/11 — çoğu bilgisayar | `ArChrono-…-Windows-x64-Setup.exe` |
| Windows 11 — ARM (Snapdragon vb.) | `ArChrono-…-Windows-ARM64-Setup.exe` |
| Windows, kurulum olmadan | `…-Portable.zip` (içindeki `ArChrono.exe`'yi çalıştırın) |

**Gereksinim:** bilgisayarınızda Git 2.38 veya üzeri. Windows'ta [Git for Windows](https://git-scm.com/download/win); Mac'te Terminal'de `xcode-select --install` veya Homebrew ile `git`.

## Kurulum

**Mac:** DMG'yi açın, ArChrono'yu Uygulamalar klasörüne sürükleyin. Uygulama henüz Apple tarafından onaylanmış (notarize) değil; ilk açılışta macOS engellerse
**Sistem Ayarları → Gizlilik ve Güvenlik → "Yine de Aç"** deyin ya da Terminal'de:

```
xattr -dr com.apple.quarantine /Applications/ArChrono.app
```

**Windows:** `Setup.exe` yönetici izni istemeden kullanıcı klasörünüze kurar. Kurulum dosyası imzasız olduğu için SmartScreen uyarı verebilir: **Ek bilgi → Yine de çalıştır**.

Dosyaların doğruluğunu `SHA256SUMS.txt` ile kontrol edebilirsiniz.

---

*English:* Pick the DMG for your Mac (Apple Silicon or Intel) or the Windows Setup for x64 / ARM64. Requires Git 2.38+. Builds are not code-signed yet — on macOS use System Settings → Privacy & Security → "Open Anyway"; on Windows choose "More info → Run anyway" in SmartScreen.
