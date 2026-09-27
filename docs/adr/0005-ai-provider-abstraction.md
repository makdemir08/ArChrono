# ADR-0005 — AI katmanı: provider bağımsızlığı ve local-first gizlilik

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-16

## Bağlam

AI özellikleri (commit mesajı, commit/branch/conflict açıklaması) değerli ama repository içeriğinin
dışarı gönderilmesi ciddi bir güven meselesi. Ürün tek bir sağlayıcıya bağlanmamalı.

## Karar

```text
UI
 │
AiService                 (özellikler: CommitMessage, ExplainCommit, ExplainBranch, ExplainConflict…)
 │
AiContextBuilder          (diff toplama, SensitivePathFilter, SecretRedactor, boyut bütçesi)
 │
AiPayloadPreview          (gönderilecek her şey: sağlayıcı, uç nokta, dosyalar, byte, tam metin)
 │
IAiProvider
 ├── OpenAiProvider           (Chat Completions)
 ├── AnthropicProvider        (Messages API)
 ├── OllamaProvider           (local /api/chat)
 ├── AzureOpenAiProvider      (deployment tabanlı)
 └── OpenAiCompatibleProvider (LM Studio, vLLM, OpenRouter, kurum içi gateway…)
```

### Gizlilik kuralları

1. **Varsayılan: AI kapalı.** Kullanıcı bir sağlayıcı seçip etkinleştirene kadar hiçbir ağ isteği yapılmaz.
2. **Önizleme:** "Always show what will be sent" varsayılan olarak açık. Önizleme; sağlayıcıyı, uç nokta
   host'unu, dahil edilen/hariç tutulan dosyaları, byte sayısını ve tam metni gösterir.
3. **Sensitive file exclusion:** `.env`, `.env.*`, `*.pem`, `*.key`, `*.p12`, `*.pfx`, `id_rsa*`, `id_ed25519*`,
   `secrets/`, `credentials/`, `*.keystore` varsayılan olarak hariçtir; liste kullanıcı tarafından düzenlenebilir.
4. **Secret redaction:** Hariç tutulmayan dosyalarda özel anahtar blokları, bilinen token biçimleri
   (AWS, GitHub, Slack, OpenAI/Anthropic benzeri anahtarlar) ve `password=`/`secret=` atamaları
   `[REDACTED]` ile değiştirilir; kaç satırın maskelendiği önizlemede gösterilir.
5. **Denetim kaydı:** `ai_request` tablosu sağlayıcıyı, modeli, host'u, gönderilen byte'ı ve **yalnızca dosya
   yollarını** tutar. İstem metninin kendisi saklanmaz (yalnızca SHA-256 özeti).
6. **API anahtarları** yalnızca OS keychain/credential store'da (ADR-0007).

### Doğruluk kuralları

1. AI çıktısı her yerde **"AI generated"** rozetiyle gösterilir; kaynak commit'ler yanında listelenir.
2. AI hiçbir zaman gerçek Git metadata'sının (yazar, tarih, sha, dosya listesi) yerine geçmez;
   bu alanlar her zaman Git'ten okunur ve AI bölümünden ayrı gösterilir.
3. AI'nin önerdiği hiçbir değişiklik kullanıcı onayı olmadan index'e alınmaz veya commit edilmez.
   Conflict önerileri yalnızca "Result" panelini doldurur; kabul kullanıcı eylemidir.
4. Bisect ve conflict tahmininde kesinlik iddiası yok: "Likely", "Confidence: Low/Medium/High".

## Sonuçlar

- Streaming yerine tek yanıt (MVP). Arayüz iptal edilebilir.
- Model adı serbest metin + öneriler; yeni model çıktığında uygulama güncellemesi gerekmez.
- Phase 3: lokal embedding modeli ile semantic search aynı provider soyutlamasını (`IEmbeddingProvider`) kullanır.
