# ADR-0008 — Terminal ve agent çalışma alanları

- **Durum:** Kabul edildi (MVP kapsamı) / Phase 2 için taslak
- **Tarih:** 2026-09-16

## Bağlam

"CLI kullanımı engellenmemeli, terminal her zaman erişilebilir olmalı." Ayrıca Claude Code, Codex,
Cursor gibi CLI tabanlı agent'ların worktree'lerde paralel çalıştırılması hedefleniyor.

## Karar

### MVP
1. **Git Console paneli:** Uygulamanın çalıştırdığı her `git` komutu (argümanlar, süre, çıkış kodu, çıktı)
   canlı listelenir. Kullanıcı panelden etkileşimsiz `git ...` komutu çalıştırabilir. Bu komutlar
   uygulamanın dışında sayılır: durum değiştirirlerse reflog ve bir sonraki snapshot ile yakalanır.
2. **Open in Terminal:** Repository/worktree dizininde sistem terminali (macOS: Terminal veya iTerm;
   Windows: Windows Terminal, yoksa PowerShell) açılır. Kısayol: `Ctrl/Cmd+J`.

### Phase 2
1. **Gömülü PTY:** Windows'ta ConPTY (`CreatePseudoConsole`), macOS/Linux'ta `forkpty`; VT100/xterm
   ayrıştırıcı + Avalonia üzerinde hücre tabanlı çizim. `ITerminalSession` soyutlaması.
2. **Agent Workspace:** `workspace` tablosu worktree ile agent oturumunu eşler (agent türü, komut, durum).
   Her worktree için Time Machine ayrı çalışır; "Agent başlamadan önceki hâle dön" tek tıktır.
3. Agent komutları kullanıcının tanımladığı komut şablonlarıdır (`claude`, `codex`, `cursor-agent` …);
   uygulama agent'ı kendisi indirmez veya kurmaz.
