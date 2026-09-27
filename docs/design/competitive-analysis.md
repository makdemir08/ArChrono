# Rakip Analizi

> Amaç: rakiplerin özellik listesini kopyalamak değil, **hangi UX prensiplerini öğreneceğimizi** ve
> **nerede boşluk olduğunu** belirlemek. Rakip ürünlerin özellikleri sürümden sürüme değişir;
> buradaki değerlendirmeler Eylül 2026 itibarıyla genel ürün konumlandırmasını yansıtır ve
> pazarlama materyallerinde kullanılmadan önce ayrıca doğrulanmalıdır.

## 1. Klasik Git istemcileri

| | GitKraken | Tower | Fork |
|---|---|---|---|
| Teknoloji | Electron, libgit2 + git CLI | Platform başına native (iki kod tabanı) | Platform başına native (iki kod tabanı) |
| Güçlü yanı | Görsel commit grafiği, entegrasyonlar (PR/issue), ekip odaklı conflict prevention, AI özellikleri, workspace/agent yönetimi | "Undo everything" (Cmd+Z), drag & drop, conflict wizard, interactive rebase, reflog, worktree | Hız, sadelik, interactive rebase, conflict resolver, image diff, bisect, git-flow, LFS |
| Zayıf yanı | Bellek/başlangıç maliyeti, birçok özellik hesap ve bulut gerektirir | Abonelik; undo, Git'in kaydettiği durumla sınırlı | Güvenlik ağı büyük ölçüde reflog'a bırakılmış |
| Öğrenilecek prensip | Görsel yoğunluk ve keşfedilebilirlik | **Geri alma her yerde, kullanıcı korkmadan dener** | **Hız bir özelliktir; sade ekran, düşük gecikme** |

## 2. Konsept olarak en yakın referanslar

Bu ürünler klasik Git GUI değildir ama "hiçbir şeyi kaybetme" fikrinin kanıtlarıdır:

| Ürün | Fikir | Bizim için anlamı |
|---|---|---|
| **Jujutsu (jj)** | Operation log (`jj op log`, `jj undo`), çalışma kopyasının otomatik olarak commit gibi kaydedilmesi | Operasyon günlüğü + undo modeli doğrulanmış bir fikir; ancak kullanıcıdan yeni bir VCS öğrenmesini ister. Biz **Git'i değiştirmeden** aynı güveni veriyoruz. |
| **GitButler** | Virtual branches, operasyon geçmişi ve snapshot'lardan geri dönüş | Snapshot'ları Git ref'leriyle korumak pratik bir yaklaşım. Biz iş akışını değiştirmiyoruz, standart branch modeli kalıyor. |
| **JetBrains Local History** | VCS'ten bağımsız dosya geçmişi | "Bugün 10:15'te bu dosya nasıldı?" sorusunun IDE içindeki karşılığı. Biz bunu IDE'den bağımsız, repository seviyesinde ve Git durumuyla (HEAD, index, branch) ilişkili tutuyoruz. |
| **VS Code + GitLens** | Blame, satır geçmişi, commit graph | "Why did this change?" için satır → commit → PR zinciri iyi bir UX örneği. |
| **GitHub Desktop** | Yeni başlayanlar için sade terminoloji | Guided mode dili için referans, ancak ileri seviye işlemler eksik. |

## 3. Boşluk (fırsat)

Mevcut Git GUI'lerinin hiçbiri şu soruya tek ekranda cevap vermiyor:

> "Son iki saatte repository'mde ne oldu ve herhangi bir ana nasıl güvenle geri dönerim?"

- Reflog yalnızca **ref hareketlerini** tutar; commit edilmemiş değişiklikleri, index'i, silinmiş untracked dosyaları tutmaz.
- `git stash`/`git reset --hard`/`git clean` sonrası kaybolan içerik reflog'dan çoğu zaman geri gelmez.
- Undo özellikleri genelde uygulama oturumuyla ve belirli işlemlerle sınırlıdır.
- Local history IDE'ye bağlıdır; IDE kapandığında ya da başka bir editör/agent dosyayı değiştirdiğinde kapsam dışı kalır.
- AI coding agent'ları (Claude Code, Codex, Cursor) çalışma alanını hızla ve toplu değiştirir; kullanıcının "agent'tan önceki hâle dön" ihtiyacı büyüyor.

## 4. Konumlandırma

**ArChrono (kod adı: Git Next) — A Time Machine for Git.**

| Katman | Rakiplerle ilişki |
|---|---|
| Core (graph, diff, branch, merge, rebase, stash, reflog, blame, bisect) | **Eşitlik** hedeflenir; Fork'un hızı ve sadeliği ölçüt alınır. Daha fazlası hedeflenmez. |
| Safety (recovery point, undo, safe reset/rebase/force push) | **Farklılaştırıcı.** Tower'ın undo yaklaşımı her tehlikeli işleme genellenir ve uygulama oturumundan bağımsız, kalıcı hâle getirilir. |
| Time Machine (çalışma alanı snapshot'ları, dosya zaman makinesi) | **Farklılaştırıcı.** jj/GitButler fikrini standart Git iş akışını bozmadan sunar. |
| Intelligence (AI açıklama, commit mesajı, conflict açıklaması) | **Destekleyici.** Provider bağımsız, local-first; AI hiçbir zaman gerçek Git metadata'sının yerine geçmez. |
| Modern development (worktree, agent session) | **Destekleyici.** Agent'ın yaptığı her değişiklik Time Machine tarafından yakalanır → agent güvenliği. |

## 5. Tasarımda kopyalanmayacaklar

- GitKraken'ın koyu mor/neon grafik estetiği ve dairesel avatar düğümleri.
- Tower'ın sol kenar çubuğu + üç bölmeli macOS Mail benzeri düzeni birebir.
- Fork'un sekmeli repository listesi ve ikon dili.

ArChrono'nun görsel kimliği **zaman ekseni** üzerine kurulur: pencerenin altındaki **Chrono Strip**
(bugünkü recovery point ve snapshot'ların yatay zaman şeridi) ve üst çubuktaki **kalıcı Undo düğmesi**
ürünün imzasıdır. Renk dili: güvenli/geri alınabilir işlemler için teal, zaman/recovery için amber.
