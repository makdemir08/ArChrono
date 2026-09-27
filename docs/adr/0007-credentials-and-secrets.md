# ADR-0007 — Kimlik bilgileri ve sırlar

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-16

## Karar

| Sır | Nerede tutulur |
|---|---|
| AI API anahtarları | OS credential store |
| Hosting token'ları (GitHub/GitLab/Azure DevOps — Phase 2) | OS credential store |
| HTTPS Git kimlik bilgileri | Git credential helper (osxkeychain, Git Credential Manager) |
| SSH anahtarları | Kullanıcının `~/.ssh` dizini ve SSH agent — **uygulama okumaz, kopyalamaz, saklamaz** |

`ICredentialStore` uygulamaları:

- **macOS:** `Security.framework` `SecItemAdd/SecItemCopyMatching/SecItemUpdate/SecItemDelete`,
  `kSecClassGenericPassword`, servis adı `dev.archrono.<kapsam>`. Sır komut satırı argümanı olarak
  hiçbir sürece (`security` CLI) geçirilmez.
- **Windows:** `advapi32` `CredWriteW/CredReadW/CredDeleteW`, `CRED_TYPE_GENERIC`,
  `CRED_PERSIST_LOCAL_MACHINE`, hedef adı `ArChrono:<kapsam>`.
- **Linux (ileride):** Secret Service (libsecret). Uygun store yoksa sır **saklanmaz**; kullanıcıdan her
  oturumda istenir. Düz metin fallback **yoktur**.

## Diğer kurallar

- Log ve Git Console çıktısında URL içindeki kimlik bilgileri (`https://user:token@host`) maskelenir.
- Hata raporları ve telemetry (opt-in) repository içeriği, dosya yolu veya sır içermez.
- Ayarlar ekranı anahtarı yalnızca "kayıtlı / kayıtlı değil" olarak gösterir, değerini geri okumaz.
