# ADR-0006 — Uygulama metadata'sı için SQLite

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-16
- **Şema:** [architecture/sqlite-schema.md](../architecture/sqlite-schema.md)

## Karar

- Uygulama metadata'sı tek bir SQLite veritabanında tutulur: `<AppData>/ArChrono/archrono.db`.
- Erişim `Microsoft.Data.Sqlite` ile, ORM kullanılmadan, küçük ve açık SQL sorgularıyla yapılır.
  (ORM, şemanın kurtarma verisi olduğu bir üründe gereksiz soyutlama ve başlangıç maliyeti getirir.)
- `PRAGMA journal_mode=WAL`, `foreign_keys=ON`, `synchronous=NORMAL`, `busy_timeout=5000`.
- Şema sürümlemesi `PRAGMA user_version` + sıralı, yalnızca ileri yönlü migration script'leri.
- **Git repository SQLite'a taşınmaz.** Git nesneleri, ref'ler ve reflog Git'te kalır; SQLite yalnızca
  uygulamanın ürettiği bilgiyi (recovery point, snapshot manifest'i, işlem kaydı, AI denetim kaydı, ayarlar) tutar.
- **Hiçbir sır SQLite'a yazılmaz** (ADR-0007).

## Neden tek veritabanı?

- Content store tüm repository'ler arasında paylaşıldığı için referans sayımı global olmalı.
- Global arama (Recovery Center, snapshot'lar) tek sorguda yapılabilir.
- Repository başına ayrı DB, repository taşındığında/silindiğinde yetim veri üretir.

## Sonuçlar

- Veritabanı silinirse Git repository'leri etkilenmez; yalnızca uygulama geçmişi kaybolur.
  Content store `objects/` klasörü DB'den bağımsız doğrulanabilir (içerik adresli).
