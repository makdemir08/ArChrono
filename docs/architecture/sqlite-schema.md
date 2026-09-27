# SQLite Şeması

Karar gerekçesi: [ADR-0006](../adr/0006-metadata-storage-sqlite.md).
Kanonik şema: [`src/ArChrono.Storage/Migrations/0001_initial.sql`](../../src/ArChrono.Storage/Migrations/0001_initial.sql).

## İlişki diyagramı

```text
repository ─┬─< snapshot ─┬─< snapshot_entry >── content_blob (blob_id, mantıksal)
            │             └── pin_commit ──────── git_pin (common_dir, commit_sha)
            │
            ├─< recovery_point ─┬─< recovery_ref
            │        │          └─< recovery_stash
            │        └── snapshot_id ──► snapshot
            │
            ├─< git_operation ── before_point_id / after_point_id ──► recovery_point
            │                └── undo_of_operation_id ──► git_operation
            │
            ├─< workspace
            ├─< ai_request
            └─< ai_result_cache

setting (global anahtar/değer)
```

## Tablolar

| Tablo | Amaç | Not |
|---|---|---|
| `repository` | Açılmış çalışma dizinleri (worktree başına bir satır) | `common_dir` aynı repo'nun worktree'lerini gruplar |
| `content_blob` | Content store envanteri | `SUM(stored_size)` = kullanılan depolama |
| `snapshot` | Snapshot başlığı: HEAD, index ağacı, pin, parmak izi | `trigger`: operation/timer/manual/restore/shutdown |
| `snapshot_entry` | Overlay dosyaları | `(path, snapshot_id)` indeksi dosya zaman makinesini hızlandırır |
| `git_operation` | Uygulamanın yaptığı her işlem | undo zinciri `undo_of_operation_id` / `undone_by_operation_id` |
| `recovery_point` | Kurtarma noktası başlığı | `is_pinned` retention'dan korur |
| `recovery_ref` | Noktadaki ref hedefleri | `target_sha` indeksi pin temizliği için |
| `recovery_stash` | Noktadaki stash listesi | stash drop geri alma |
| `git_pin` | Repository içindeki `refs/archrono/pins/*` kaydı | retention ref'i ve satırı birlikte siler |
| `workspace` | Worktree + agent eşlemesi | Phase 2 UI |
| `ai_request` | AI denetim kaydı | istem metni yok, yalnızca SHA-256 ve yollar |
| `ai_result_cache` | AI açıklamalarının yerel önbelleği | repository silinince cascade |
| `setting` | JSON ayarlar | sır yok |

## Sorgu örnekleri

Dosyanın 10:15'teki hâli için snapshot:

```sql
SELECT s.id, s.index_tree, e.kind, e.blob_id
FROM snapshot s
LEFT JOIN snapshot_entry e ON e.snapshot_id = s.id AND e.path = @path
WHERE s.repository_id = @repo AND s.created_at <= @time
ORDER BY s.created_at DESC
LIMIT 1;
```

Referanssız blob'lar (sweep):

```sql
SELECT b.blob_id FROM content_blob b
WHERE NOT EXISTS (SELECT 1 FROM snapshot_entry e WHERE e.blob_id = b.blob_id);
```

Kullanılan depolama:

```sql
SELECT COALESCE(SUM(stored_size), 0) FROM content_blob;
```

## Migration kuralları

- `PRAGMA user_version` mevcut sürümü tutar; `Migrations/NNNN_*.sql` sırayla ve transaction içinde uygulanır.
- Migration'lar yalnızca ileri yönlüdür ve yayımlandıktan sonra değiştirilmez.
- Açılışta veritabanı yeni sürümden geliyorsa (`user_version` > bilinen) uygulama salt okunur modda uyarı verir.

## Gelecek (Phase 3)

Semantic search için ayrı migration ile:

```sql
CREATE TABLE embedding_chunk (
    id INTEGER PRIMARY KEY, repository_id INTEGER, source_kind TEXT, source_id TEXT,
    path TEXT, start_line INTEGER, end_line INTEGER, model TEXT, vector BLOB, created_at INTEGER);
```

`ISearchProvider` soyutlaması sayesinde lexical ve semantic sağlayıcılar aynı arama kutusunda birleşir.
